using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ServiceStack.Configuration;
using ServiceStack.Templates;
using ServiceStack.Text;

namespace ServiceStack.Auth
{
    public abstract class OAuth2ProviderSync : OAuthProvider
    {
        public OAuth2ProviderSync(IAppSettings appSettings, string authRealm, string oAuthProvider) 
            : base(appSettings, authRealm, oAuthProvider) {}
        public OAuth2ProviderSync(IAppSettings appSettings, string authRealm, string oAuthProvider, string consumerKeyName, string consumerSecretName) 
            : base(appSettings, authRealm, oAuthProvider, consumerKeyName, consumerSecretName) {}

        protected string[] Scopes { get; set; }
        
        public string ResponseMode { get; set; }

        protected override void AssertValidState()
        {
            base.AssertValidState();
            
            AssertAuthorizeUrl();
            AssertAccessTokenUrl();
        }

        protected virtual void AssertAccessTokenUrl()
        {
            if (string.IsNullOrEmpty(AccessTokenUrl))
                throw new Exception($"oauth.{Provider}.{nameof(AccessTokenUrl)} is required");
        }

        protected virtual void AssertAuthorizeUrl()
        {
            if (string.IsNullOrEmpty(AuthorizeUrl))
                throw new Exception($"oauth.{Provider}.{nameof(AuthorizeUrl)} is required");
        }

        public override async Task<object> AuthenticateAsync(IServiceBase authService, IAuthSession session, Authenticate request, CancellationToken token = default)
        {
            var tokens = Init(authService, ref session, request);

            //Transferring AccessToken/Secret from Mobile/Desktop App to Server
            if (request?.AccessToken != null && VerifyAccessToken != null)
            {
                if (!VerifyAccessToken(request.AccessToken))
                    return HttpError.Unauthorized("AccessToken is not for client_id: " + ConsumerKey);

                var isHtml = authService.Request.IsHtml();
                var failedResult = await AuthenticateWithAccessTokenAsync(authService, session, tokens, request.AccessToken, token: token).ConfigAwait();
                if (failedResult != null)
                    return ConvertToClientError(failedResult, isHtml);

                return isHtml
                    ? authService.Redirect(SuccessRedirectUrlFilter(this, session.ReferrerUrl.SetParam("s", "1")))
                    : null; //return default AuthenticateResponse
            }

            var httpRequest = authService.Request;
            var error = httpRequest.GetQueryStringOrForm("error_reason")
                        ?? httpRequest.GetQueryStringOrForm("error")
                        ?? httpRequest.GetQueryStringOrForm("error_code")
                        ?? httpRequest.GetQueryStringOrForm("error_description");

            var hasError = !error.IsNullOrEmpty();
            if (hasError)
            {
                var httpParams = HttpUtils.HasRequestBody(httpRequest.Verb)
                    ? httpRequest.QueryString
                    : httpRequest.FormData;
                Log.Error($"OAuth2 Error callback. {httpParams}");
                return authService.Redirect(FailedRedirectUrlFilter(this, session.ReferrerUrl.SetParam("f", error)));
            }             
        
            var code = httpRequest.GetQueryStringOrForm(Keywords.Code);
            var isPreAuthCallback = !code.IsNullOrEmpty();
            if (!isPreAuthCallback)
            {
                var oauthstate = session.Id;                
                var preAuthUrl = AuthorizeUrl
                    .AddQueryParam("response_type", "code")
                    .AddQueryParam("client_id", ConsumerKey)
                    .AddQueryParam("redirect_uri", this.CallbackUrl)
                    .AddQueryParam("scope", string.Join(" ", Scopes))
                    .AddQueryParam(Keywords.State, oauthstate);

                if (ResponseMode != null)
                    preAuthUrl = preAuthUrl.AddQueryParam("response_mode", ResponseMode);
                        
                if (session is AuthUserSession authSession)
                    (authSession.Meta ?? (authSession.Meta = new Dictionary<string, string>()))["oauthstate"] = oauthstate;

                await this.SaveSessionAsync(authService, session, SessionExpiry, token).ConfigAwait();
                return authService.Redirect(PreAuthUrlFilter(this, preAuthUrl));
            }

            try
            {
                var state = httpRequest.GetQueryStringOrForm(Keywords.State);
                if (state != null && session is AuthUserSession authSession)
                {
                    if (authSession.Meta == null)
                        authSession.Meta = new Dictionary<string, string>();
                    
                    if (authSession.Meta.TryGetValue("oauthstate", out var oauthState) && state != oauthState)
                        return authService.Redirect(FailedRedirectUrlFilter(this, session.ReferrerUrl.SetParam("f", "InvalidState")));

                    authSession.Meta.Remove("oauthstate");
                }
                
                var contents = await GetAccessTokenJsonAsync(code, token).ConfigAwait();
                var authInfo = (Dictionary<string,object>)JSON.parse(contents);

                var accessToken = (string)authInfo["access_token"];

                var redirectUrl = SuccessRedirectUrlFilter(this, session.ReferrerUrl.SetParam("s", "1"));

                var errorResult = await AuthenticateWithAccessTokenAsync(authService, session, tokens, accessToken, authInfo, token).ConfigAwait();
                if (errorResult != null)
                    return errorResult;
                
                //Haz Access!

                if (HostContext.Config?.UseSameSiteCookies == true)
                {
                    // Workaround Set-Cookie HTTP Header not being honoured in 302 Redirects 
                    var redirectHtml = HtmlTemplates.GetHtmlRedirectTemplate(redirectUrl);
                    return new HttpResult(redirectHtml, MimeTypes.Html);
                }
                
                return authService.Redirect(redirectUrl); 
            }
            catch (WebException we)
            {
                var errorBody = await we.GetResponseBodyAsync(token).ConfigAwait();
                Log.Error($"Failed to get Access Token for '{Provider}': {errorBody}");
                
                var statusCode = ((HttpWebResponse)we.Response).StatusCode;
                if (statusCode == HttpStatusCode.BadRequest)
                {
                    return authService.Redirect(FailedRedirectUrlFilter(this, session.ReferrerUrl.SetParam("f", "AccessTokenFailed")));
                }
            }

            //Shouldn't get here
            return authService.Redirect(FailedRedirectUrlFilter(this, session.ReferrerUrl.SetParam("f", "Unknown")));
        }

