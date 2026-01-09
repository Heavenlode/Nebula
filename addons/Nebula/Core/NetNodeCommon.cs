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
                Debugger.Instance.Log($"Only network scenes can be converted to BSON: {network.RawNode.GetPath()} with scene {network.RawNode.SceneFilePath}", Debugger.DebugLevel.ERROR);
            }
            BsonDocument result = new BsonDocument();
            result["data"] = new BsonDocument();
            result["scene"] = network.RawNode.SceneFilePath;
            // We retain this for debugging purposes.
            result["nodeName"] = network.RawNode.Name.ToString();

            // foreach (var node in Protocol.ListProperties(network.RawNode.SceneFilePath))
            // {
            //     var nodePath = node["nodePath"].AsString();
            //     var nodeProps = node["properties"].As<Godot.Collections.Array<ProtocolNetProperty>>();
            //     result["data"][nodePath] = new BsonDocument();
            //     var nodeData = result["data"][nodePath] as BsonDocument;
            //     var hasValues = false;
            //     foreach (var property in nodeProps)
            //     {
            //         var prop = network.RawNode.GetNode(nodePath).Get(property.Name);
            //         var val = BsonTransformer.Instance.SerializeVariant(prop, property.Metadata.TypeIdentifier);
            //         if (val == null) continue;
            //         nodeData[property.Name] = val;
            //         hasValues = true;
            //     }

            //     if (!hasValues)
            //     {
            //         // Delete empty objects from JSON, i.e. network nodes with no network properties.
            //         (result["data"] as BsonDocument).Remove(nodePath);
            //     }
            // }

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
                var targetNode = node.GetNodeOrNull<INetNodeBase>(nodePath);
                if (targetNode == null)
                {
                    Debugger.Instance.Log($"Node not found for: ${nodePath}", Debugger.DebugLevel.ERROR);
                    continue;
                }
                targetNode.Network.NetParent = node.Network;
                foreach (var prop in nodeProps)
                {
                    node.Network.InitialSetNetProperties.Add(new Tuple<string, string>(nodePath, prop.Name));
                    ProtocolNetProperty propData;
                    if (!Protocol.LookupProperty(node.SceneFilePath, nodePath, prop.Name, out propData))
                    {
                        throw new Exception($"Failed to unpack property: {nodePath}.{prop.Name}");
                    }
                    var variantType = propData.VariantType;
                    try
                    {
                        if (variantType == SerialVariantType.String)
                        {
                            targetNode.Network.RawNode.Set(prop.Name, prop.Value.ToString());
                        }
                        else if (variantType == SerialVariantType.Float)
                        {
                            targetNode.Network.RawNode.Set(prop.Name, prop.Value.AsDouble);
                        }
                        else if (variantType == SerialVariantType.Int)
                        {
                            if (propData.Metadata.TypeIdentifier == "Int")
                            {
                                targetNode.Network.RawNode.Set(prop.Name, prop.Value.AsInt32);
                            }
                            else if (propData.Metadata.TypeIdentifier == "Byte")
                            {
                                // Convert MongoDB Binary value to Byte
                                targetNode.Network.RawNode.Set(prop.Name, (byte)prop.Value.AsInt32);
                            }
                            else if (propData.Metadata.TypeIdentifier == "Short")
                            {
                                targetNode.Network.RawNode.Set(prop.Name, (short)prop.Value.AsInt32);
                            }
                            else
                            {
                                targetNode.Network.RawNode.Set(prop.Name, prop.Value.AsInt64);
                            }
                        }
                        else if (variantType == SerialVariantType.Bool)
                        {
                            targetNode.Network.RawNode.Set(prop.Name, (bool)prop.Value);
                        }
                        else if (variantType == SerialVariantType.Vector2)
                        {
                            var vec = prop.Value as BsonArray;
                            targetNode.Network.RawNode.Set(prop.Name, new Vector2((float)vec[0].AsDouble, (float)vec[1].AsDouble));
                        }
                        else if (variantType == SerialVariantType.PackedByteArray)
                        {
                            targetNode.Network.RawNode.Set(prop.Name, prop.Value.AsByteArray);
                        }
                        else if (variantType == SerialVariantType.PackedInt64Array)
                        {
                            targetNode.Network.RawNode.Set(prop.Name, prop.Value.AsBsonArray.Select(x => x.AsInt64).ToArray());
                        }
                        else if (variantType == SerialVariantType.Vector3)
                        {
                            var vec = prop.Value as BsonArray;
                            targetNode.Network.RawNode.Set(prop.Name, new Vector3((float)vec[0].AsDouble, (float)vec[1].AsDouble, (float)vec[2].AsDouble));
                        }
                        else if (variantType == SerialVariantType.Object)
                        {
                            // For complex object types, we need to handle them specially
                            // This path requires the Protocol to have registered a BSON deserializer
                            Debugger.Instance.Log($"Object type deserialization not yet implemented for new context: {nodePath}.{prop.Name}", Debugger.DebugLevel.WARN);
                        }
                    }
                    catch (InvalidCastException e)
                    {
                        Debugger.Instance.Log($"Failed to set property: {prop.Name} on {nodePath} with value: {prop.Value} and type: {variantType}. {e.Message}", Debugger.DebugLevel.ERROR);
                    }
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
                            Debugger.Instance.Log($"Parent node not found for: {nodePath}", Debugger.DebugLevel.ERROR);
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
