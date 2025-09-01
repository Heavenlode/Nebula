using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Nebula.Serialization;
using Nebula.Utility.Tools;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace Nebula.Serialization
{
    public partial class BsonTransformer : Node
    {

        /// <summary>
        /// The singleton instance.
        /// </summary>
        public static BsonTransformer Instance { get; internal set; }

        /// <inheritdoc/>
        public override void _EnterTree()
        {
            if (Instance != null)
            {
                QueueFree();
            }
            Instance = this;
        }

        public byte[] SerializeBsonValue(BsonValue value)
        {
            var wrapper = new BsonDocument("value", value);

            using (var memoryStream = new MemoryStream())
            {
                using (var writer = new BsonBinaryWriter(memoryStream))
                {
                    BsonSerializer.Serialize(writer, typeof(BsonDocument), wrapper);
                }
                return memoryStream.ToArray();
            }
        }

        public T DeserializeBsonValue<T>(byte[] bytes) where T : BsonValue
        {
            using (var memoryStream = new MemoryStream(bytes))
            {
                using (var reader = new BsonBinaryReader(memoryStream))
                {
                    var wrapper = BsonSerializer.Deserialize<BsonDocument>(reader);
                    BsonValue value = wrapper["value"];

                    if (typeof(T) == typeof(BsonValue))
                    {
                        // If requesting base BsonValue type, return as is
                        return (T)value;
                    }

                    // Check if the actual type matches the requested type
                    if (IsCompatibleType<T>(value))
                    {
                        // Convert to the requested type
                        return ConvertToType<T>(value);
                    }

                    throw new InvalidCastException(
                        $"Cannot convert BsonValue of type {value.BsonType} to {typeof(T).Name}: {value.ToJson()}");
                }
            }
        }

        private bool IsCompatibleType<T>(BsonValue value) where T : BsonValue
        {
            if (typeof(T) == typeof(BsonDocument))
                return value.IsBsonDocument;
            else if (typeof(T) == typeof(BsonBinaryData))
                return value.IsBsonBinaryData;
            else if (typeof(T) == typeof(BsonString))
                return value.IsString;
            else if (typeof(T) == typeof(BsonInt32))
                return value.IsInt32;
            else if (typeof(T) == typeof(BsonInt64))
                return value.IsInt64;
            else if (typeof(T) == typeof(BsonDouble))
                return value.IsDouble;
            else if (typeof(T) == typeof(BsonBoolean))
                return value.IsBoolean;
            else if (typeof(T) == typeof(BsonDateTime))
                return value.IsBsonDateTime;
            else if (typeof(T) == typeof(BsonArray))
                return value.IsBsonArray;
            else if (typeof(T) == typeof(BsonObjectId))
                return value.IsObjectId;
            else if (typeof(T) == typeof(BsonNull))
                return value.IsBsonNull;
            // Add other types as needed

            return false;
        }

        private T ConvertToType<T>(BsonValue value) where T : BsonValue
        {
            if (typeof(T) == typeof(BsonDocument))
                return (T)(BsonValue)value.AsBsonDocument;
            else if (typeof(T) == typeof(BsonBinaryData))
                return (T)(BsonValue)value.AsBsonBinaryData;
            else if (typeof(T) == typeof(BsonString))
                return (T)(BsonValue)value.AsString;
            else if (typeof(T) == typeof(BsonInt32))
                return (T)(BsonValue)value.AsInt32;
            else if (typeof(T) == typeof(BsonInt64))
                return (T)(BsonValue)value.AsInt64;
            else if (typeof(T) == typeof(BsonDouble))
                return (T)(BsonValue)value.AsDouble;
            else if (typeof(T) == typeof(BsonBoolean))
                return (T)(BsonValue)value.AsBoolean;
            else if (typeof(T) == typeof(BsonDateTime))
                return (T)(BsonValue)value.AsBsonDateTime;
            else if (typeof(T) == typeof(BsonArray))
                return (T)(BsonValue)value.AsBsonArray;
            else if (typeof(T) == typeof(BsonObjectId))
                return (T)(BsonValue)value.AsObjectId;
            else if (typeof(T) == typeof(BsonNull))
                return (T)(BsonValue)value.AsBsonNull;

            throw new InvalidCastException(
                $"Conversion from {value.BsonType} to {typeof(T).Name} is not implemented");
        }

        public BsonDocument ToBSONDocument(
            INetNode netNode,
            Variant context = new Variant(),
            bool recurse = true,
            HashSet<Type> skipNodeTypes = null,
            HashSet<Tuple<Variant.Type, string>> propTypes = null,
            HashSet<Tuple<Variant.Type, string>> skipPropTypes = null
        )
        {
            var network = netNode.Network;
            if (!network.IsNetScene())
            {
                throw new System.Exception("Only network scenes can be converted to BSON: " + netNode.Node.GetPath());
            }
            BsonDocument result = new BsonDocument();
            result["data"] = new BsonDocument();
            if (network.IsNetScene())
            {
                result["scene"] = netNode.Node.SceneFilePath;
            }
            // We retain this for debugging purposes.
            result["nodeName"] = netNode.Node.Name.ToString();

            foreach (var node in ProtocolRegistry.Instance.ListProperties(netNode.Node.SceneFilePath))
            {
                var nodePath = node["nodePath"].AsString();
                var nodeProps = node["properties"].As<Godot.Collections.Array<ProtocolNetProperty>>();
                result["data"][nodePath] = new BsonDocument();
                var nodeData = result["data"][nodePath] as BsonDocument;
                var hasValues = false;
                foreach (var property in nodeProps)
                {
                    if (propTypes != null && !propTypes.Contains(new Tuple<Variant.Type, string>(property.VariantType, property.Metadata.TypeIdentifier)))
                    {
                        continue;
                    }
                    if (skipPropTypes != null && skipPropTypes.Contains(new Tuple<Variant.Type, string>(property.VariantType, property.Metadata.TypeIdentifier)))
                    {
                        continue;
                    }
                    var prop = netNode.Node.GetNode(nodePath).Get(property.Name);
                    var val = SerializeVariant(context, prop, property.Metadata.TypeIdentifier);
                    // Debugger.Instance.Log($"Serializing: {nodePath}.{property.Name} with value: {val}", Debugger.DebugLevel.VERBOSE);
                    if (val == null) continue;
                    nodeData[property.Name] = val;
                    hasValues = true;
                }

                if (!hasValues)
                {
                    // Delete empty objects from JSON, i.e. network nodes with no network properties.
                    (result["data"] as BsonDocument).Remove(nodePath);
                }
            }

            if (recurse)
            {
                result["children"] = new BsonDocument();
                foreach (var child in network.NetSceneChildren)
                {
                    if (child.Node is INetNode networkChild && (skipNodeTypes == null || !skipNodeTypes.Contains(networkChild.GetType())))
                    {
                        string pathTo = netNode.Node.GetPathTo(networkChild.Node);
                        result["children"][pathTo] = ToBSONDocument(networkChild, context, recurse, skipNodeTypes, propTypes, skipPropTypes);
                    }
                }
            }

            return result;
        }


        public async void FromBSON(ProtocolRegistry protocolRegistry, Variant context, byte[] data, NetNode3D fillNode)
        {
            await FromBSON(protocolRegistry, context, BsonSerializer.Deserialize<BsonDocument>(data), fillNode);
        }

        public async Task<T> FromBSON<T>(ProtocolRegistry protocolRegistry, Variant context, byte[] data, T fillNode = null) where T : Node, INetNode
        {
            return await FromBSON(protocolRegistry, context, BsonSerializer.Deserialize<BsonDocument>(data), fillNode);
        }

        public async Task<T> FromBSON<T>(ProtocolRegistry protocolRegistry, Variant context, BsonDocument data, T fillNode = null) where T : Node, INetNode
        {
            T node = fillNode;
            if (fillNode == null)
            {
                if (data.Contains("scene"))
                {
                    node = GD.Load<PackedScene>(data["scene"].AsString).Instantiate<T>();
                }
                else
                {
                    throw new System.Exception("No scene path found in BSON data");
                }
            }

            // Mark imported nodes accordingly
            if (!node.GetMeta("import_from_external", false).AsBool())
            {
                var tcs = new TaskCompletionSource<bool>();
                node.Ready += () =>
                {
                    foreach (var child in node.Network.GetNetworkChildren(NetworkController.NetworkChildrenSearchToggle.INCLUDE_SCENES))
                    {
                        if (child.IsNetScene())
                        {
                            child.Node.QueueFree();
                        }
                        else
                        {
                            child.Node.SetMeta("import_from_external", true);
                        }
                    }
                    node.SetMeta("import_from_external", true);
                    tcs.SetResult(true);
                };
                NetRunner.Instance.AddChild(node);
                await tcs.Task;
                NetRunner.Instance.RemoveChild(node);
            }

            foreach (var netNodePathAndProps in data["data"] as BsonDocument)
            {
                var nodePath = netNodePathAndProps.Name;
                var nodeProps = netNodePathAndProps.Value as BsonDocument;
                var targetNode = node.GetNodeOrNull(nodePath);
                if (targetNode == null)
                {
                    Debugger.Instance.Log($"Node not found for: ${nodePath}", Debugger.DebugLevel.ERROR);
                    continue;
                }
                foreach (var prop in nodeProps)
                {
                    node.Network.InitialSetNetProperties.Add(new Tuple<string, string>(nodePath, prop.Name));
                    ProtocolNetProperty propData;
                    if (!protocolRegistry.LookupProperty(node.SceneFilePath, nodePath, prop.Name, out propData))
                    {
                        throw new Exception($"Failed to unpack property: {nodePath}.{prop.Name}");
                    }
                    var variantType = propData.VariantType;
                    try
                    {
                        if (variantType == Variant.Type.String)
                        {
                            targetNode.Set(prop.Name, prop.Value.ToString());
                        }
                        else if (variantType == Variant.Type.Float)
                        {
                            targetNode.Set(prop.Name, prop.Value.AsDouble);
                        }
                        else if (variantType == Variant.Type.Int)
                        {
                            if (propData.Metadata.TypeIdentifier == "Int")
                            {
                                targetNode.Set(prop.Name, prop.Value.AsInt32);
                            }
                            else if (propData.Metadata.TypeIdentifier == "Byte")
                            {
                                // Convert MongoDB Binary value to Byte
                                targetNode.Set(prop.Name, (byte)prop.Value.AsInt32);
                            }
                            else if (propData.Metadata.TypeIdentifier == "Short")
                            {
                                targetNode.Set(prop.Name, (short)prop.Value.AsInt32);
                            }
                            else
                            {
                                targetNode.Set(prop.Name, prop.Value.AsInt64);
                            }
                        }
                        else if (variantType == Variant.Type.Bool)
                        {
                            targetNode.Set(prop.Name, (bool)prop.Value);
                        }
                        else if (variantType == Variant.Type.Vector2)
                        {
                            var vec = prop.Value as BsonArray;
                            targetNode.Set(prop.Name, new Vector2((float)vec[0].AsDouble, (float)vec[1].AsDouble));
                        }
                        else if (variantType == Variant.Type.PackedByteArray)
                        {
                            targetNode.Set(prop.Name, prop.Value.AsByteArray);
                        }
                        else if (variantType == Variant.Type.PackedInt64Array)
                        {
                            targetNode.Set(prop.Name, prop.Value.AsBsonArray.Select(x => x.AsInt64).ToArray());
                        }
                        else if (variantType == Variant.Type.Vector3)
                        {
                            var vec = prop.Value as BsonArray;
                            targetNode.Set(prop.Name, new Vector3((float)vec[0].AsDouble, (float)vec[1].AsDouble, (float)vec[2].AsDouble));
                        }
                        else if (variantType == Variant.Type.Object)
                        {
                            var callable = ProtocolRegistry.Instance.GetStaticMethodCallable(propData, StaticMethodType.BsonDeserialize);
                            if (callable == null)
                            {
                                Debugger.Instance.Log($"No BsonDeserialize method found for {nodePath}.{prop.Name}", Debugger.DebugLevel.ERROR);
                                continue;
                            }
                            var value = callable.Value.Call(context, SerializeBsonValue(prop.Value), targetNode.Get(prop.Name).AsGodotObject());
                            targetNode.Set(prop.Name, value);
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
                        var childNode = await FromBSON<T>(protocolRegistry, context, childData as BsonDocument);
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

        public BsonValue SerializeVariant(Variant context, Variant variant, string subtype = "None")
        {
            if (variant.VariantType == Variant.Type.String)
            {
                return variant.ToString();
            }
            else if (variant.VariantType == Variant.Type.Float)
            {
                return variant.AsDouble();
            }
            else if (variant.VariantType == Variant.Type.Int)
            {
                if (subtype == "Byte")
                {
                    return variant.AsByte();
                }
                else if (subtype == "Int")
                {
                    return variant.AsInt32();
                }
                else
                {
                    return variant.AsInt64();
                }
            }
            else if (variant.VariantType == Variant.Type.Bool)
            {
                return variant.AsBool();
            }
            else if (variant.VariantType == Variant.Type.Vector2)
            {
                var vec = variant.As<Vector2>();
                return new BsonArray { vec.X, vec.Y };
            }
            else if (variant.VariantType == Variant.Type.Vector3)
            {
                var vec = variant.As<Vector3>();
                return new BsonArray { vec.X, vec.Y, vec.Z };
            }
            else if (variant.VariantType == Variant.Type.Nil)
            {
                return BsonNull.Value;
            }
            else if (variant.VariantType == Variant.Type.Object)
            {
                var obj = variant.As<GodotObject>();
                if (obj == null)
                {
                    return BsonNull.Value;
                }
                else
                {
                    if (obj is IBsonSerializableBase bsonSerializable)
                    {
                        return bsonSerializable.BsonSerialize(context);
                    }

                    Debugger.Instance.Log($"Object does not implement IBsonSerializable<T>: {obj}", Debugger.DebugLevel.ERROR);
                    return null;
                }
            }
            else if (variant.VariantType == Variant.Type.PackedByteArray)
            {
                return new BsonBinaryData(variant.AsByteArray(), BsonBinarySubType.Binary);
            }
            else if (variant.VariantType == Variant.Type.PackedInt32Array)
            {
                return new BsonArray(variant.AsInt32Array());
            }
            else if (variant.VariantType == Variant.Type.PackedInt64Array)
            {
                return new BsonArray(variant.AsInt64Array());
            }
            else if (variant.VariantType == Variant.Type.Dictionary)
            {
                var dict = variant.AsGodotDictionary();
                var bsonDict = new BsonDocument();
                foreach (var key in dict.Keys)
                {
                    bsonDict[key.ToString()] = SerializeVariant(context, dict[key]);
                }
                return bsonDict;
            }
            else
            {
                Debugger.Instance.Log($"Serializing to JSON unsupported property type: {variant.VariantType}", Debugger.DebugLevel.ERROR);
                return null;
            }
        }
    }
}