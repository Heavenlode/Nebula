using Godot;
using Godot.Collections;
using Nebula.Internal.Utility;
using System;
using System.Linq;

namespace Nebula.Serialization
{
#if DEBUG
    /// <summary>
    /// This extension of ProtocolRegistry is used for generating the <see cref="ProtocolResource"/>.
    /// </summary>
    [Tool]
    public partial class ProtocolRegistryBuilder : Node
    {
        public static string EmptySubtype = "None";
        /// <summary>
        /// Create and store the <see cref="ProtocolResource"/>, which is used to serialize and deserialize scenes, network properties, and network functions sent across the network.
        /// </summary>
        /// <returns>True if the resource was built successfully, false otherwise.</returns>
        /// <example>
        /// <list type="bullet">
        /// <item>
        /// <description>
        /// Imagine our game has two network scenes, "Game", and "Character".
        /// We compile that into bytecode so that "Game" is represented as 0, and "Character" is 1.
        /// Imagine "Character" has a network property "isAlive", which is a boolean.
        /// If we want to tell the client about the "isAlive" property of the "Character" scene,
        /// We only need to send two bytes across the network: 1 (the scene bytecode for "Character") and 0 (the index of "isAlive") which takes 16 bits.
        /// </description>
        /// </item>
        /// </list>
        /// </example>
        public bool Build()
        {
            var resource = new ProtocolResource();
            resource.STATIC_METHODS = [];
            resource.SCENES_MAP = [];
            resource.SCENES_PACK = [];
            resource.STATIC_NETWORK_NODE_PATHS_MAP = [];
            resource.STATIC_NETWORK_NODE_PATHS_PACK = [];
            resource.PROPERTIES_MAP = [];
            resource.FUNCTIONS_MAP = [];
            resource.PROPERTIES_LOOKUP = [];
            resource.FUNCTIONS_LOOKUP = [];
            SceneDataCache = [];
            serializableTypesMap = [];

            // This is necessary because in Godot you cannot call a static method from a GD.Load() Script.
            // And we don't want to perform reflection every single time we want to serialize or deserialize
            var serializableTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly => assembly.GetTypes()
                    .Where(type => {
                        if (type.IsInterface) return false;
                        
                        var interfaces = type.GetInterfaces();
                        return interfaces.Any(i => 
                            (i.IsGenericType && i.GetGenericTypeDefinition().Name == "IBsonSerializable`1") ||
                            (i.IsGenericType && i.GetGenericTypeDefinition().Name == "INetSerializable`1"));
                    }));

            var staticMethodIndex = 0;
            foreach (var type in serializableTypes)
            {
                var staticMethod = new StaticMethodResource();
                var interfaces = type.GetInterfaces();
                
                if (interfaces.Any(i => i.IsGenericType && 
                    i.GetGenericTypeDefinition().Name == "IBsonSerializable`1" && 
                    type.GetInterfaceMap(i).TargetMethods.Any(m => m.DeclaringType == type)))
                {
                    staticMethod.StaticMethodType |= StaticMethodType.BsonDeserialize;
                }
                if (interfaces.Any(i => i.IsGenericType && 
                    i.GetGenericTypeDefinition().Name == "INetSerializable`1" && 
                    type.GetInterfaceMap(i).TargetMethods.Any(m => m.DeclaringType == type)))
                {
                    staticMethod.StaticMethodType |= StaticMethodType.NetworkSerialize | StaticMethodType.NetworkDeserialize;
                }
                staticMethod.ReflectionPath = type.FullName;
                serializableTypesMap[type.FullName] = staticMethodIndex;
                resource.STATIC_METHODS[staticMethodIndex] = staticMethod;
                staticMethodIndex++;
            }

