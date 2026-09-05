using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;

namespace Nebula.Serialization
{
    /// <summary>
    /// Runtime helper that wraps the generated Protocol data and provides convenience methods.
    /// This bridges the generated pure-C# data with Godot runtime operations.
    /// </summary>
    public static class Protocol
    {
        // Cache for reflected static methods
        private static readonly Dictionary<(string TypeName, string MethodName), MethodInfo> _methodCache = new();
        private static readonly Dictionary<string, Type> _typeCache = new();

        #region Protocol Identity

        /// <summary>
        /// The Nebula version this build was compiled against, read from plugin.cfg at
        /// generation time. Part of <see cref="Hash"/>.
        /// </summary>
        public static string NebulaVersion => GeneratedProtocol.NebulaVersion;

        /// <summary>
        /// Deterministic 64-bit hash of the entire generated protocol (Nebula version,
        /// scenes, properties, functions, serializable types). Identical across builds of
        /// the same Nebula version generated from identical protocol source.
        /// </summary>
        public static ulong Hash => GeneratedProtocol.ProtocolHash;

        /// <summary>
        /// 32-bit fold of <see cref="Hash"/>, sized to fit ENet's connect-data field.
        /// Sent by clients in the connection handshake and validated by the server.
        /// </summary>
        public static uint HandshakeHash => unchecked((uint)(GeneratedProtocol.ProtocolHash ^ (GeneratedProtocol.ProtocolHash >> 32)));

        #endregion

        #region Property Lookups

        /// <summary>
        /// Look up a property by scene path, node path, and property name.
        /// </summary>
        public static bool LookupProperty(string scenePath, string nodePath, string propertyName, out ProtocolNetProperty property)
        {
            property = default;

            if (!GeneratedProtocol.PropertiesMap.TryGetValue(scenePath, out var nodeMap))
                return false;

            if (!nodeMap.TryGetValue(nodePath, out var propMap))
                return false;

            if (!propMap.TryGetValue(propertyName, out property))
                return false;

            return true;
        }

        /// <summary>
        /// Get a property by scene path and index.
        /// </summary>
        public static ProtocolNetProperty UnpackProperty(string scenePath, int propertyIndex)
        {
            if (GeneratedProtocol.PropertiesLookup.TryGetValue(scenePath, out var lookup) &&
                lookup.TryGetValue(propertyIndex, out var prop))
            {
                return prop;
            }

            throw new KeyNotFoundException($"Property index {propertyIndex} not found for scene {scenePath}");
        }

