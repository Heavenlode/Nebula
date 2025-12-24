using System;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using Godot.Collections;

namespace Nebula.Serialization
{
    /// <summary>
    /// The singleton instance of the ProtocolRegistry.
    /// This is used to serialize and deserialize scenes, network properties, and network functions sent across the network.
    /// The point is that we can't send an entire scene string across the network or some other lengthy identifier
    /// because it would take up too much bandwidth.
    /// So we use this to classify scenes as bytes which can be sent across the network, using minimal bandwidth.
    /// The same goes for network properties and functions. See <see cref="ProtocolRegistry.Build"/> for more information.
    /// </summary>
    [Tool]
    public partial class ProtocolRegistry : Node
    {
        private Dictionary<byte, PackedScene> SCENES_CACHE = [];
        private Dictionary<int, Dictionary<StaticMethodType, Callable>> STATIC_METHOD_CALLABLES;
        private ProtocolResource resource;

        private static Dictionary<StaticMethodType, string> STATIC_METHODS = new Dictionary<StaticMethodType, string> {
            { StaticMethodType.NetworkSerialize, "NetworkSerialize" },
            { StaticMethodType.NetworkDeserialize, "NetworkDeserialize" },
            { StaticMethodType.BsonDeserialize, "BsonDeserialize" },
        };

        /// <summary>
        /// The singleton instance.
        /// </summary>
        public static ProtocolRegistry Instance { get; internal set; }
        public static ProtocolRegistry EditorInstance => Engine.GetSingleton("ProtocolRegistry") as ProtocolRegistry;

        /// <inheritdoc/>
        public override void _EnterTree()
        {
            if (Engine.IsEditorHint())
            {
                Engine.RegisterSingleton("ProtocolRegistry", this);
                return;
            }
            if (Instance != null)
            {
                QueueFree();
            }
            Instance = this;
        }

        protected override void Dispose(bool disposing)
        {
            STATIC_METHOD_CALLABLES.Clear();
            SCENES_CACHE.Clear();
            base.Dispose(disposing);
        }

        public override void _ExitTree()
        {
            Instance = null;
            base._ExitTree();
        }

        public void Load()
        {
            LoadProtocol();
            LoadStaticCallables();
        }

        public Callable? GetStaticMethodCallable(int staticMethodIndex, StaticMethodType type)
        {
            if (!STATIC_METHOD_CALLABLES.ContainsKey(staticMethodIndex))
                return null;
            if (!STATIC_METHOD_CALLABLES[staticMethodIndex].ContainsKey(type))
                return null;

            return STATIC_METHOD_CALLABLES[staticMethodIndex][type];
        }

        public Callable? GetStaticMethodCallable(ProtocolNetProperty property, StaticMethodType type)
        {
            if (property.ClassIndex == -1)
                return null;
            return GetStaticMethodCallable(property.ClassIndex, type);
        }

        /// <summary>
        /// List all network scenes.
        /// </summary>
        /// <returns>A dictionary with scene paths as keys and arrays of NetNode paths as values.</returns>
        public Dictionary<string, Array<string>> ListScenes()
        {
            var result = new Dictionary<string, Array<string>>();
            foreach (var scene in resource.SCENES_PACK.Keys)
            {
                var nodeList = new Array<string>();
                foreach (var nodePath in resource.STATIC_NETWORK_NODE_PATHS_PACK[scene].Keys)
                {
                    nodeList.Add(nodePath);
                }
                result[scene] = nodeList;
            }
            return result;
        }

        /// <summary>
        /// Pack a scene path into a byte to be sent over the network.
        /// </summary>
        /// <param name="scene">The scene path.</param>
        /// <returns>The packed byte.</returns>
        public byte PackScene(string scene)
        {
            return resource.SCENES_PACK[scene];
        }

        /// <summary>
        /// Unpack a scene byte into a scene path.
        /// </summary>
        /// <param name="sceneId">The scene byte.</param>
        /// <returns>The scene path.</returns>
        public PackedScene UnpackScene(byte sceneId)
        {
            if (!SCENES_CACHE.ContainsKey(sceneId))
            {
                SCENES_CACHE[sceneId] = GD.Load<PackedScene>(resource.SCENES_MAP[sceneId]);
            }
            return SCENES_CACHE[sceneId];
        }

