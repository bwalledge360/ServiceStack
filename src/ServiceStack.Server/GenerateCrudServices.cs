using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ServiceStack.Auth;
using ServiceStack.Caching;
using ServiceStack.Configuration;
using ServiceStack.Data;
using ServiceStack.DataAnnotations;
using ServiceStack.FluentValidation.Internal;
using ServiceStack.Host;
using ServiceStack.NativeTypes;
using ServiceStack.NativeTypes.CSharp;
using ServiceStack.NativeTypes.Dart;
using ServiceStack.NativeTypes.FSharp;
using ServiceStack.NativeTypes.Java;
using ServiceStack.NativeTypes.Kotlin;
using ServiceStack.NativeTypes.Swift;
using ServiceStack.NativeTypes.TypeScript;
using ServiceStack.NativeTypes.VbNet;
using ServiceStack.OrmLite;
using ServiceStack.Web;

namespace ServiceStack
{
    //TODO: persist AutoCrud
    public class GenerateCrudServices : IGenerateCrudServices
    {
        public List<string> IncludeCrudOperations { get; set; } = new List<string> {
            AutoCrudOperation.Query,
            AutoCrudOperation.Create,
            AutoCrudOperation.Update,
            AutoCrudOperation.Patch,
            AutoCrudOperation.Delete,
        };

        /// <summary>
        /// Generate services 
        /// </summary>
        public List<CreateCrudServices> CreateServices { get; set; } = new List<CreateCrudServices>();
        
        public Action<MetadataTypes, MetadataTypesConfig, IRequest> MetadataTypesFilter { get; set; }
        public Action<MetadataType, IRequest> TypeFilter { get; set; }
        public Action<MetadataOperationType, IRequest> ServiceFilter { get; set; }
        
        public Func<MetadataType, bool> IncludeType { get; set; }
        public Func<MetadataOperationType, bool> IncludeService { get; set; }
        
        public string AccessRole { get; set; } = RoleNames.Admin;
        
        internal ConcurrentDictionary<Tuple<string,string>, DbSchema> CachedDbSchemas { get; } 
            = new ConcurrentDictionary<Tuple<string,string>, DbSchema>();

        public Func<ColumnSchema, IOrmLiteDialectProvider, Type> ResolveColumnType { get; set; }
        
        public Type DefaultResolveColumnType(ColumnSchema column, IOrmLiteDialectProvider dialect)
        {
            var dataType = column.DataType;
            if (dataType == null)
                return null;
            
            if (dataType == typeof(string) && column.ColumnSize == 1)
                return typeof(char);

            if (dataType == typeof(Array))
            {
                return column.DataTypeName switch 
                {
                    "hstore" => typeof(Dictionary<string,string>),
                    "bytea" => typeof(byte[]),
                    "text[]" => typeof(string[]),
                    "int[]" => typeof(int[]),
                    "short[]" => typeof(short[]),
                    "bigint[]" => typeof(long[]),
                    "real[]" => typeof(float[]),
                    "double precision[]" => typeof(double[]),
                    "numeric[]" => typeof(decimal[]),
                    "time[]" => typeof(DateTime[]),
                    "timestamp[]" => typeof(DateTime[]),
                    "timestamptz[]" => typeof(DateTimeOffset[]),
                    "timestamp with time zone[]" => typeof(DateTimeOffset[]),
                    _ => null,
                };
            }

            return dataType;
        }

        public Dictionary<Type, string[]> ServiceRoutes { get; set; } = new Dictionary<Type, string[]> {
            { typeof(CrudCodeGenTypesService), new[]
            {
                "/" + "crud".Localize() + "/{Include}/{Lang}",
            } },
            { typeof(CrudTablesService), new[] {
                "/" + "crud".Localize() + "/" + "tables".Localize(),
                "/" + "crud".Localize() + "/" + "tables".Localize() + "/{Schema}",
            } },
        };

        private const string NoSchema = "__noschema";

        public GenerateCrudServices()
        {
            ResolveColumnType = DefaultResolveColumnType;
        }

        public void Register(IAppHost appHost)
        {
            if (AccessRole != null)
            {
                appHost.RegisterServices(ServiceRoutes);
            }
        }

        static string ResolveRequestType(Dictionary<string, MetadataType> existingTypesMap, MetadataOperationType op, CrudCodeGenTypes requestDto,
            out string modifier)
        {
            modifier = null;
            if (existingTypesMap.ContainsKey(op.Request.Name))
            {
                // Cannot use more refined Request DTO, skip...
                if (requestDto.NamedConnection == null && requestDto.Schema == null)
                    return null;
                        
                var method = op.Request.Name.SplitPascalCase().LeftPart(' ');
                var suffix = op.Request.Name.Substring(method.Length);
                var newRequestName = method + (modifier = StringUtils.SnakeCaseToPascalCase(requestDto.NamedConnection ?? requestDto.Schema)) + suffix;
                if (existingTypesMap.ContainsKey(newRequestName))
                {
                    if (requestDto.NamedConnection == null || requestDto.Schema == null)
                        return null;
                            
                    newRequestName = method + (modifier = StringUtils.SnakeCaseToPascalCase(requestDto.Schema)) + suffix;
                    if (existingTypesMap.ContainsKey(newRequestName))
                    {
                        newRequestName = method + (modifier = StringUtils.SnakeCaseToPascalCase(requestDto.NamedConnection + requestDto.Schema)) + suffix;
                        if (existingTypesMap.ContainsKey(newRequestName))
                            return null;
                    }
                }
                return newRequestName;
            }
            return op.Request.Name;
        }

        public List<Type> GenerateMissingServices(AutoQueryFeature feature)
        {
            var types = new List<Type>();
            // return types;

            var assemblyName = new AssemblyName { Name = "tmpCrudAssembly" };
            var dynModule = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run)
                .DefineDynamicModule("tmpCrudModule");

            var metadataTypes = GetMissingTypesToCreate(out var existingMetaTypesMap);
            var generatedTypes = new Dictionary<Tuple<string,string>, Type>();