            // Get all types from loaded assemblies that implement INetNode
            var netNodeTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly => assembly.GetTypes()
                    .Where(type => type.GetInterfaces().Any(i => 
                        i.IsGenericType && i.GetGenericTypeDefinition().Name == "INetNode`1"))
                    .Select(type => new { Type = type, ScriptPath = GetScriptPath(type) })
                    .Select(x => new { x.Type, x.ScriptPath, NetworkTypeInfo = new NetworkTypeInfo
                        {
                            ScriptPath = x.ScriptPath,
                            Properties = GetInheritanceChain(x.Type)
                                .SelectMany(t => t.GetProperties(System.Reflection.BindingFlags.DeclaredOnly | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                                .Where(p => p.GetCustomAttributes(false)
                                    .Any(a => a.GetType().Name == "NetProperty"))
                                .Select(p => new NetPropertyInfo
                                {
                                    Property = p,
                                    Attributes = p.GetCustomAttributes(false)
                                }),
                            Functions = GetInheritanceChain(x.Type)
                                .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.DeclaredOnly | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                                .Where(m => m.GetCustomAttributes(false)
                                    .Any(a => a.GetType().Name == "NetFunction"))
                                .Select(m => new NetworkMethodInfo
                                {
                                    Method = m,
                                    Attributes = m.GetCustomAttributes(false)
                                })
                        }
                    })).ToList();

            // Check for duplicate script paths and report the problem types
            var duplicates = netNodeTypes.GroupBy(x => x.ScriptPath).Where(g => g.Count() > 1).ToList();
            if (duplicates.Any())
            {
                foreach (var group in duplicates)
                {
                    var typeNames = string.Join(", ", group.Select(x => x.Type.FullName));
                    GD.PrintErr($"DEBUG: DUPLICATE ScriptPath '{group.Key}' in types: {typeNames}");
                }
            }

            var nodeNetworkTypes = netNodeTypes.Select(x => x.NetworkTypeInfo).ToDictionary(x => x.ScriptPath, x => x);

            var sceneFileList = new Array<string>();
            void SearchDirectory(string path, Array<string> files)
            {
                using (var dir = DirAccess.Open(path))
                {
                    foreach (var file in dir.GetFiles())
                    {
                        if (file.EndsWith(".tscn"))
                        {
                            var pathTrimmed = path.TrimPrefix("res://");
                            // Paths that are inside a directory should have a leading slash
                            if (pathTrimmed != "") pathTrimmed = $"{pathTrimmed}/";
                            files.Add($"{pathTrimmed}{file}");
                        }
                    }

                    foreach (var subdir in dir.GetDirectories())
                    {
                        if (path == "res://")
                        {
                            SearchDirectory($"res://{subdir}", files);
                        }
                        else
                        {
                            SearchDirectory($"{path}/{subdir}", files);
                        }
                    }
                }
            }

            SearchDirectory("res://", sceneFileList);

            var sceneFileContentByPath = new Dictionary<string, string>();
            foreach (var file in sceneFileList)
            {
                var path = $"res://{file}";
                using (var fileAccess = FileAccess.Open(path, FileAccess.ModeFlags.Read))
                {
                    sceneFileContentByPath[path] = fileAccess.GetAsText();
                }
            }

            byte sceneId = 0;
            foreach (var sceneFile in sceneFileList)
            {
                var sceneResourcePath = $"res://{sceneFile}";
                var result = GenerateSceneBytecode(sceneResourcePath, sceneFileContentByPath, nodeNetworkTypes);

                if (!result.IsNetScene) continue;

                resource.SCENES_MAP[sceneId] = sceneResourcePath;
                resource.SCENES_PACK[sceneResourcePath] = sceneId;
                sceneId++;

                if (result.StaticNetNodes.Count > 0)
                {
                    resource.STATIC_NETWORK_NODE_PATHS_MAP[sceneResourcePath] = new Dictionary<byte, string>();
                    resource.STATIC_NETWORK_NODE_PATHS_PACK[sceneResourcePath] = new Dictionary<string, byte>();
                    foreach (var pair in result.StaticNetNodes)
                    {
                        byte nodeId = pair["id"].AsByte();
                        string path = pair["path"].AsString();
                        resource.STATIC_NETWORK_NODE_PATHS_MAP[sceneResourcePath][nodeId] = path;
                        resource.STATIC_NETWORK_NODE_PATHS_PACK[sceneResourcePath][path] = nodeId;
                    }
                }

                if (result.Properties.Count > 0)
                {
                    resource.PROPERTIES_MAP[sceneResourcePath] = result.Properties;
                    resource.PROPERTIES_LOOKUP[sceneResourcePath] = new Dictionary<int, ProtocolNetProperty>();

                    foreach (var node in result.Properties)
                    {
                        foreach (var prop in node.Value.Values)
                        {
                            resource.PROPERTIES_LOOKUP[sceneResourcePath][prop.Index] = prop;
                        }
                    }
                }

                if (result.Functions.Count > 0)
                {
                    resource.FUNCTIONS_MAP[sceneResourcePath] = result.Functions;
                    resource.FUNCTIONS_LOOKUP[sceneResourcePath] = new Dictionary<int, ProtocolNetFunction>();

                    foreach (var node in result.Functions)
                    {
                        foreach (var func in node.Value.Values)
                        {
                            resource.FUNCTIONS_LOOKUP[sceneResourcePath][func.Index] = func;
                        }
                    }
                }
            }

            // Save the resource
            var err = ResourceSaver.Save(resource, "res://Nebula.Protocol.res", ResourceSaver.SaverFlags.Compress);
            if (err != Error.Ok)
            {
                GD.PrintErr($"Failed to save protocol resource: {err}");
                return false;
            }
            return true;
        }