        /// <summary>
        /// Lookup a property by its scene, node, and name.
        /// </summary>
        /// <param name="scene">The scene path.</param>
        /// <param name="node">The node path.</param>
        /// <param name="property">The property name.</param>
        /// <param name="prop">The property, if found.</param>
        /// <returns>True if the property was found, false otherwise.</returns>
        public bool LookupProperty(string scene, string node, string property, out ProtocolNetProperty prop)
        {
            if (!resource.PROPERTIES_MAP.ContainsKey(scene) || !resource.PROPERTIES_MAP[scene].ContainsKey(node) || !resource.PROPERTIES_MAP[scene][node].ContainsKey(property))
            {
                prop = new ProtocolNetProperty();
                return false;
            }

            prop = resource.PROPERTIES_MAP[scene][node][property];
            return true;
        }

        /// <summary>
        /// List all NetNodes which are not scenes.
        /// </summary>
        /// <param name="scene">The scene path.</param>
        /// <returns>An array of node paths.</returns>
        public Array<string> ListStaticNodes(string scene)
        {
            var result = new Array<string>();
            var nodes = resource.STATIC_NETWORK_NODE_PATHS_PACK[scene].Keys;
            var isFirst = true;
            foreach (var node in nodes)
            {
                if (isFirst)
                {
                    isFirst = false;
                    continue;
                }
                result.Add(node);
            }
            return result;
        }

        /// <summary>
        /// List all NetProperties in a scene.
        /// </summary>
        /// <param name="scene">The scene path.</param>
        /// <returns>An array of dictionaries with node paths and properties.</returns>
        public Array<Dictionary> ListProperties(string scene)
        {
            if (!resource.PROPERTIES_MAP.ContainsKey(scene))
            {
                return new Array<Dictionary>();
            }

            var result = new Array<Dictionary>();
            foreach (var node in resource.PROPERTIES_MAP[scene])
            {
                var entry = new Dictionary();
                entry["nodePath"] = node.Key;
                entry["properties"] = new Array<ProtocolNetProperty>(node.Value.Values);
                result.Add(entry);
            }
            return result;
        }

        /// <summary>
        /// List all NetProperties for a given NetNode within the scene.
        /// </summary>
        /// <param name="scene">The scene path.</param>
        /// <param name="node">The node path.</param>
        /// <returns>An array of NetProperties.</returns>
        public Array<ProtocolNetProperty> ListProperties(string scene, string node)
        {
            if (!resource.PROPERTIES_MAP.ContainsKey(scene) || !resource.PROPERTIES_MAP[scene].ContainsKey(node))
            {
                return [];
            }
            return [.. resource.PROPERTIES_MAP[scene][node].Values];
        }

        /// <summary>
        /// List all NetFunctions for a given NetNode within the scene.
        /// </summary>
        /// <param name="scene">The scene path.</param>
        /// <param name="node">The node path.</param>
        /// <returns>An array of NetFunctions.</returns>
        public Array<ProtocolNetFunction> ListFunctions(string scene, string node)
        {
            if (!resource.FUNCTIONS_MAP.ContainsKey(scene) || !resource.FUNCTIONS_MAP[scene].ContainsKey(node))
            {
                return [];
            }
            return [.. resource.FUNCTIONS_MAP[scene][node].Values];
        }
        /// <summary>
        /// Get the number of NetProperties in a scene.
        /// </summary>
        /// <param name="scene">The scene path.</param>
        /// <returns>The number of NetProperties.</returns>
        public int GetPropertyCount(string scene)
        {
            if (!resource.PROPERTIES_LOOKUP.ContainsKey(scene))
            {
                return 0;
            }
            return resource.PROPERTIES_LOOKUP[scene].Count;
        }