            var appHost = HostContext.AppHost;
            var dtoTypes = new HashSet<Type>();
            foreach (var dtoType in appHost.Metadata.GetAllDtos())
            {
                dtoTypes.Add(dtoType);
            }
            foreach (var assembly in feature.LoadFromAssemblies)
            {
                try
                {
                    var asmAqTypes = assembly.GetTypes().Where(x =>
                        x.HasInterface(typeof(IQueryDb)) || x.HasInterface(typeof(ICrud)));

                    foreach (var aqType in asmAqTypes)
                    {
                        ServiceMetadata.AddReferencedTypes(dtoTypes, aqType);
                    }
                }
                catch (Exception ex)
                {
                    appHost.NotifyStartupException(ex);
                }
            }
            foreach (var dtoType in dtoTypes)
            {
                generatedTypes[key(dtoType)] = dtoType;
            }

            foreach (var metaType in metadataTypes)
            {
                try 
                {
                    var newType = CreateOrGetType(dynModule, metaType, metadataTypes, existingMetaTypesMap, generatedTypes);
                    types.Add(newType);
                }
                catch (Exception ex)
                {
                    appHost.NotifyStartupException(ex);
                }
            }

            return types;
        }

        private static Type CreateOrGetType(ModuleBuilder dynModule, MetadataTypeName metaTypeRef,
            List<MetadataType> metadataTypes, Dictionary<Tuple<string, string>, MetadataType> existingMetaTypesMap, 
            Dictionary<Tuple<string, string>, Type> generatedTypes)
        {
            var typeKey = key(metaTypeRef);
            var type = ResolveType(typeKey, generatedTypes);
            if (type != null)
                return type;

            if (existingMetaTypesMap.TryGetValue(typeKey, out var metaType))
                return CreateOrGetType(dynModule, metaType, metadataTypes, existingMetaTypesMap, generatedTypes);

            ThrowCouldNotResolveType(metaTypeRef.Namespace + '.' + metaTypeRef.Name);
            return null;
        }

        private static Type CreateOrGetType(ModuleBuilder dynModule, string typeName,
            List<MetadataType> metadataTypes, Dictionary<Tuple<string, string>, MetadataType> existingMetaTypesMap, 
            Dictionary<Tuple<string, string>, Type> generatedTypes)
        {
            var type = ResolveType(typeName, generatedTypes);
            if (type != null)
                return type;

            foreach (var entry in existingMetaTypesMap)
            {
                if (entry.Value.Name == typeName)
                    return CreateOrGetType(dynModule, entry.Value, metadataTypes, existingMetaTypesMap, generatedTypes);
            }

            ThrowCouldNotResolveType(typeName);
            return null;
        }

        private static void ThrowCouldNotResolveType(string name) =>
            throw new NotSupportedException($"Could not resolve type '{name}'");

        private static readonly Dictionary<string, Type> ServiceStackModelTypes = new Dictionary<string, Type> {
            { nameof(UserAuth), typeof(UserAuth) },
            { nameof(IUserAuth), typeof(IUserAuth) },                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             
            { nameof(UserAuthDetails), typeof(UserAuthDetails) },
            { nameof(IUserAuthDetails), typeof(IUserAuthDetails) },
            { nameof(UserAuthRole), typeof(UserAuthRole) },
            { nameof(AuthUserSession), typeof(AuthUserSession) },
            { nameof(IAuthSession), typeof(IAuthSession) },
            { nameof(CrudEvent), typeof(CrudEvent) },
            { nameof(CacheEntry), typeof(CacheEntry) },
            { nameof(ValidationRule), typeof(ValidationRule) },
            { nameof(RequestLogEntry), typeof(RequestLogEntry) },
        };
        
        private static Type ResolveType(string typeName, Dictionary<Tuple<string, string>, Type> generatedTypes)
        {
            if (ServiceStackModelTypes.TryGetValue(typeName, out var ssType))
                return ssType;

            foreach (var entry in generatedTypes)
            {
                if (entry.Key.Item2 == typeName)
                    return entry.Value;
            }
            
            var ssInterfacesType = typeof(IQuery).Assembly.GetTypes().FirstOrDefault(x => x.Name == typeName);
            if (ssInterfacesType != null)
                return ssInterfacesType;

            var ssClientType = typeof(Authenticate).Assembly.GetTypes().FirstOrDefault(x => x.Name == typeName);
            if (ssClientType != null)
                return ssClientType;
            
            return HostContext.AppHost.ScriptContext.ProtectedMethods.@typeof(typeName);
        }

        private static Type AssertResolveType(string typeName, Dictionary<Tuple<string, string>, Type> generatedTypes)
        {
            var ret = ResolveType(typeName, generatedTypes);
            if (ret == null)
                ThrowCouldNotResolveType(typeName);
            return ret;
        }

        private static Type ResolveType(Tuple<string,string> typeKey, Dictionary<Tuple<string, string>, Type> generatedTypes)
        {
            var hasNs = typeKey.Item1 != null;
            if (!hasNs)
                return ResolveType(typeKey.Item2, generatedTypes);
            
            if (generatedTypes.TryGetValue(typeKey, out var existingType))
                return existingType;

            var fullTypeName = typeKey.Item1 + "." + typeKey.Item2;
            if (fullTypeName.StartsWith("ServiceStack"))
            {
                var ssInterfacesType = typeof(IQuery).Assembly.GetType(fullTypeName);
                if (ssInterfacesType != null)
                    return ssInterfacesType;

                var ssClientType = typeof(Authenticate).Assembly.GetType(fullTypeName);
                if (ssClientType != null)
                    return ssClientType;
            }

            var appType = Type.GetType(fullTypeName);
            if (appType != null)
                return appType;

            return HostContext.AppHost.ScriptContext.ProtectedMethods.@typeof(fullTypeName);
        }