        /// <summary>
        /// Try to get a property by scene path and index.
        /// </summary>
        public static bool TryUnpackProperty(string scenePath, int propertyIndex, out ProtocolNetProperty property)
        {
            property = default;

            if (GeneratedProtocol.PropertiesLookup.TryGetValue(scenePath, out var lookup) &&
                lookup.TryGetValue(propertyIndex, out property))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Get the total number of properties for a scene.
        /// </summary>
        public static int GetPropertyCount(string scenePath)
        {
            if (GeneratedProtocol.PropertiesLookup.TryGetValue(scenePath, out var lookup))
            {
                return lookup.Count;
            }
            return 0;
        }

        /// <summary>
        /// Look up a property by scene path, static child ID, and property name.
        /// This is the preferred method for runtime lookups as it avoids string-based node path computation.
        /// </summary>
        public static bool LookupPropertyByStaticChildId(string scenePath, byte staticChildId, string propertyName, out ProtocolNetProperty property)
        {
            property = default;

            if (!GeneratedProtocol.PropertiesByStaticChildId.TryGetValue(scenePath, out var nodeMap))
                return false;

            if (!nodeMap.TryGetValue(staticChildId, out var propMap))
                return false;

            if (!propMap.TryGetValue(propertyName, out property))
                return false;

            return true;
        }

        #endregion

        #region Function Lookups

        /// <summary>
        /// Look up a function by scene path, node path, and function name.
        /// </summary>
        public static bool LookupFunction(string scenePath, string nodePath, string functionName, out ProtocolNetFunction function)
        {
            function = default;

            if (!GeneratedProtocol.FunctionsMap.TryGetValue(scenePath, out var nodeMap))
                return false;

            if (!nodeMap.TryGetValue(nodePath, out var funcMap))
                return false;

            if (!funcMap.TryGetValue(functionName, out function))
                return false;

            return true;
        }

        /// <summary>
        /// Get a function by scene path and index.
        /// </summary>
        public static ProtocolNetFunction UnpackFunction(string scenePath, int functionIndex)
        {
            if (GeneratedProtocol.FunctionsLookup.TryGetValue(scenePath, out var lookup) &&
                lookup.TryGetValue(functionIndex, out var func))
            {
                return func;
            }

            throw new KeyNotFoundException($"Function index {functionIndex} not found for scene {scenePath}");
        }

        /// <summary>
        /// Get the total number of functions for a scene.
        /// </summary>
        public static int GetFunctionCount(string scenePath)
        {
            if (GeneratedProtocol.FunctionsLookup.TryGetValue(scenePath, out var lookup))
            {
                return lookup.Count;
            }
            return 0;
        }

        #endregion

        #region Scene Lookups

        /// <summary>
        /// Get scene path by ID.
        /// </summary>
        public static string GetScenePath(byte sceneId)
        {
            if (GeneratedProtocol.ScenesMap.TryGetValue(sceneId, out var path))
                return path;
            return "";
        }

        /// <summary>
        /// Get scene ID by path.
        /// </summary>
        public static bool TryGetSceneId(string scenePath, out byte sceneId)
        {
            return GeneratedProtocol.ScenesPack.TryGetValue(scenePath, out sceneId);
        }

        /// <summary>
        /// Check if a scene path is registered as a network scene.
        /// </summary>
        public static bool IsNetScene(string scenePath)
        {
            return GeneratedProtocol.ScenesPack.ContainsKey(scenePath);
        }

        /// <summary>
        /// Get scene-level interest requirements for a network scene.
        /// </summary>
        public static ProtocolSceneInterest GetSceneInterest(string scenePath)
        {
            if (GeneratedProtocol.SceneInterestMap.TryGetValue(scenePath, out var interest))
                return interest;
            return default;
        }

        /// <summary>
        /// Try to get scene-level interest requirements for a network scene.
        /// </summary>
        public static bool TryGetSceneInterest(string scenePath, out ProtocolSceneInterest interest)
        {
            return GeneratedProtocol.SceneInterestMap.TryGetValue(scenePath, out interest);
        }

        /// <summary>
        /// Pack a scene path to its byte ID.
        /// </summary>
        public static byte PackScene(string scenePath)
        {
            if (GeneratedProtocol.ScenesPack.TryGetValue(scenePath, out var id))
                return id;
            throw new KeyNotFoundException($"Scene not found in protocol: {scenePath}");
        }

        /// <summary>
        /// Every scene the protocol knows, held for the life of the process once first used.
        ///
        /// The holding is the point, not the load saving. Godot keeps a resource -- and its GPU
        /// residency -- only while something references it, and a PackedScene references everything
        /// inside it. Loading per call meant that when a world was freed on a world change, the last
        /// reference to its meshes and textures went with it, and the arriving world paid to upload
        /// the same resources again.
        ///
        /// That cost was measured on this project's hub scene: first entry into the tree 149ms,
        /// destruction 17ms, and re-entry of the same scene with the PackedScene still referenced
        /// 18ms. The 130ms difference is upload, and it is invisible to every profiler that times the
        /// AddChild -- it lands in the following frame.
        ///
        /// Concurrent because spawn deserialization reaches this from world tick threads.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<byte, PackedScene> SceneCache = new();

        /// <summary>
        /// Unpack a scene ID to its PackedScene. Cached -- see <see cref="SceneCache"/> for why that
        /// matters to frame time and not just to load time.
        /// </summary>
        public static PackedScene UnpackScene(byte sceneId)
        {
            if (SceneCache.TryGetValue(sceneId, out var cached)) return cached;

            if (GeneratedProtocol.ScenesMap.TryGetValue(sceneId, out var path))
                return SceneCache.GetOrAdd(sceneId, _ => GD.Load<PackedScene>(path));

            throw new KeyNotFoundException($"Scene ID not found in protocol: {sceneId}");
        }

        /// <summary>
        /// Whether <see cref="UnpackScene"/> would answer from cache. False means the next
        /// resolve is a synchronous <c>GD.Load</c> of the scene and its whole dependency graph —
        /// on a client that happens mid-tick, on the main thread, and is the difference between
        /// a spawn costing a millisecond and costing a visible freeze.
        /// </summary>
        public static bool IsSceneCached(byte sceneId) => SceneCache.ContainsKey(sceneId);

        /// <summary>
        /// The scenes marked <see cref="Nebula.Preload"/>, in protocol order.
        /// </summary>
        public static IReadOnlyList<string> ListPreloadScenes() => GeneratedProtocol.PreloadScenes;

        /// <summary>
        /// Guards against a second caller paying a cost the first already paid. Preloading is
        /// idempotent by nature -- the second Instantiate is the cheap one -- but a caller that
        /// invokes this from two screens should not queue the work twice.
        /// </summary>
        private static int _preloadStarted;

        /// <summary>
        /// Builds every <see cref="Nebula.Preload"/> scene once and throws the instances away, so the
        /// per-process first-instantiate cost is paid here instead of wherever the game first needs
        /// them. Returns when they are all built.
        ///
        /// Measured on this engine: a first <c>Instantiate()</c> costs ~100ms whatever the scene is,
        /// and the second costs 0.2ms. Call this from a menu, a hub or a loading screen -- anywhere the
        /// player is already waiting -- and arriving in one of those scenes costs a normal frame.
        ///
        /// Thread split, and it is not negotiable: the LOAD runs off main where that is safe (any
        /// client -- a real renderer's RID owners are thread-safe, a HEADLESS server's dummy
        /// renderer's are not; see <see cref="NebulaThread.CanBuildResourcesOffMain"/>), through the
        /// engine's own threaded loader. The INSTANTIATE runs on the main thread, one scene per
        /// frame. Instantiating on the worker was the original design and it was a launch-time
        /// crash: building scene nodes issues RenderingServer work (visual instances, particles)
        /// that raced the main thread's frame -- a FATAL "index 0 of size 0" in engine code, about
        /// one client launch in three. Worse, a Godot FATAL trap inside a .NET-hosted process is
        /// intercepted by the runtime's exception dispatcher and re-executed forever, so the client
        /// did not even crash: its main thread spun silently and it never sent its ENet connect.
        /// Bisected by launch loop: load-only off main is clean over 10/10; load + instantiate off
        /// main failed 3/9 and 1/3.
        ///
        /// Nothing is added to the tree, so no _Ready runs and nothing registers itself. The instances
        /// exist only long enough to force their script classes through construction once.
        /// </summary>
        /// <returns>How many scenes were built. Zero if none are marked, or if a call is already in
        /// flight.</returns>
        public static async System.Threading.Tasks.Task<int> PreloadScenes()
        {
            var scenes = GeneratedProtocol.PreloadScenes;
            if (scenes.Length == 0) return 0;

            if (System.Threading.Interlocked.Exchange(ref _preloadStarted, 1) != 0) return 0;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool offMain = NebulaThread.CanBuildResourcesOffMain;

            PackedScene[] packed = offMain
                ? await System.Threading.Tasks.Task.Run(() => LoadPreloadScenesThreaded(scenes))
                : LoadPreloadScenesInline(scenes);
            long loadMs = clock.ElapsedMilliseconds;

            // The await above resumes on Godot's synchronization context, i.e. the main thread --
            // which is where instantiation MUST happen (see the summary). Checked, not assumed.
            NebulaThread.AssertMain("Protocol.PreloadScenes instantiate");

            int built = 0;
            var tree = Engine.GetMainLoop() as SceneTree;
            for (var i = 0; i < scenes.Length; i++)
            {
                if (packed[i] == null) continue;
                // One per frame: each first instantiate is a ~100ms hitch, and four back to back
                // is a visible freeze where four spread out are not.
                if (built > 0 && offMain && tree != null)
                    await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                try
                {
                    var throwaway = packed[i].Instantiate();
                    throwaway.Free();
                    built++;
                }
                catch (Exception ex)
                {
                    Utility.Tools.Debugger.Instance?.Log(
                        $"[Preload] {scenes[i]} threw while building: {ex.Message}. Skipping it; the " +
                        "scene will simply pay its own cost on first use.",
                        Utility.Tools.Debugger.DebugLevel.WARN);
                }
            }

            Utility.Tools.Debugger.Instance?.Log(
                $"[Preload] built {built}/{scenes.Length} scene(s) in {clock.ElapsedMilliseconds}ms" +
                (offMain
                    ? $" (loaded on a worker in {loadMs}ms, instantiated on main one per frame)"
                    : " inline") +
                "; arriving in one of them is now a normal frame.");

            return built;
        }

        /// <summary>
        /// Worker half of <see cref="PreloadScenes"/>: loads through the engine's threaded loader
        /// (the one off-main load path the engine supports) and parks each scene in
        /// <see cref="SceneCache"/>. Returns one entry per requested scene, null where it did not load.
        /// </summary>
        private static PackedScene[] LoadPreloadScenesThreaded(string[] scenes)
        {
            var result = new PackedScene[scenes.Length];
            for (var i = 0; i < scenes.Length; i++)
            {
                try
                {
                    var err = ResourceLoader.LoadThreadedRequest(scenes[i]);
                    var packed = err == Error.Ok
                        ? ResourceLoader.LoadThreadedGet(scenes[i]) as PackedScene
                        : null;
                    result[i] = HoldPreloaded(scenes[i], packed);
                }
                catch (Exception ex)
                {
                    Utility.Tools.Debugger.Instance?.Log(
                        $"[Preload] {scenes[i]} threw while loading: {ex.Message}. Skipping it.",
                        Utility.Tools.Debugger.DebugLevel.WARN);
                }
            }
            return result;
        }

        /// <summary>Main-thread half for a headless server: a plain synchronous load per scene.</summary>
        private static PackedScene[] LoadPreloadScenesInline(string[] scenes)
        {
            var result = new PackedScene[scenes.Length];
            for (var i = 0; i < scenes.Length; i++)
            {
                try
                {
                    result[i] = HoldPreloaded(scenes[i], GD.Load<PackedScene>(scenes[i]));
                }
                catch (Exception ex)
                {
                    Utility.Tools.Debugger.Instance?.Log(
                        $"[Preload] {scenes[i]} threw while loading: {ex.Message}. Skipping it.",
                        Utility.Tools.Debugger.DebugLevel.WARN);
                }
            }
            return result;
        }

        /// <summary>
        /// HELD, not just loaded. Godot's resource cache keeps weak references, so a PackedScene
        /// nobody holds is unloaded again the moment the loader drops it -- taking its whole
        /// dependency graph with it and leaving the game to reload the lot from disk on arrival.
        /// Preloading a scene and then letting it evaporate is worse than not preloading it: it
        /// costs the load twice. Parked in the same cache UnpackScene reads, so the spawn path that
        /// eventually needs this scene finds it already there.
        /// </summary>
        private static PackedScene HoldPreloaded(string scenePath, PackedScene packed)
        {
            if (packed == null)
            {
                Utility.Tools.Debugger.Instance?.Log(
                    $"[Preload] {scenePath} did not load; skipping it.",
                    Utility.Tools.Debugger.DebugLevel.WARN);
                return null;
            }
            if (TryGetSceneId(scenePath, out var sceneId)) SceneCache.TryAdd(sceneId, packed);
            return packed;
        }

        #endregion

        #region Static Node Paths

        /// <summary>
        /// Get static network node path by scene and node ID.
        /// </summary>
        public static string GetStaticNodePath(string scenePath, byte nodeId)
        {
            if (GeneratedProtocol.StaticNetworkNodePathsMap.TryGetValue(scenePath, out var nodeMap) &&
                nodeMap.TryGetValue(nodeId, out var path))
            {
                return path;
            }
            return "";
        }

        /// <summary>
        /// Get static network node ID by scene and path.
        /// </summary>
        public static bool TryGetStaticNodeId(string scenePath, string nodePath, out byte nodeId)
        {
            nodeId = 0;
            if (GeneratedProtocol.StaticNetworkNodePathsPack.TryGetValue(scenePath, out var nodeMap) &&
                nodeMap.TryGetValue(nodePath, out nodeId))
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Pack a node path to its byte ID within a scene.
        /// </summary>
        public static bool PackNode(string scenePath, string nodePath, out byte nodeId)
        {
            return TryGetStaticNodeId(scenePath, nodePath, out nodeId);
        }

        /// <summary>
        /// Unpack a node ID to its path within a scene.
        /// </summary>
        public static string UnpackNode(string scenePath, byte nodeId)
        {
            return GetStaticNodePath(scenePath, nodeId);
        }

        #endregion

        #region Static Method Invocation

        /// <summary>
        /// Invoke a static serialization method (NetworkSerialize, NetworkDeserialize, BsonDeserialize).
        /// Returns null if the method doesn't exist.
        /// </summary>
        public static object InvokeStaticMethod(ProtocolNetProperty prop, StaticMethodType methodType, params object[] args)
        {
            if (prop.ClassIndex < 0)
                return null;

            if (!GeneratedProtocol.StaticMethods.TryGetValue(prop.ClassIndex, out var methodInfo))
                return null;

            if ((methodInfo.MethodType & methodType) == 0)
                return null;

            var method = GetCachedMethod(methodInfo.TypeFullName, methodType.ToString());
            if (method == null)
                return null;

            return method.Invoke(null, args);
        }

        /// <summary>
        /// Get a Callable for a static method. For backwards compatibility with existing code.
        /// Returns null if the method doesn't exist.
        /// </summary>
        public static Callable? GetStaticMethodCallable(ProtocolNetProperty prop, StaticMethodType methodType)
        {
            if (prop.ClassIndex < 0)
                return null;

            if (!GeneratedProtocol.StaticMethods.TryGetValue(prop.ClassIndex, out var methodInfo))
                return null;

            if ((methodInfo.MethodType & methodType) == 0)
                return null;

            var type = GetCachedType(methodInfo.TypeFullName);
            if (type == null)
                return null;

            var methodName = methodType.ToString();
            return Callable.From((Func<object[], object>)(args => 
            {
                var method = GetCachedMethod(methodInfo.TypeFullName, methodName);
                return method?.Invoke(null, args);
            }));
        }

        /// <summary>
        /// Get a delegate for a static method. More efficient than Callable for hot paths.
        /// </summary>
        public static MethodInfo GetStaticMethod(ProtocolNetProperty prop, StaticMethodType methodType)
        {
            if (prop.ClassIndex < 0)
                return null;

            if (!GeneratedProtocol.StaticMethods.TryGetValue(prop.ClassIndex, out var methodInfo))
                return null;

            if ((methodInfo.MethodType & methodType) == 0)
                return null;

            return GetCachedMethod(methodInfo.TypeFullName, methodType.ToString());
        }

        /// <summary>
        /// Get a generated deserializer delegate for a property's type.
        /// This is the preferred method for deserialization - no reflection or boxing.
        /// </summary>
        /// <param name="classIndex">The class index from ProtocolNetProperty.ClassIndex</param>
        /// <returns>The deserializer delegate, or null if not found</returns>
        public static GeneratedProtocol.NetworkDeserializeFunc GetDeserializer(int classIndex)
        {
            return GeneratedProtocol.Deserializers.TryGetValue(classIndex, out var deserializer) ? deserializer : null;
        }

        /// <summary>
        /// Get a generated serializer delegate for a property's type.
        /// This is the preferred method for serialization - no reflection or boxing.
        /// </summary>
        /// <param name="classIndex">The class index from ProtocolNetProperty.ClassIndex</param>
        /// <returns>The serializer delegate, or null if not found</returns>
        public static GeneratedProtocol.NetworkSerializeFunc GetSerializer(int classIndex)
        {
            return GeneratedProtocol.Serializers.TryGetValue(classIndex, out var serializer) ? serializer : null;
        }

        /// <summary>
        /// Get a generated OnPeerAcknowledge delegate for an INetSerializable type.
        /// Only available for reference types (not INetValue).
        /// </summary>
        /// <param name="classIndex">The class index from ProtocolNetProperty.ClassIndex</param>
        /// <returns>The delegate, or null if not found or type is a value type</returns>
        public static GeneratedProtocol.OnPeerAcknowledgeFunc GetOnPeerAcknowledge(int classIndex)
        {
            return GeneratedProtocol.OnPeerAcknowledgeFuncs.TryGetValue(classIndex, out var func) ? func : null;
        }

        /// <summary>
        /// Get a generated OnPeerDisconnected delegate for an INetSerializable type.
        /// Only available for reference types (not INetValue).
        /// </summary>
        /// <param name="classIndex">The class index from ProtocolNetProperty.ClassIndex</param>
        /// <returns>The delegate, or null if not found or type is a value type</returns>
        public static GeneratedProtocol.OnPeerDisconnectedFunc GetOnPeerDisconnected(int classIndex)
        {
            return GeneratedProtocol.OnPeerDisconnectedFuncs.TryGetValue(classIndex, out var func) ? func : null;
        }

        private static MethodInfo GetCachedMethod(string typeName, string methodName)
        {
            var key = (typeName, methodName);
            if (_methodCache.TryGetValue(key, out var cached))
                return cached;

            var type = GetCachedType(typeName);
            if (type == null)
                return null;

            // FlattenHierarchy is required to find static methods from base classes
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            _methodCache[key] = method;
            return method;
        }

        private static readonly Dictionary<int, bool> _nodeRefClassCache = new();

        /// <summary>
        /// Whether this class index serializes a NODE REFERENCE — an id lookup — rather than
        /// in-place-mutated content.
        ///
        /// <para>The distinction decides whether the property may be gated on its dirty bit.
        /// Types like <c>NetArray&lt;T&gt;</c> and game snapshot objects are mutated in place, so
        /// their setter never fires, <c>MarkDirty</c> never runs, and the dirty mask cannot see the
        /// change — which is why the object write loop calls every object serializer every tick and
        /// lets each self-filter. A node reference is only ever ASSIGNED, and
        /// <see cref="NetworkController.MarkDirtyRef"/> already sets its bit, so the every-tick call
        /// is pure cost.</para>
        ///
        /// <para>Resolved from the type rather than a hardcoded list of the three node classes, so a
        /// property typed as any game subclass is covered — their <c>NetworkSerialize</c> is the
        /// inherited static one on NetNode/NetNode2D/NetNode3D.</para>
        /// </summary>
        public static bool IsNodeReferenceClass(int classIndex)
        {
            if (classIndex < 0) return false;
            if (_nodeRefClassCache.TryGetValue(classIndex, out var cached)) return cached;

            bool isNodeRef = false;
            if (GeneratedProtocol.StaticMethods.TryGetValue(classIndex, out var info))
            {
                var type = GetCachedType(info.TypeFullName);
                isNodeRef = type != null && typeof(INetNodeBase).IsAssignableFrom(type);
            }
            _nodeRefClassCache[classIndex] = isNodeRef;
            return isNodeRef;
        }

        private static readonly Dictionary<int, bool> _netArrayClassCache = new();

        /// <summary>
        /// Whether this class index is a <c>NetArray&lt;T&gt;</c>. NetArray content changes
        /// always pass through its indexer, which calls MarkDirty - so unlike other
        /// in-place-mutated object properties it has a real dirty signal, and a node whose
        /// only object props are NetArrays may be skipped while clean. (A write that
        /// bypassed the indexer would already fail to replicate today.)
        /// </summary>
        public static bool IsNetArrayClass(int classIndex)
        {
            if (classIndex < 0) return false;
            if (_netArrayClassCache.TryGetValue(classIndex, out var cached)) return cached;

            bool isNetArray = false;
            if (GeneratedProtocol.StaticMethods.TryGetValue(classIndex, out var info))
            {
                var type = GetCachedType(info.TypeFullName);
                isNetArray = type is { IsGenericType: true }
                    && type.GetGenericTypeDefinition() == typeof(NetArray<>);
            }
            _netArrayClassCache[classIndex] = isNetArray;
            return isNetArray;
        }

        private static Type GetCachedType(string typeName)
        {
            if (_typeCache.TryGetValue(typeName, out var cached))
                return cached;

            Type type = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName);
                if (type != null)
                    break;
            }

            _typeCache[typeName] = type;
            return type;
        }