        private bool IsNetNode(string scriptPath, System.Collections.Generic.Dictionary<string, NetworkTypeInfo> nodeNetworkTypes)
        {
            return nodeNetworkTypes.ContainsKey(scriptPath);
        }

        private object GetAttributeValue(object attribute, string propertyName)
        {
            return attribute.GetType().GetProperty(propertyName)?.GetValue(attribute);
        }

        private ExtendedVariantType GetVariantType(Type type)
        {
            // Map C# types to Godot variant types
            if (type == typeof(bool)) return new ExtendedVariantType { Type = Variant.Type.Bool };
            if (type == typeof(short)) return new ExtendedVariantType { Type = Variant.Type.Int, Subtype = "Short" };
            if (type == typeof(int)) return new ExtendedVariantType { Type = Variant.Type.Int, Subtype = "Int" };
            if (type == typeof(byte)) return new ExtendedVariantType { Type = Variant.Type.Int, Subtype = "Byte" };
            if (type == typeof(byte[])) return new ExtendedVariantType { Type = Variant.Type.PackedByteArray };
            if (type == typeof(long[])) return new ExtendedVariantType { Type = Variant.Type.PackedInt64Array };
            if (type == typeof(long)) return new ExtendedVariantType { Type = Variant.Type.Int };
            if (type == typeof(float)) return new ExtendedVariantType { Type = Variant.Type.Float };
            if (type == typeof(string)) return new ExtendedVariantType { Type = Variant.Type.String };
            if (type == typeof(Vector3)) return new ExtendedVariantType { Type = Variant.Type.Vector3 };
            if (type == typeof(Quaternion)) return new ExtendedVariantType { Type = Variant.Type.Quaternion };
            // Add more type mappings as needed

            // Check if type extends GodotObject
            if (typeof(GodotObject).IsAssignableFrom(type))
            {
                var subtype = EmptySubtype;
                // Check for custom serializable types
                var typeIdentifier = type.GetCustomAttributes(false)
                    .FirstOrDefault(attr => attr.GetType().Name == "SerialTypeIdentifier");
                if (typeIdentifier != null)
                {
                    subtype = ((SerialTypeIdentifier)typeIdentifier).Name;
                }
                return new ExtendedVariantType { Type = Variant.Type.Object, Subtype = subtype };
            }

            // Check for enum types
            if (type.IsEnum) return new ExtendedVariantType { Type = Variant.Type.Int, Subtype = "Int" };

            GD.PrintErr($"Unknown type: {type.Name}");

            return new ExtendedVariantType { Type = Variant.Type.Nil };
        }