        private static Type AssertResolveType(Tuple<string, string> typeKey, Dictionary<Tuple<string, string>, Type> generatedTypes)
        {
            var hasNs = typeKey.Item1 != null;
            if (!hasNs)
                return AssertResolveType(typeKey.Item2, generatedTypes);
                
            var ret = ResolveType(typeKey, generatedTypes);
            if (ret == null)
                ThrowCouldNotResolveType(typeKey.Item1 + '.' + typeKey.Item2);
            return ret;
        }

        private static Dictionary<string, Type> attributesMap;
        static Dictionary<string, Type> AttributesMap
        {
            get
            {
                if (attributesMap != null)
                    return attributesMap;
            
                attributesMap = new Dictionary<string, Type>();

                foreach (var type in typeof(RouteAttribute).Assembly.GetTypes())
                {
                    if (typeof(Attribute).IsAssignableFrom(type))
                        attributesMap[StringUtils.RemoveSuffix(type.Name, "Attribute")] = type;
                }

                foreach (var type in typeof(AuthenticateAttribute).Assembly.GetTypes())
                {
                    if (typeof(Attribute).IsAssignableFrom(type))
                        attributesMap[StringUtils.RemoveSuffix(type.Name, "Attribute")] = type;
                }

                return attributesMap;
            }
        }

        private static Type CreateOrGetType(ModuleBuilder dynModule, MetadataType metaType, 
            List<MetadataType> metadataTypes, Dictionary<Tuple<string, string>, MetadataType> existingMetaTypesMap, 
            Dictionary<Tuple<string, string>, Type> generatedTypes)
        {
            var typeKey = key(metaType);
            var existingType = ResolveType(typeKey, generatedTypes);
            if (existingType != null)
                return existingType;

            var baseType = metaType.Inherits != null
                ? CreateOrGetType(dynModule, metaType.Inherits, metadataTypes, existingMetaTypesMap, generatedTypes)
                : null;

            if (baseType?.IsGenericTypeDefinition == true)
            {
                var argTypes = new List<Type>();
                foreach (var typeName in metaType.Inherits.GenericArgs)
                {
                    var argType = CreateOrGetType(dynModule, typeName, metadataTypes, existingMetaTypesMap, generatedTypes);
                    argTypes.Add(argType);
                }
                baseType = baseType.MakeGenericType(argTypes.ToArray());
            }

            var interfaceTypes = new List<Type>();
            var returnMarker = metaType.RequestType?.ReturnType;
            if (returnMarker != null && returnMarker.Name != "QueryResponse`1")
            {
                var responseType = CreateOrGetType(dynModule, returnMarker, metadataTypes, existingMetaTypesMap, generatedTypes);
                if (responseType == null)
                    ThrowCouldNotResolveType(returnMarker.Name);
                
                interfaceTypes.Add(typeof(IReturn<>).MakeGenericType(responseType));
            }
            else if (metaType.RequestType?.ReturnsVoid == true)
            {
                interfaceTypes.Add(typeof(IReturnVoid));
            }
            
            foreach (var metaIface in metaType.Implements.Safe())
            {
                var ifaceType = CreateOrGetType(dynModule, metaIface, metadataTypes, existingMetaTypesMap, generatedTypes);
                if (ifaceType.IsGenericTypeDefinition)
                {
                    var argTypes = new List<Type>();
                    foreach (var typeName in metaIface.GenericArgs.Safe())
                    {
                        var argType = CreateOrGetType(dynModule, typeName, metadataTypes, existingMetaTypesMap, generatedTypes);
                        argTypes.Add(argType);
                    }
                    ifaceType = ifaceType.MakeGenericType(argTypes.ToArray());
                }
                interfaceTypes.Add(ifaceType);
            }
            
            var typeBuilder = dynModule.DefineType(metaType.Namespace + "." + metaType.Name,
                TypeAttributes.Public | TypeAttributes.Class, baseType, interfaceTypes.ToArray());

            foreach (var metaAttr in metaType.Attributes.Safe())
            {
                var attrBuilder = CreateCustomAttributeBuilder(metaAttr, generatedTypes);
                typeBuilder.SetCustomAttribute(attrBuilder);
            }

            foreach (var metaProp in metaType.Properties.Safe())
            {
                var returnType = metaProp.PropertyType ??
                    AssertResolveType(keyNs(metaProp.TypeNamespace, metaProp.Type), generatedTypes);
                var propBuilder = typeBuilder.DefineProperty(metaProp.Name, PropertyAttributes.HasDefault,
                    CallingConventions.Any, returnType, null);
                
                foreach (var metaAttr in metaProp.Attributes.Safe())
                {
                    var attrBuilder = CreateCustomAttributeBuilder(metaAttr, generatedTypes);
                    propBuilder.SetCustomAttribute(attrBuilder);
                }

                CreatePublicPropertyBody(typeBuilder, propBuilder);
            }
            
            var newType = typeBuilder.CreateTypeInfo().AsType();
            generatedTypes[key(newType)] = newType;
            return newType;
        }
        
        private static void CreatePublicPropertyBody(TypeBuilder typeBuilder, PropertyBuilder propertyBuilder)
        {
            const MethodAttributes attributes = 
                MethodAttributes.Public | 
                MethodAttributes.HideBySig | 
                MethodAttributes.SpecialName | 
                MethodAttributes.Virtual |
                MethodAttributes.Final;
            
            var fb = typeBuilder.DefineField("_" + propertyBuilder.Name.ToCamelCase(), propertyBuilder.PropertyType, FieldAttributes.Private);

            var getterBuilder = typeBuilder.DefineMethod("get_" + propertyBuilder.Name, attributes, propertyBuilder.PropertyType, Type.EmptyTypes);

            // Code generation
            var ilgen = getterBuilder.GetILGenerator();

            ilgen.Emit(OpCodes.Ldarg_0);
            ilgen.Emit(OpCodes.Ldfld, fb); // returning the firstname field
            ilgen.Emit(OpCodes.Ret);
            
            var setterBuilder = typeBuilder.DefineMethod("set_" + propertyBuilder.Name,
                attributes, null, new[] { propertyBuilder.PropertyType });

            ilgen = setterBuilder.GetILGenerator();

            ilgen.Emit(OpCodes.Ldarg_0);
            ilgen.Emit(OpCodes.Ldarg_1);
            ilgen.Emit(OpCodes.Stfld, fb);
            ilgen.Emit(OpCodes.Ret);

            propertyBuilder.SetGetMethod(getterBuilder);
            propertyBuilder.SetSetMethod(setterBuilder);
        }