        #endregion

        #region Type Conversion

        /// <summary>
        /// Convert SerialVariantType to Godot Variant.Type.
        /// </summary>
        public static Variant.Type ToGodotVariantType(SerialVariantType serialType)
        {
            return (Variant.Type)(int)serialType;
        }

        /// <summary>
        /// Convert Godot Variant.Type to SerialVariantType.
        /// </summary>
        public static SerialVariantType FromGodotVariantType(Variant.Type godotType)
        {
            return (SerialVariantType)(int)godotType;
        }

        #endregion

        #region Tooling / Editor Introspection

        // These enumerate the generated protocol tables for the editor's
        // inspector and Network Scenes dock. They ALLOCATE and sort, and are
        // not for runtime use — nothing on a tick path should call them.
        //
        // They also reflect the last successful C# build, not the current
        // editor state: GeneratedProtocol is source-generated from .tscn files
        // at compile time, so a freshly added [NetProperty] or a brand-new
        // NetScene does not appear until a rebuild.

        /// <summary>
        /// All registered network scene paths, sorted.
        /// </summary>
        public static IReadOnlyList<string> ListScenes()
        {
            var scenes = new List<string>(GeneratedProtocol.ScenesPack.Keys);
            scenes.Sort(StringComparer.Ordinal);
            return scenes;
        }