        private Dictionary<string, SceneBytecode> SceneDataCache;
        private Dictionary<string, int> serializableTypesMap = new Dictionary<string, int>();
        private SceneBytecode GenerateSceneBytecode(string sceneResourcePath, Dictionary<string, string> sceneFileContentByPath, System.Collections.Generic.Dictionary<string, NetworkTypeInfo> nodeNetworkTypes)
        {
            // Scene bytecode is cached to avoid redundant parsing
            if (SceneDataCache.TryGetValue(sceneResourcePath, out var cachedData))
            {
                return cachedData;
            }

            SceneBytecode result = new SceneBytecode
            {
                Properties = new Dictionary<string, Dictionary<string, ProtocolNetProperty>>(),
                Functions = new Dictionary<string, Dictionary<string, ProtocolNetFunction>>(),
                StaticNetNodes = new Array<Dictionary>(),
                IsNetScene = false
            };

            // Parse the scene file to gather its child nodes and scenes therein
            // With that information, we can build the scene bytecode
            // We don't use Godot's built-in Config File parser because it can't load scene files correctly as of Godot 4.4
            // (It throws errors)
            var parser = new ConfigParser();
            var parsedTscn = parser.ParseTscnFile(sceneFileContentByPath[sceneResourcePath]);

            // Ensure the scene has a script on the root node.
            if (parsedTscn.RootNode == null || !parsedTscn.RootNode.Properties.TryGetValue("script", out var rootScript))
                return result;

            result.IsNetScene = IsNetNode(rootScript, nodeNetworkTypes);
            var nodePathId = 0;
            var propertyCount = 0;
            var functionCount = 0;

            foreach (var node in parsedTscn.Nodes)
            {
                var nodePath = node.Parent == null ? "." : node.Parent == "." ? node.Name : $"{node.Parent}/{node.Name}";

                var nodeHasScript = node.Properties.ContainsKey("script");
                var nodeIsNetNode = nodeHasScript && IsNetNode(node.Properties["script"], nodeNetworkTypes);
                var nodeIsNestedScene = node.Instance != null;

                if (!nodeIsNetNode && !nodeIsNestedScene)
                {
                    continue;
                }
                // This is a nested scene within the scene
                if (nodeIsNestedScene)
                {
                    var recurseData = GenerateSceneBytecode(node.Instance, sceneFileContentByPath, nodeNetworkTypes);

                    // Nested network scenes do not roll up into the parent scene.
                    if (recurseData.IsNetScene) continue;

                    foreach (var entry in recurseData.StaticNetNodes)
                    {
                        var newEntry = new Dictionary();
                        newEntry["id"] = nodePathId++;
                        newEntry["path"] = nodePath + "/" + entry["path"].AsString();
                        result.StaticNetNodes.Add(newEntry);
                    }
                    foreach (var kvp in recurseData.Properties)
                    {
                        foreach (var prop in kvp.Value) {
                            prop.Value.Index = (byte)propertyCount++;
                        }
                        result.Properties[nodePath + "/" + kvp.Key] = kvp.Value;
                    }
                    foreach (var kvp in recurseData.Functions)
                    {
                        foreach (var func in kvp.Value) {
                            func.Value.Index = (byte)functionCount++;
                        }
                        result.Functions[nodePath + "/" + kvp.Key] = kvp.Value;
                    }
                    continue;
                }

                // At this point, we're looking at a node with a script that implements INetNode
                var nodeEntry = new Dictionary();
                nodeEntry["id"] = nodePathId++;
                nodeEntry["path"] = nodePath;
                result.StaticNetNodes.Add(nodeEntry);

                // Collect network properties and functions using reflection
                var properties = nodeNetworkTypes[node.Properties["script"]].Properties;
                foreach (var propertyInfo in properties)
                {
                    var networkPropertyAttr = propertyInfo.Attributes
                        .FirstOrDefault(attr => attr.GetType().Name == "NetProperty");
                    if (networkPropertyAttr == null) continue;

                    var propType = GetVariantType(propertyInfo.Property.PropertyType);

                    var networkProp = new ProtocolNetProperty
                    {
                        NodePath = nodePath,
                        Name = propertyInfo.Property.Name,
                        VariantType = propType.Type,
                        Metadata = new SerialMetadata
                        {
                            TypeIdentifier = string.IsNullOrEmpty(propType.Subtype) ? EmptySubtype : propType.Subtype,
                        },
                        InterestMask = GetAttributeValue(networkPropertyAttr, "InterestMask") as long? ?? 0,
                        Index = (byte)propertyCount++,
                        ClassIndex = serializableTypesMap.TryGetValue(propertyInfo.Property.PropertyType.FullName, out var index) ? index : -1
                    };

                    if (!result.Properties.ContainsKey(nodePath))
                    {
                        result.Properties[nodePath] = new Dictionary<string, ProtocolNetProperty>();
                    }
                    result.Properties[nodePath][propertyInfo.Property.Name] = networkProp;
                }

                var methods = nodeNetworkTypes[node.Properties["script"]].Functions;
                foreach (var methodInfo in methods)
                {
                    var networkFunctionAttr = methodInfo.Attributes
                        .FirstOrDefault(attr => attr.GetType().Name == "NetFunction");
                    if (networkFunctionAttr == null) continue;

                    var parameters = methodInfo.Method.GetParameters();
                    var variantTypes = new ExtendedVariantType[parameters.Length];
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        variantTypes[i] = GetVariantType(parameters[i].ParameterType);
                    }

                    var networkFunc = new ProtocolNetFunction
                    {
                        NodePath = nodePath,
                        Name = methodInfo.Method.Name,
                        Arguments = variantTypes
                            .Select(x =>
                                new NetFunctionArgument { VariantType = x.Type, Metadata = new SerialMetadata { TypeIdentifier = x.Subtype } }
                            ).ToArray(),
                        WithPeer = GetAttributeValue(networkFunctionAttr, "WithPeer") as bool? ?? false,
                        Sources = GetAttributeValue(networkFunctionAttr, "Source") as NetFunction.NetworkSources? ?? NetFunction.NetworkSources.All,
                        Index = (byte)functionCount++
                    };

                    if (!result.Functions.ContainsKey(nodePath))
                    {
                        result.Functions[nodePath] = new Dictionary<string, ProtocolNetFunction>();
                    }
                    result.Functions[nodePath][methodInfo.Method.Name] = networkFunc;
                }
            }