        private static CustomAttributeBuilder CreateCustomAttributeBuilder(MetadataAttribute metaAttr,
            Dictionary<Tuple<string, string>, Type> generatedTypes)
        {
            var attrType = metaAttr.Attribute?.GetType();
            if (attrType == null && !AttributesMap.TryGetValue(metaAttr.Name, out attrType))
                throw new NotSupportedException($"Could not resolve Attribute '{metaAttr.Name}'");

            var args = new List<object>();
            ConstructorInfo ciAttr;
            var useCtorArgs = metaAttr.ConstructorArgs?.Count > 0; 
            if (useCtorArgs)
            {
                var argCount = metaAttr.ConstructorArgs.Count;
                ciAttr = attrType.GetConstructors().FirstOrDefault(ci => ci.GetParameters().Length == argCount)
                         ?? throw new NotSupportedException(
                             $"Could not resolve Attribute '{metaAttr.Name}' Constructor with {argCount} parameters");

                foreach (var argType in metaAttr.ConstructorArgs)
                {
                    var ctorAttrType = ResolveType(argType.Type, generatedTypes);
                    var argValue = argType.Value.ConvertTo(ctorAttrType);
                    args.Add(argValue);
                }
                var attrBuilder = new CustomAttributeBuilder(ciAttr, args.ToArray());
                return attrBuilder;
            }
            else
            {
                ciAttr = attrType.GetConstructors().FirstOrDefault(ci => ci.GetParameters().Length == 0)
                         ?? throw new NotSupportedException($"Attribute '{metaAttr.Name}' does not have a default Constructor");
                
                var propInfos = new List<PropertyInfo>();
                if (metaAttr.Args != null)
                {
                    foreach (var argType in metaAttr.Args)
                    {
                        propInfos.Add(attrType.GetProperty(argType.Name));
                        var piAttrType = ResolveType(argType.Type, generatedTypes);
                        var argValue = argType.Value.ConvertTo(piAttrType);
                        args.Add(argValue);
                    }
                }

                var attrBuilder = new CustomAttributeBuilder(ciAttr, TypeConstants.EmptyObjectArray, propInfos.ToArray(), args.ToArray());
                return attrBuilder;
            }
        }

        string ResolveModelType(MetadataOperationType op)
        {
            var baseGenericArg = op.Request.Inherits?.GenericArgs?.FirstOrDefault(); //e.g. QueryDb<From> or base class
            if (baseGenericArg != null)
                return baseGenericArg;

            var interfaceArg = op.Request.Implements?.Where(x => x.GenericArgs?.Length > 0).FirstOrDefault()?.GenericArgs[0];
            return interfaceArg;
        }

        void ReplaceModelType(MetadataTypes metadataTypes, string fromType, string toType)
        {
            foreach (var op in metadataTypes.Operations)
            {
                ReplaceModelType(op.Request, fromType, toType);
                ReplaceModelType(op.Response, fromType, toType);
            }
            foreach (var metadataType in metadataTypes.Types)
            {
                ReplaceModelType(metadataType, fromType, toType);
            }
        }

        void ReplaceModelType(MetadataType metadataType, string fromType, string toType)
        {
            if (metadataType == null)
                return;

            static void ReplaceGenericArgs(string[] genericArgs, string fromType, string toType)
            {
                if (genericArgs == null) 
                    return;
                
                for (var i = 0; i < genericArgs.Length; i++)
                {
                    var arg = genericArgs[i];
                    if (arg == fromType)
                        genericArgs[i] = toType;
                }
            }

            if (metadataType.Name == fromType)
            {
                //need to preserve DB Name if changing Type Name
                var dbTableName = metadataType.Items != null && metadataType.Items.TryGetValue(nameof(TableSchema), out var oSchema)
                        && oSchema is TableSchema schema
                    ? schema.Name
                    : metadataType.Name;
                    
                metadataType.AddAttributeIfNotExists(new AliasAttribute(dbTableName));
                metadataType.Name = toType;
            }
            
            if (metadataType.Inherits != null)
            {
                if (metadataType.Inherits.Name == fromType)
                    metadataType.Inherits.Name = toType;
                var genericArgs = metadataType.Inherits.GenericArgs;
                ReplaceGenericArgs(genericArgs, fromType, toType);
            }
            foreach (var iface in metadataType.Implements.Safe())
            {
                ReplaceGenericArgs(iface.GenericArgs, fromType, toType);
            }
        }

        internal List<MetadataType> GetMissingTypesToCreate(out Dictionary<Tuple<string, string>, MetadataType> typesMap)
        {
            var existingTypesMap = new Dictionary<string, MetadataType>();
            var existingExactTypesMap = new Dictionary<Tuple<string, string>, MetadataType>();
            var existingTypes = new List<MetadataType>();

            void addType(MetadataType type)
            {
                existingTypes.Add(type);
                existingTypesMap[type.Name] = type;
                existingExactTypesMap[key(type)] = type;
            }

            foreach (var genInstruction in CreateServices)
            {
                var requestDto = genInstruction.ConvertTo<CrudCodeGenTypes>();
                requestDto.Include = "new";
                
                var req = new BasicRequest(requestDto);
                var ret = ResolveMetadataTypes(requestDto, req);
                foreach (var op in ret.Item1.Operations)
                {
                    var requestName = ResolveRequestType(existingTypesMap, op, requestDto, out var modifier);
                    if (requestName == null)
                        continue;
                    if (op.Request.Name != requestName)
                    {
                        op.Request.Name = requestName;
                        foreach (var route in op.Routes.Safe())
                        {
                            route.Path = ("/" + modifier + "/" + route.Path.Substring(1)).ToLower();
                        }

                        var modelType = ResolveModelType(op);
                        if (modelType != null)
                        {
                            ReplaceModelType(ret.Item1, modelType, modifier + modelType);
                        }
                    }

                    foreach (var route in op.Routes.Safe())
                    {
                        op.Request.AddAttribute(new RouteAttribute(route.Path, route.Verbs));
                    }

                    addType(op.Request);

                    if (op.Response != null && !existingExactTypesMap.ContainsKey(key(op.Response)))
                        addType(op.Response);
                }

                foreach (var metaType in ret.Item1.Types)
                {
                    if (!existingExactTypesMap.ContainsKey(key(metaType)))
                        addType(metaType);
                }
            }

            var orderedTypes = existingTypes
                .OrderBy(x => x.Namespace)
                .ThenBy(x => x.Name)
                .ToList();

            typesMap = existingExactTypesMap;
            return orderedTypes;
        }