        /// <summary>
        /// Get a NetProperty by its scene and index (typically received from the network).
        /// </summary>
        /// <param name="scene">The scene path.</param>
        /// <param name="propertyId">The property index.</param>
        /// <returns>The property.</returns>
        public ProtocolNetProperty UnpackProperty(string scene, int propertyId)
        {
            if (!resource.PROPERTIES_LOOKUP.ContainsKey(scene) || !resource.PROPERTIES_LOOKUP[scene].ContainsKey(propertyId))
            {
                // Debugger.Instance.Log($"Property {propertyId} not found in scene {scene}", Debugger.DebugLevel.ERROR);
                throw new Exception($"Property {propertyId} not found in scene {scene}");
            }
            return resource.PROPERTIES_LOOKUP[scene][propertyId];
        }

        /// <summary>
        /// Get a NetNode path by its scene and index (typically received from the network).
        /// </summary>
        /// <param name="scene">The scene path.</param>
        /// <param name="nodeId">The node index.</param>
        /// <returns>The node path.</returns>
        public string UnpackNode(string scene, byte nodeId)
        {
            return resource.STATIC_NETWORK_NODE_PATHS_MAP[scene][nodeId];
        }

        /// <summary>
        /// Pack a scene's NetNode by path into a byte to be sent over the network.
        /// </summary>
        /// <param name="scene">The scene path.</param>
        /// <param name="node">The node path.</param>
        /// <param name="nodeId">The node byte.</param>
        /// <returns>True if the node was found, false otherwise.</returns>
        public bool PackNode(string scene, string node, out byte nodeId)
        {
            if (!resource.STATIC_NETWORK_NODE_PATHS_PACK.ContainsKey(scene) || !resource.STATIC_NETWORK_NODE_PATHS_PACK[scene].ContainsKey(node))
            {
                nodeId = 0;
                return false;
            }

            nodeId = resource.STATIC_NETWORK_NODE_PATHS_PACK[scene][node];
            return true;
        }

        /// <summary>
        /// List all NetNodes which are children of a given NetNode.
        /// </summary>
        /// <param name="node">The node.</param>
        /// <returns>An array of NetNodes.</returns>
        public Array<NetNodeWrapper> ListNetworkChildren(Node node)
        {
            var result = new Array<NetNodeWrapper>();
            var paths = resource.STATIC_NETWORK_NODE_PATHS_MAP[node.SceneFilePath];
            var isSelf = true;

            foreach (var path in paths)
            {
                if (isSelf)
                {
                    // The first node is the parent node itself (i.e. path is ".")
                    isSelf = false;
                    continue;
                }

                var child = new NetNodeWrapper(node.GetNodeOrNull(path.Value));
                if (child != null)
                {
                    result.Add(child);
                }
            }
            return result;
        }

        /// <summary>
        /// Lookup a NetFunction by its scene, node, and name.
        /// </summary>
        /// <param name="scene">The scene path.</param>
        /// <param name="node">The node path.</param>
        /// <param name="function">The function name.</param>
        /// <param name="func">The function, if found.</param>
        /// <returns>True if the function was found, false otherwise.</returns>
        public bool LookupFunction(string scene, string node, string function, out ProtocolNetFunction func)
        {
            if (!resource.FUNCTIONS_MAP.ContainsKey(scene) || !resource.FUNCTIONS_MAP[scene].ContainsKey(node) || !resource.FUNCTIONS_MAP[scene][node].ContainsKey(function))
            {
                func = new ProtocolNetFunction();
                return false;
            }

            func = resource.FUNCTIONS_MAP[scene][node][function];
            return true;
        }

        /// <summary>
        /// Get a NetFunction by its scene and index (typically received from the network).
        /// </summary>
        /// <param name="scene">The scene path.</param>
        /// <param name="functionId">The function index.</param>
        /// <returns>The function.</returns>
        public ProtocolNetFunction UnpackFunction(string scene, byte functionId)
        {
            return resource.FUNCTIONS_LOOKUP[scene][functionId];
        }

        /// <summary>
        /// Check if a scene is a NetScene.
        /// </summary>
        /// <param name="scenePath">The scene path.</param>
        /// <returns>True if the scene is a NetScene, false otherwise.</returns>
        public bool IsNetScene(string scenePath)
        {
            return resource.SCENES_PACK.ContainsKey(scenePath);
        }