            SceneDataCache[sceneResourcePath] = result;
            return result;
        }


        // Helper function to get inheritance chain including the type itself
        private System.Collections.Generic.IEnumerable<Type> GetInheritanceChain(Type type)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                if (current.GetInterfaces().Any(i => 
                    i.IsGenericType && i.GetGenericTypeDefinition().Name == "INetNode`1"))
                {
                    yield return current;
                }
            }
        }

        // Helper function to get script path from ScriptPathAttribute
        private string GetScriptPath(Type type)
        {
            var scriptPathAttr = type.GetCustomAttributes(false)
                .FirstOrDefault(attr => attr.GetType().Name == "ScriptPathAttribute");

            if (scriptPathAttr != null)
            {
                var path = GetAttributeValue(scriptPathAttr, "Path")?.ToString();
                return path?.Replace("\\", "/") ?? "";
            }
            
            // Only debug print the problem cases
            if (type.Name.Contains("NetNode"))
            {
                GD.PrintErr($"{type.FullName} missing ScriptPathAttribute. This usually means your file name doesn't match the class name, which throws Godot's build system off. Be sure they match.");
            }
            return "";
        }
    }

    internal class NetPropertyInfo
    {
        public System.Reflection.PropertyInfo Property { get; init; }
        public object[] Attributes { get; init; }
    }

    internal class NetworkMethodInfo
    {
        public System.Reflection.MethodInfo Method { get; init; }
        public object[] Attributes { get; init; }
    }

    internal class NetworkTypeInfo
    {
        public string ScriptPath { get; init; }
        public System.Collections.Generic.IEnumerable<NetPropertyInfo> Properties { get; init; }
        public System.Collections.Generic.IEnumerable<NetworkMethodInfo> Functions { get; init; }
    }

    /// <summary>
    /// An extended variant type to extend the encoding of Godot's Variant type.
    /// </summary>
    internal class ExtendedVariantType
    {
        public Variant.Type Type;
        public string Subtype;
    }

#endif
}