        public DbSchema GetCachedDbSchema(IDbConnectionFactory dbFactory, string schema=null, string namedConnection=null)
        {
            var key = new Tuple<string, string>(schema ?? NoSchema, namedConnection);
            return CachedDbSchemas.GetOrAdd(key, k => {

                var tables = GetTableSchemas(dbFactory, schema, namedConnection);
                return new DbSchema {
                    Schema = schema,
                    NamedConnection = namedConnection,
                    Tables = tables,
                };
            });
        }

        public static List<TableSchema> GetTableSchemas(IDbConnectionFactory dbFactory, string schema=null, string namedConnection=null)
        {
            using var db = namedConnection != null
                ? dbFactory.OpenDbConnection(namedConnection)
                : dbFactory.OpenDbConnection();

            var tables = db.GetTableNames(schema);
            var results = new List<TableSchema>();

            var dialect = db.GetDialectProvider();
            foreach (var table in tables)
            {
                var to = new TableSchema {
                    Name = table,
                };

                try
                {
                    var quotedTable = dialect.GetQuotedTableName(table, schema);
                    to.Columns = db.GetTableColumns($"SELECT * FROM {quotedTable}");
                }
                catch (Exception e)
                {
                    to.ErrorType = e.GetType().Name;
                    to.ErrorMessage = e.Message;
                }

                results.Add(to);
            }

            return results;
        }


        public static string GenerateSourceCode(IRequest req, CrudCodeGenTypes request, 
            MetadataTypesConfig typesConfig, MetadataTypes crudMetadataTypes)
        {
            var metadata = req.Resolve<INativeTypesMetadata>();
            var src = request.Lang switch {
                "csharp" => new CSharpGenerator(typesConfig).GetCode(crudMetadataTypes, req),
                "fsharp" => new FSharpGenerator(typesConfig).GetCode(crudMetadataTypes, req),
                "vbnet" => new VbNetGenerator(typesConfig).GetCode(crudMetadataTypes, req),
                "typescript" => new TypeScriptGenerator(typesConfig).GetCode(crudMetadataTypes, req, metadata),
                "dart" => new DartGenerator(typesConfig).GetCode(crudMetadataTypes, req, metadata),
                "swift" => new SwiftGenerator(typesConfig).GetCode(crudMetadataTypes, req),
                "java" => new JavaGenerator(typesConfig).GetCode(crudMetadataTypes, req, metadata),
                "kotlin" => new KotlinGenerator(typesConfig).GetCode(crudMetadataTypes, req, metadata),
                "typescript.d" => new FSharpGenerator(typesConfig).GetCode(crudMetadataTypes, req),
                _ => throw new NotSupportedException($"Unknown language '{request.Lang}', Supported languages: " +
                                                     $"csharp, typescript, dart, swift, java, kotlin, vbnet, fsharp")
            };
            return src;
        }

        public static string GenerateSource(IRequest req, CrudCodeGenTypes request)
        {
            var ret = ResolveMetadataTypes(request, req);
            var crudMetadataTypes = ret.Item1;
            var typesConfig = ret.Item2;
            
            if (request.Lang == "typescript")
            {
                typesConfig.MakePropertiesOptional = false;
                typesConfig.ExportAsTypes = true;
            }
            else if (request.Lang == "typescript.d")
            {
                typesConfig.MakePropertiesOptional = true;
            }

            var src = GenerateSourceCode(req, request, typesConfig, crudMetadataTypes);
            return src;
        }

        static Tuple<string, string> key(Type type) => new Tuple<string, string>(type.Namespace, type.Name);
        static Tuple<string, string> key(MetadataType type) => new Tuple<string, string>(type.Namespace, type.Name);
        static Tuple<string, string> key(MetadataTypeName type) => new Tuple<string, string>(type.Namespace, type.Name);
        static Tuple<string, string> keyNs(string ns, string name) => new Tuple<string, string>(ns, name);