        private void LoadProtocol()
        {
            resource = GD.Load<ProtocolResource>("res://Nebula.Protocol.res");
        }

        private void LoadStaticCallables()
        {
            STATIC_METHOD_CALLABLES = new Dictionary<int, Dictionary<StaticMethodType, Callable>>();
            foreach (var staticMethodIndex in resource.STATIC_METHODS.Keys)
            {
                var staticMethod = resource.STATIC_METHODS[staticMethodIndex];
                // Find the type
                Type type = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(staticMethod.ReflectionPath);
                    if (type != null)
                        break;
                }

                if (type == null)
                    throw new ArgumentException($"Type {staticMethod.ReflectionPath} not found in any loaded assembly");

                // Iterate over each serializationType enum value
                foreach (var serializationType in STATIC_METHODS.Keys)
                {
                    if ((staticMethod.StaticMethodType & serializationType) == 0)
                        continue;

                    var methodName = STATIC_METHODS[serializationType];


                    // Get the method
                    MethodInfo method = type.GetMethod(methodName,
                        BindingFlags.Public | BindingFlags.Static);

                    if (method == null)
                        throw new ArgumentException($"Static method {methodName} not found in {staticMethod.ReflectionPath}");


                    MethodInfo helperMethod = null;

                    if (serializationType == StaticMethodType.NetworkDeserialize)
                    {
                        helperMethod = type.GetMethod("GetDeserializeContext", BindingFlags.Public | BindingFlags.Static);
                        if (helperMethod == null)
                        {
                            throw new ArgumentException($"Helper method {methodName} not found in {staticMethod.ReflectionPath}");
                        }
                    }

                    // Create a callable that wraps the static method
                    // TODO: Find a way so this isn't special-cased for every serialization type.
                    // At time of writing, Callable doesn't support variable arguments.
                    // var callable = Callable.From((params object[] args) => method.Invoke(null, args));

                    Callable callable;

                    switch (serializationType)
                    {
                        case StaticMethodType.NetworkSerialize:
                            callable = Callable.From((WorldRunner currentWorld, NetPeer peer, GodotObject obj) => method.Invoke(null, [currentWorld, peer, obj]) as GodotObject);
                            break;
                        case StaticMethodType.NetworkDeserialize:
                            callable = Callable.From((WorldRunner currentWorld, NetPeer peer, HLBuffer buffer, GodotObject initialObject) =>
                            {
                                object ctxObj = helperMethod.Invoke(null, [initialObject]);
                                if (ctxObj is not Variant ctx)
                                    throw new InvalidCastException($"GetDeserializeContext in {staticMethod.ReflectionPath} didn't return a Variant.");
                                var result = method.Invoke(null, [currentWorld, peer, buffer, ctx]) as GodotObject;
                                return result;
                            });
                            break;
                        case StaticMethodType.BsonDeserialize:
                            callable = Callable.From((Variant context, byte[] bson, GodotObject initialObject, Callable completed) =>
                            {
                                Task.Run(async () =>
                                {
                                    try
                                    {
                                        var taskObj = method.Invoke(null, [context, bson, initialObject]);
                                        if (taskObj is not Task task)
                                            throw new InvalidCastException($"Method {method.Name} in {staticMethod.ReflectionPath} didn't return a Task like it fucking should.");
                                        
                                        await task.ConfigureAwait(false);

                                        dynamic dynamicTask = taskObj;
                                        var result = dynamicTask.Result;
                                        completed.Call(result as GodotObject);
                                    }
                                    catch (Exception ex)
                                    {
                                        GD.PrintErr($"BsonDeserialize blew up: {ex.Message}\n{ex.StackTrace}");
                                    }
                                });
                            });
                            break;
                        default:
                            throw new ArgumentException($"Unsupported serialization type: {serializationType}");
                    }

                    // Initialize the dictionary if it doesn't exist
                    if (!STATIC_METHOD_CALLABLES.ContainsKey(staticMethodIndex))
                        STATIC_METHOD_CALLABLES[staticMethodIndex] = new Dictionary<StaticMethodType, Callable>();

                    STATIC_METHOD_CALLABLES[staticMethodIndex][serializationType] = callable;
                }
            }
        }
    }
}