        /// <summary>
        /// Node paths within a scene that carry network state, "." first.
        /// </summary>
        public static IReadOnlyList<string> ListStaticNodes(string scenePath)
        {
            if (!GeneratedProtocol.StaticNetworkNodePathsMap.TryGetValue(scenePath, out var nodeMap))
                return Array.Empty<string>();

            var paths = new List<string>(nodeMap.Values);
            paths.Sort(static (a, b) =>
            {
                if (a == ".") return b == "." ? 0 : -1;
                if (b == ".") return 1;
                return string.CompareOrdinal(a, b);
            });
            return paths;
        }

        /// <summary>
        /// Network properties declared on a specific node of a scene.
        /// </summary>
        public static IReadOnlyList<ProtocolNetProperty> ListProperties(string scenePath, string nodePath)
        {
            if (!GeneratedProtocol.PropertiesMap.TryGetValue(scenePath, out var nodeMap))
                return Array.Empty<ProtocolNetProperty>();
            if (!nodeMap.TryGetValue(nodePath, out var propertyMap))
                return Array.Empty<ProtocolNetProperty>();

            var properties = new List<ProtocolNetProperty>(propertyMap.Values);
            properties.Sort(static (a, b) => a.Index.CompareTo(b.Index));
            return properties;
        }

        /// <summary>
        /// Network functions declared on a specific node of a scene.
        /// </summary>
        public static IReadOnlyList<ProtocolNetFunction> ListFunctions(string scenePath, string nodePath)
        {
            if (!GeneratedProtocol.FunctionsMap.TryGetValue(scenePath, out var nodeMap))
                return Array.Empty<ProtocolNetFunction>();
            if (!nodeMap.TryGetValue(nodePath, out var functionMap))
                return Array.Empty<ProtocolNetFunction>();

            var functions = new List<ProtocolNetFunction>(functionMap.Values);
            functions.Sort(static (a, b) => a.Index.CompareTo(b.Index));
            return functions;
        }

        #endregion
    }
}