        public static Tuple<MetadataTypes, MetadataTypesConfig> ResolveMetadataTypes(CrudCodeGenTypes request, IRequest req)
        {
            if (string.IsNullOrEmpty(request.Include))
                throw new ArgumentNullException(nameof(request.Include));
            if (request.Include != "all" && request.Include != "new")
                throw new ArgumentException(
                    "'Include' must be either 'all' to include all AutoQuery Services or 'new' to include only missing Services and Types", 
                    nameof(request.Include));
            
            var metadata = req.Resolve<INativeTypesMetadata>();
            var genServices = HostContext.AssertPlugin<AutoQueryFeature>().GenerateCrudServices;

            var dbFactory = req.TryResolve<IDbConnectionFactory>();
            var results = request.NoCache == true 
                ? GetTableSchemas(dbFactory, request.Schema, request.NamedConnection)
                : genServices.GetCachedDbSchema(dbFactory, request.Schema, request.NamedConnection).Tables;

            var appHost = HostContext.AppHost;
            request.BaseUrl ??= HostContext.GetPlugin<NativeTypesFeature>().MetadataTypesConfig.BaseUrl ?? appHost.GetBaseUrl(req);
            var typesConfig = metadata.GetConfig(request);
            if (request.MakePartial == null)
                typesConfig.MakePartial = false;
            if (request.MakeVirtual == null)
                typesConfig.MakeVirtual = false;
            if (request.InitializeCollections == null)
                typesConfig.InitializeCollections = false;
            if (request.AddDataContractAttributes == null) //required for gRPC
                typesConfig.AddDataContractAttributes = true;
            if (request.AddIndexesToDataMembers == null) //required for gRPC
                typesConfig.AddIndexesToDataMembers = true;
            if (string.IsNullOrEmpty(request.AddDefaultXmlNamespace))
                typesConfig.AddDefaultXmlNamespace = null;

            typesConfig.UsePath = req.PathInfo;
            var metadataTypes = metadata.GetMetadataTypes(req, typesConfig);
            var serviceModelNs = appHost.GetType().Namespace + ".ServiceModel";
            var typesNs = serviceModelNs + ".Types";

            //Put NamedConnections Types in their own Namespace
            if (request.NamedConnection != null)
            {
                serviceModelNs += "." + StringUtils.SnakeCaseToPascalCase(request.NamedConnection);
                typesNs = serviceModelNs;
            }
            
            metadataTypes.Namespaces.Add(serviceModelNs);
            metadataTypes.Namespaces.Add(typesNs);
            metadataTypes.Namespaces = metadataTypes.Namespaces.Distinct().ToList();
            
            var crudVerbs = new Dictionary<string,string> {
                { AutoCrudOperation.Query, HttpMethods.Get },
                { AutoCrudOperation.Create, HttpMethods.Post },
                { AutoCrudOperation.Update, HttpMethods.Put },
                { AutoCrudOperation.Patch, HttpMethods.Patch },
                { AutoCrudOperation.Delete, HttpMethods.Delete },
                { AutoCrudOperation.Save, HttpMethods.Post },
            };
            var allVerbs = crudVerbs.Values.Distinct().ToArray();

            var existingRoutes = new HashSet<Tuple<string,string>>();
            foreach (var op in appHost.Metadata.Operations)
            {
                foreach (var route in op.Routes.Safe())
                {
                    var routeVerbs = route.Verbs.IsEmpty() ? allVerbs : route.Verbs;
                    foreach (var verb in routeVerbs)
                    {
                        existingRoutes.Add(new Tuple<string, string>(route.Path, verb));
                    }
                }
            }

            var typesToGenerateMap = new Dictionary<string, TableSchema>();
            foreach (var result in results)
            {
                var keysCount = result.Columns.Count(x => x.IsKey);
                if (keysCount != 1) // Only support tables with 1 PK
                    continue;
                
                typesToGenerateMap[result.Name] = result;
            }
            
            var includeCrudServices = request.IncludeCrudOperations ?? genServices.IncludeCrudOperations;
            var includeCrudInterfaces = AutoCrudOperation.CrudInterfaceMetadataNames(includeCrudServices);
            
            var existingTypes = new HashSet<string>();
            var operations = new List<MetadataOperationType>();
            var types = new List<MetadataType>();
            
            var appDlls = new List<Assembly>();
            var exactTypesLookup = new Dictionary<Tuple<string,string>, MetadataType>();
            foreach (var op in metadataTypes.Operations)
            {
                exactTypesLookup[key(op.Request)] = op.Request;
                if (op.Response != null)
                    exactTypesLookup[key(op.Response)] = op.Response;
                
                if (op.Request.Type != null)
                    appDlls.Add(op.Request.Type.Assembly);
                if (op.Response?.Type != null)
                    appDlls.Add(op.Response.Type.Assembly);
            }
            foreach (var metaType in metadataTypes.Types)
            {
                exactTypesLookup[key(metaType)] = metaType;
                if (metaType.Type != null)
                    appDlls.Add(metaType.Type.Assembly);
            }
            
            // Also don't include Types in App's Implementation Assemblies
            appHost.ServiceAssemblies.Each(x => appDlls.Add(x));
            var existingAppTypes = new HashSet<string>();
            var existingExactAppTypes = new HashSet<Tuple<string,string>>();
            foreach (var appType in appDlls.SelectMany(x => x.GetTypes()))
            {
                existingAppTypes.Add(appType.Name);
                existingExactAppTypes.Add(keyNs(appType.Namespace, appType.Name));
            }

            // Re-use existing Types with same name for default DB Connection
            if (request.NamedConnection == null)
            {
                foreach (var op in metadataTypes.Operations)
                {
                    if (op.Request.Implements?.Any(x => includeCrudInterfaces.Contains(x.Name)) == true)
                    {
                        operations.Add(op);
                    }
                    existingTypes.Add(op.Request.Name);
                    if (op.Response != null)
                        existingTypes.Add(op.Response.Name);
                }
                foreach (var metaType in metadataTypes.Types)
                {
                    if (typesToGenerateMap.ContainsKey(metaType.Name))
                    {
                        types.Add(metaType);
                    }
                    existingTypes.Add(metaType.Name);
                }
            }
            else
            {
                // Only re-use existing Types with exact Namespace + Name for NamedConnection Types
                foreach (var op in metadataTypes.Operations)
                {
                    if (op.Request.Implements?.Any(x => includeCrudInterfaces.Contains(x.Name)) == true &&
                        exactTypesLookup.ContainsKey(keyNs(serviceModelNs, op.Request.Name)))
                    {
                        operations.Add(op);
                    }
                    
                    if (exactTypesLookup.ContainsKey(key(op.Request)))
                        existingTypes.Add(op.Request.Name);
                    if (op.Response != null && exactTypesLookup.ContainsKey(key(op.Request)))
                        existingTypes.Add(op.Response.Name);
                }
                foreach (var metaType in metadataTypes.Types)
                {
                    if (typesToGenerateMap.ContainsKey(metaType.Name) && 
                        exactTypesLookup.ContainsKey(keyNs(serviceModelNs, metaType.Name)))
                    {
                        types.Add(metaType);
                    }
                    if (exactTypesLookup.ContainsKey(keyNs(typesNs, metaType.Name)))
                        existingTypes.Add(metaType.Name);
                }
            }

            var crudMetadataTypes = new MetadataTypes {
                Config = metadataTypes.Config,
                Namespaces = metadataTypes.Namespaces,
                Operations = operations,
                Types = types,
            };
            if (request.Include == "new")
            {
                crudMetadataTypes.Operations = new List<MetadataOperationType>();
                crudMetadataTypes.Types = new List<MetadataType>();
            }

            using var db = request.NamedConnection == null
                ? dbFactory.OpenDbConnection()
                : dbFactory.OpenDbConnection(request.NamedConnection);
            var dialect = db.GetDialectProvider();

            List<MetadataPropertyType> toMetaProps(IEnumerable<ColumnSchema> columns, bool isModel=false)
            {
                var to = new List<MetadataPropertyType>();
                foreach (var column in columns)
                {
                    var dataType = genServices.ResolveColumnType(column, dialect);
                    if (dataType == null)
                        continue;

                    var isKey = column.IsKey || column.IsAutoIncrement;

                    if (dataType.IsValueType && column.AllowDBNull && !isKey)
                    {
                        dataType = typeof(Nullable<>).MakeGenericType(dataType);
                    }
                    
                    var prop = new MetadataPropertyType {
                        PropertyType = dataType,
                        Items = !isModel ? null : new Dictionary<string, object> {
                            [nameof(ColumnSchema)] = column,
                        },
                        Name = StringUtils.SnakeCaseToPascalCase(column.ColumnName),
                        Type = dataType.GetMetadataPropertyType(),
                        IsValueType = dataType.IsValueType ? true : (bool?) null,
                        IsSystemType = dataType.IsSystemType() ? true : (bool?) null,
                        IsEnum = dataType.IsEnum ? true : (bool?) null,
                        TypeNamespace = dataType.Namespace,
                        GenericArgs = MetadataTypesGenerator.ToGenericArgs(dataType),
                    };
                    
                    var attrs = new List<MetadataAttribute>();
                    if (isModel)
                    {
                        prop.TypeNamespace = typesNs;
                        if (column.IsKey && column.ColumnName != IdUtils.IdField && !column.IsAutoIncrement)
                            prop.AddAttribute(new PrimaryKeyAttribute());
                        if (column.IsAutoIncrement)
                            prop.AddAttribute(new AutoIncrementAttribute());
                        if (!string.Equals(dialect.NamingStrategy.GetColumnName(prop.Name), column.ColumnName, StringComparison.OrdinalIgnoreCase))
                            prop.AddAttribute(new AliasAttribute(column.ColumnName));
                        if (!dataType.IsValueType && !column.AllowDBNull && !isKey)
                            prop.AddAttribute(new RequiredAttribute());
                    }

                    if (attrs.Count > 0)
                        prop.Attributes = attrs;
                    
                    to.Add(prop);
                }
                return to;
            }

            bool containsType(string ns, string requestType) => request.NamedConnection == null
                ? existingTypes.Contains(requestType) || existingAppTypes.Contains(requestType)
                : exactTypesLookup.ContainsKey(keyNs(ns, requestType)) || existingExactAppTypes.Contains(keyNs(ns, requestType));

            void addToExistingTypes(string ns, MetadataType type)
            {
                existingTypes.Add(type.Name);
                exactTypesLookup[keyNs(ns, type.Name)] = type;
            }

            foreach (var entry in typesToGenerateMap)
            {
                var typeName = StringUtils.SnakeCaseToPascalCase(entry.Key);
                var tableSchema = entry.Value;
                if (includeCrudServices != null)
                {
                    var pkField = tableSchema.Columns.First(x => x.IsKey);
                    var id = StringUtils.SnakeCaseToPascalCase(pkField.ColumnName);
                    foreach (var operation in includeCrudServices)
                    {
                        if (!AutoCrudOperation.IsOperation(operation))
                            continue;

                        var requestType = operation + typeName;
                        if (containsType(serviceModelNs, requestType))
                            continue;

                        var verb = crudVerbs[operation];
                        var plural = Words.Pluralize(typeName).ToLower();
                        var route = verb == "GET" || verb == "POST"
                            ? "/" + plural
                            : "/" + plural + "/{" + id + "}";
                            
                        var op = new MetadataOperationType {
                            Actions = new List<string> { verb },
                            Routes = new List<MetadataRoute> {},
                            Request = new MetadataType {
                                Name = requestType,
                                Namespace = serviceModelNs,
                                Implements = new [] { 
                                    new MetadataTypeName {
                                        Name = "I" + verb[0] + verb.Substring(1).ToLower(), //marker interface 
                                    },
                                },
                            },
                            DataModel = new MetadataTypeName { Name = typeName },
                        };
                        op.Request.RequestType = op;
                        
                        if (!existingRoutes.Contains(new Tuple<string, string>(route, verb)))
                            op.Routes.Add(new MetadataRoute { Path = route, Verbs = verb });

                        if (verb == HttpMethods.Get)
                        {
                            op.Request.Inherits = new MetadataTypeName {
                                Namespace = "ServiceStack",
                                Name = "QueryDb`1",
                                GenericArgs = new[] { typeName },
                            };
                            op.ReturnType = new MetadataTypeName {
                                Namespace = "ServiceStack",
                                Name = "QueryResponse`1",
                                GenericArgs = new[] { typeName },
                            };
                            op.ViewModel = new MetadataTypeName { Name = typeName };
                            
                            var uniqueRoute = "/" + plural + "/{" + id + "}";
                            if (!existingRoutes.Contains(new Tuple<string, string>(uniqueRoute, verb)))
                            {
                                op.Routes.Add(new MetadataRoute {
                                    Path = uniqueRoute, 
                                    Verbs = verb
                                });
                            }
                        }
                        else
                        {
                            op.Request.Implements = new List<MetadataTypeName>(op.Request.Implements) {
                                new MetadataTypeName {
                                    Namespace = "ServiceStack",
                                    Name = $"I{operation}Db`1",
                                    GenericArgs = new[] {
                                        typeName,
                                    }
                                },
                            }.ToArray();
                            op.ReturnType = new MetadataTypeName {
                                Namespace = "ServiceStack",
                                Name = "IdResponse",
                            };
                            op.Response = new MetadataType {
                                Namespace = "ServiceStack",
                                Name = "IdResponse",
                            };
                        }

                        var allProps = toMetaProps(tableSchema.Columns);
                        switch (operation)
                        {
                            case AutoCrudOperation.Query:
                                // Only Id Property (use implicit conventions)
                                op.Request.Properties = new List<MetadataPropertyType> {
                                    new MetadataPropertyType {
                                        Name = id,
                                        Type = pkField.DataType.Name + (pkField.DataType.IsValueType ? "?" : ""),
                                        TypeNamespace = pkField.DataType.Namespace, 
                                    }
                                };
                                break;
                            case AutoCrudOperation.Create:
                                // all props - AutoIncrement/AutoId PK
                                var autoId = tableSchema.Columns.FirstOrDefault(x => x.IsAutoIncrement);
                                if (autoId != null)
                                {
                                    op.Request.Properties = toMetaProps(tableSchema.Columns)
                                        .Where(x => !x.Name.EqualsIgnoreCase(autoId.ColumnName)).ToList();
                                }
                                else
                                {
                                    op.Request.Properties = allProps;
                                }
                                break;
                            case AutoCrudOperation.Update:
                                // all props
                                op.Request.Properties = allProps;
                                break;
                            case AutoCrudOperation.Patch:
                                // all props
                                op.Request.Properties = allProps;
                                break;
                            case AutoCrudOperation.Delete:
                                // PK prop
                                var pks = tableSchema.Columns.Where(x => x.IsKey).ToList();
                                op.Request.Properties = toMetaProps(pks);
                                break;
                            case AutoCrudOperation.Save:
                                // all props
                                op.Request.Properties = allProps;
                                break;
                        }
                        
                        crudMetadataTypes.Operations.Add(op);
                        
                        addToExistingTypes(serviceModelNs, op.Request);
                        if (op.Response != null)
                            addToExistingTypes(serviceModelNs, op.Response);
                    }
                }

                if (!containsType(typesNs, typeName))
                {
                    var modelType = new MetadataType {
                        Name = typeName,
                        Namespace = typesNs,
                        Properties = toMetaProps(tableSchema.Columns, isModel:true),
                        Items = new Dictionary<string, object> {
                            [nameof(TableSchema)] = tableSchema,
                        }
                    };
                    if (request.NamedConnection != null)
                        modelType.AddAttribute(new NamedConnectionAttribute(request.NamedConnection));
                    if (request.Schema != null)
                        modelType.AddAttribute(new SchemaAttribute(request.Schema));
                    if (!string.Equals(dialect.NamingStrategy.GetTableName(typeName), tableSchema.Name, StringComparison.OrdinalIgnoreCase))
                        modelType.AddAttribute(new AliasAttribute(tableSchema.Name));
                    crudMetadataTypes.Types.Add(modelType);

                    addToExistingTypes(typesNs, modelType);
                }
            }

            if (genServices.IncludeService != null)
            {
                crudMetadataTypes.Operations = crudMetadataTypes.Operations.Where(genServices.IncludeService).ToList();
            }
            if (genServices.IncludeType != null)
            {
                crudMetadataTypes.Types = crudMetadataTypes.Types.Where(genServices.IncludeType).ToList();
            }

            if (genServices.ServiceFilter != null)
            {
                foreach (var op in crudMetadataTypes.Operations)
                {
                    genServices.ServiceFilter(op, req);
                }
            }

            if (genServices.TypeFilter != null)
            {
                foreach (var type in crudMetadataTypes.Types)
                {
                    genServices.TypeFilter(type, req);
                }
            }
            
            genServices.MetadataTypesFilter?.Invoke(crudMetadataTypes, typesConfig, req);
            
            return new Tuple<MetadataTypes, MetadataTypesConfig>(crudMetadataTypes, typesConfig);
        }
        
    }
    
    [Restrict(VisibilityTo = RequestAttributes.None)]
    [DefaultRequest(typeof(CrudCodeGenTypes))]
    public class CrudCodeGenTypesService : Service
    {
        [AddHeader(ContentType = MimeTypes.PlainText)]
        public object Any(CrudCodeGenTypes request)
        {
            try
            {
                var genServices = HostContext.AssertPlugin<AutoQueryFeature>().GenerateCrudServices;
                RequestUtils.AssertAccessRoleOrDebugMode(base.Request, accessRole: genServices.AccessRole, authSecret: request.AuthSecret);
                
                var src = GenerateCrudServices.GenerateSource(Request, request);
                return src;
            }
            catch (Exception e)
            {
                base.Response.StatusCode = e.ToStatusCode();
                base.Response.StatusDescription = e.GetType().Name;
                return e.ToString();
            }
        }
    }
    
    [Restrict(VisibilityTo = RequestAttributes.None)]
    [DefaultRequest(typeof(CrudTables))]
    public class CrudTablesService : Service
    {
        public object Any(CrudTables request)
        {
            var genServices = HostContext.AssertPlugin<AutoQueryFeature>().GenerateCrudServices;
            RequestUtils.AssertAccessRoleOrDebugMode(Request, accessRole: genServices.AccessRole, authSecret: request.AuthSecret);
            
            var dbFactory = TryResolve<IDbConnectionFactory>();
            var results = request.NoCache == true 
                ? GenerateCrudServices.GetTableSchemas(dbFactory, request.Schema, request.NamedConnection)
                : genServices.GetCachedDbSchema(dbFactory, request.Schema, request.NamedConnection).Tables;

            return new AutoCodeSchemaResponse {
                Results = results,
            };
        }
#if DEBUG
        public class GetMissingTypesToCreate {}

        public object Any(GetMissingTypesToCreate request) => ((GenerateCrudServices) HostContext
            .AssertPlugin<AutoQueryFeature>()
            .GenerateCrudServices).GetMissingTypesToCreate(out _);
#endif
    }

}