        protected virtual async Task<string> GetAccessTokenJsonAsync(string code, CancellationToken token=default)
        {
            var accessTokenUrl = $"{AccessTokenUrl}?code={code}&client_id={ConsumerKey}&client_secret={ConsumerSecret}&redirect_uri={this.CallbackUrl.UrlEncode()}&grant_type=authorization_code";
            var contents = await AccessTokenUrlFilter(this, accessTokenUrl).PostToUrlAsync("").ConfigAwait();
            return contents;
        }

        protected virtual async Task<object> AuthenticateWithAccessTokenAsync(IServiceBase authService, IAuthSession session, IAuthTokens tokens, 
            string accessToken, Dictionary<string,object> authInfo = null, CancellationToken token=default)
        {
            tokens.AccessToken = accessToken;
            if (authInfo != null)
            {
                tokens.Items ??= new Dictionary<string, string>();
                foreach (var entry in authInfo)
                {
                    tokens.Items[entry.Key] = entry.Value?.ToString();
                }
            }

            var accessTokenAuthInfo = await this.CreateAuthInfoAsync(accessToken, token).ConfigAwait();

            session.IsAuthenticated = true;

            return await OnAuthenticatedAsync(authService, session, tokens, accessTokenAuthInfo, token).ConfigAwait();
        }

        protected abstract Task<Dictionary<string, string>> CreateAuthInfoAsync(string accessToken, CancellationToken token=default);

        /// <summary>
        /// Override to return User chosen username or Email for this AuthProvider
        /// </summary>
        protected virtual string GetUserAuthName(IAuthTokens tokens, Dictionary<string, string> authInfo) =>
            tokens.Email;

        protected override Task LoadUserAuthInfoAsync(AuthUserSession userSession, IAuthTokens tokens, Dictionary<string, string> authInfo, CancellationToken token=default)
        {
            try
            {
                tokens.UserId = authInfo.Get("user_id");
                tokens.UserName = authInfo.Get("username") ?? tokens.UserId;
                tokens.DisplayName = authInfo.Get("name");
                tokens.FirstName = authInfo.Get("first_name");
                tokens.LastName = authInfo.Get("last_name");
                tokens.Email = authInfo.Get("email");
                userSession.UserAuthName = GetUserAuthName(tokens, authInfo);

                if (authInfo.TryGetValue(AuthMetadataProvider.ProfileUrlKey, out var profileUrl))
                {
                    if (string.IsNullOrEmpty(userSession.ProfileUrl))
                        userSession.ProfileUrl = profileUrl.SanitizeOAuthUrl();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Could not retrieve '{Provider}' profile user info for '{tokens.DisplayName}'", ex);
            }

            LoadUserOAuthProvider(userSession, tokens);
            return TypeConstants.EmptyTask;
        }

        public Func<IAuthSession,IAuthTokens, string> ResolveUnknownDisplayName { get; set; }
        
        public override void LoadUserOAuthProvider(IAuthSession authSession, IAuthTokens tokens)
        {
            if (!(authSession is AuthUserSession userSession))
                return;

            userSession.UserName = tokens.UserName ?? userSession.UserName;
            userSession.DisplayName = tokens.DisplayName ?? userSession.DisplayName;
            userSession.FirstName = tokens.FirstName ?? userSession.FirstName;
            userSession.LastName = tokens.LastName ?? userSession.LastName;
            userSession.Email = userSession.PrimaryEmail = tokens.Email ?? userSession.PrimaryEmail ?? userSession.Email;
            
            if (userSession.DisplayName == null && ResolveUnknownDisplayName != null)
                userSession.DisplayName = ResolveUnknownDisplayName(authSession, tokens);
        }

    }
}