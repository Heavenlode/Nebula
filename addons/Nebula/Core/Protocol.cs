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
        /// Unpack a scene ID to its PackedScene.
        /// </summary>
        public static PackedScene UnpackScene(byte sceneId)
        {
            if (GeneratedProtocol.ScenesMap.TryGetValue(sceneId, out var path))
                return GD.Load<PackedScene>(path);
            throw new KeyNotFoundException($"Scene ID not found in protocol: {sceneId}");
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
    }
}