using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MongoDB.Bson;
using Nebula.Serialization;
using Nebula.Utility.Tools;

namespace Nebula.Utility
{
    /// <summary>
    /// This class contains methods for serializing and deserializing network nodes to and from BSON.
    /// The logic is extracted to this utility class to reuse it across <see cref="NetNode"/>, <see cref="NetNode2D"/>, and <see cref="NetNode3D"/>.
    /// </summary>
    internal static class NetNodeCommon
    {
        readonly public static BsonDocument NullBsonDocument = new BsonDocument("value", BsonNull.Value);

        internal static BsonDocument ToBSONDocument(
            INetNodeBase netNode,
            NetBsonContext context = default
        )
        {
            var network = netNode.Network;
            if (!network.IsNetScene())
            {
                // Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Only network scenes can be converted to BSON: {network.RawNode.GetPath()} with scene {network.RawNode.SceneFilePath}");
            }
            BsonDocument result = new BsonDocument();
            result["data"] = new BsonDocument();
            result["scene"] = network.RawNode.SceneFilePath;
            // We retain this for debugging purposes.
            result["nodeName"] = network.RawNode.Name.ToString();

            if (GeneratedProtocol.PropertiesMap.TryGetValue(network.RawNode.SceneFilePath, out var nodeMap))
            {
                foreach (var nodeEntry in nodeMap)
                {
                    var nodePath = nodeEntry.Key;
                    
                    // Get the target node
                    var targetNode = network.RawNode.GetNodeOrNull(nodePath);
                    if (targetNode == null) continue;
                    
                    var nodeData = new BsonDocument();
                    
                    // Call WriteBsonProperties through concrete base types to use virtual dispatch
                    // (not interface dispatch, which would call the empty default implementation)
                    if (targetNode is NetNode3D nn3d)
                        nn3d.WriteBsonProperties(nodeData, context);
                    else if (targetNode is NetNode2D nn2d)
                        nn2d.WriteBsonProperties(nodeData, context);
                    else if (targetNode is NetNode nn)
                        nn.WriteBsonProperties(nodeData, context);
                    
                    // Only add if there are actual properties
                    if (nodeData.ElementCount > 0)
                    {
                        result["data"][nodePath] = nodeData;
                    }
                }
            }

            if (context.Recurse)
            {
                result["children"] = new BsonDocument();
                foreach (var child in network.DynamicNetworkChildren)
                {
                    if (context.NodeFilter != null && !context.NodeFilter(child.RawNode))
                    {
                        continue;
                    }
                    string pathTo = network.RawNode.GetPathTo(child.RawNode.GetParent());
                    if (!result["children"].AsBsonDocument.Contains(pathTo))
                    {
                        result["children"][pathTo] = new BsonArray();
                    }
                    result["children"][pathTo].AsBsonArray.Add(ToBSONDocument(child.NetNode, context));
                }
            }

            return result;
        }

        internal static async Task<T> FromBSON<T>(NetBsonContext context, BsonDocument data, T fillNode = null) where T : Node, INetNodeBase
        {
            T node = fillNode;
            if (fillNode == null)
            {
                if (data.Contains("scene"))
                {
                    // Instantiate the scene naturally, then cast to T
                    // This allows the scene to create the correct derived type
                    var sceneInstance = GD.Load<PackedScene>(data["scene"].AsString).Instantiate();
                    node = sceneInstance as T;
                    if (node == null)
                    {
                        throw new System.Exception($"Scene {data["scene"].AsString} does not contain a node of type {typeof(T).Name}");
                    }
                }
                else
                {
                    throw new System.Exception($"No scene path found in BSON data: {data.ToJson()}");
                }
            }

            // Mark imported nodes accordingly
            if (!node.GetMeta("import_from_external", false).AsBool())
            {
                var tcs = new TaskCompletionSource<bool>();
                // Create the event handler as a separate method so we can disconnect it later
                Action treeEnteredHandler = () =>
                {
                    foreach (var dyanmicChild in node.Network.DynamicNetworkChildren)
                    {
                        dyanmicChild.RawNode.Free();
                    }
                    foreach (var staticChild in node.Network.StaticNetworkChildren)
                    {
                        if (staticChild == null) continue;
                        staticChild.RawNode.SetMeta("import_from_external", true);
                    }
                    node.SetMeta("import_from_external", true);
                    tcs.SetResult(true);
                };

                node.TreeEntered += treeEnteredHandler;
                NetRunner.Instance.AddChild(node);
                await tcs.Task;
                NetRunner.Instance.RemoveChild(node);
                // Disconnect the TreeEntered event handler before removing the child
                node.TreeEntered -= treeEnteredHandler;
            }

            if (data.Contains("nodeName"))
            {
                node.Name = data["nodeName"].AsString;
            }

            foreach (var netNodePathAndProps in data["data"] as BsonDocument)
            {
                var nodePath = netNodePathAndProps.Name;
                var nodeProps = netNodePathAndProps.Value as BsonDocument;
                var targetNode = node.GetNodeOrNull(nodePath);
                if (targetNode == null)
                {
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Node not found for: ${nodePath}");
                    continue;
                }
                
                // Get the INetNodeBase interface for network setup
                if (targetNode is INetNodeBase netNodeBase)
                {
                    netNodeBase.Network.NetParent = node.Network;
                }
                
                // Track which properties are being set for network initialization
                foreach (var prop in nodeProps)
                {
                    node.Network.InitialSetNetProperties.Add(new Tuple<string, string>(nodePath, prop.Name));
                }
                
                // Call ReadBsonProperties through concrete base types to use virtual dispatch
                // (not interface dispatch, which would call the empty default implementation)
                try
                {
                    if (targetNode is NetNode3D nn3d)
                        nn3d.ReadBsonProperties(nodeProps);
                    else if (targetNode is NetNode2D nn2d)
                        nn2d.ReadBsonProperties(nodeProps);
                    else if (targetNode is NetNode nn)
                        nn.ReadBsonProperties(nodeProps);
                }
                catch (Exception e)
                {
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Failed to read BSON properties for {nodePath}: {e.Message}");
                }
            }
            if (data.Contains("children"))
            {
                foreach (var child in data["children"] as BsonDocument)
                {
                    var nodePath = child.Name;
                    var children = child.Value as BsonArray;
                    if (children == null)
                    {
                        continue;
                    }
                    foreach (var childData in children)
                    {
                        var childNode = await FromBSON<T>(context, childData as BsonDocument);
                        var parent = node.GetNodeOrNull(nodePath);
                        if (parent == null)
                        {
                            Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Parent node not found for: {nodePath}");
                            continue;
                        }
                        parent.AddChild(childNode);
                    }
                }
            }
            return node;
        }

    }
}
