using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Nebula.Utility.Tools;

namespace Nebula.Serialization.Serializers
{
    public partial class NetPropertiesSerializer : Node, IStateSerializer
    {
        private struct Data
        {
            public byte[] propertiesUpdated;
            public Dictionary<int, Variant> properties;
        }
        private NetNodeWrapper wrapper;

        private Dictionary<int, Variant> cachedPropertyChanges = new Dictionary<int, Variant>();
        public struct LerpableChangeQueue
        {
            public ProtocolNetProperty Prop;
            public Variant From;
            public Variant To;
            public double Weight;
        }

        private Dictionary<string, LerpableChangeQueue> lerpableChangeQueue = new Dictionary<string, LerpableChangeQueue>();

        private Dictionary<int, bool> propertyUpdated = new Dictionary<int, bool>();
        private Dictionary<int, bool> processingPropertiesUpdated = new Dictionary<int, bool>();

        /// <summary>
        /// This is used to keep track of properties which have changed before a client ever had interest in the node.
        /// </summary>
        private Dictionary<NetPeer, byte[]> peerInitialPropSync = new Dictionary<NetPeer, byte[]>();
        public override void _EnterTree()
        {
            wrapper = new NetNodeWrapper(GetParent());
            Name = "NetPropertiesSerializer";

            // First, determine if the Node class has the NetScene attribute
            // This is because only a network scene will serialize network node properties recursively
            if (!wrapper.IsNetScene())
            {
                return;
            }

            if (NetRunner.Instance.IsServer)
            {
                wrapper.Network.Connect("NetPropertyChanged", Callable.From((string nodePath, string propertyName) =>
                {
                    if (ProtocolRegistry.Instance.LookupProperty(wrapper.Node.SceneFilePath, nodePath, propertyName, out var prop))
                    {
                        propertyUpdated[prop.Index] = true;
                        nonDefaultProperties.Add(prop.Index);
                    }
                    else
                    {
                        Debugger.Instance.Log($"Property not found: {nodePath}:{propertyName}", Debugger.DebugLevel.ERROR);
                    }
                }));

                wrapper.Network.Connect("InterestChanged", Callable.From((UUID peerId, long interest) =>
                {
                    var peer = NetRunner.Instance.GetPeer(peerId);
                    if (peer != null && !peerInitialPropSync.ContainsKey(peer))
                    {
                        peerInitialPropSync.Remove(peer);
                    }
                }));
            }
            else
            {
                // As a client, apply all the cached changes
                foreach (var propIndex in cachedPropertyChanges.Keys)
                {
                    var prop = ProtocolRegistry.Instance.UnpackProperty(wrapper.Node.SceneFilePath, propIndex);
                    ImportProperty(prop, wrapper.CurrentWorld.CurrentTick, cachedPropertyChanges[propIndex]);
                }
            }

        }

        public void ImportProperty(ProtocolNetProperty prop, Tick tick, Variant value)
        {
            var propNode = wrapper.Node.GetNode(prop.NodePath);
            Variant oldVal = propNode.Get(prop.Name);
            if (oldVal.Equals(value))
            {
                return;
            }
            var friendlyPropName = prop.Name;
            if (friendlyPropName.StartsWith("network_"))
            {
                friendlyPropName = friendlyPropName["network_".Length..];
            }
            if (propNode.HasMethod("OnNetworkChange" + prop.Name))
            {
                propNode.Call("OnNetworkChange" + prop.Name, tick, oldVal, value);
            }
            else if (propNode.HasMethod("_on_network_change_" + friendlyPropName))
            {
                propNode.Call("_on_network_change_" + friendlyPropName, tick, oldVal, value);
            }

            var propId = $"{prop.NodePath}:{prop.Name}";

#if DEBUG
            var netProps = GetMeta("NETWORK_PROPS", new Godot.Collections.Dictionary()).AsGodotDictionary();
            netProps[propId] = value;
            SetMeta("NETWORK_PROPS", netProps);
#endif
            if (lerpableChangeQueue.TryGetValue(propId, out var lerpableChange))
            {
                lerpableChange.To = value;
            }
            else
            {
                lerpableChangeQueue[propId] = new LerpableChangeQueue
                {
                    Prop = prop,
                    From = oldVal,
                    To = value,
                    Weight = 0.0f
                };
            }
        }


        private Data Deserialize(HLBuffer buffer)
        {
            var data = new Data
            {
                propertiesUpdated = new byte[GetByteCountOfProperties()],
                properties = new Dictionary<int, Variant>()
            };
            for (byte i = 0; i < data.propertiesUpdated.Length; i++)
            {
                data.propertiesUpdated[i] = HLBytes.UnpackByte(buffer);
            }
            // GD.Print($"FOR {wrapper.Node.GetPath()}: Receiving prop updates: {BitConverter.ToString(data.propertiesUpdated)}");
            for (byte propertyByteIndex = 0; propertyByteIndex < data.propertiesUpdated.Length; propertyByteIndex++)
            {
                var propertyByte = data.propertiesUpdated[propertyByteIndex];
                for (byte propertyBit = 0; propertyBit < BitConstants.BitsInByte; propertyBit++)
                {
                    if ((propertyByte & (1 << propertyBit)) == 0)
                    {
                        continue;
                    }
                    var propertyIndex = propertyByteIndex * BitConstants.BitsInByte + propertyBit;
                    var prop = ProtocolRegistry.Instance.UnpackProperty(wrapper.Node.SceneFilePath, propertyIndex);
                    // GD.Print($"FOR {wrapper.Node.GetPath()}: Receiving prop update: {prop.Name}");
                    if (prop.VariantType == Variant.Type.Object)
                    {
                        var node = wrapper.Node.GetNode(prop.NodePath);
                        var propNode = node.Get(prop.Name).As<RefCounted>();
                        var callable = ProtocolRegistry.Instance.GetStaticMethodCallable(prop, StaticMethodType.NetworkDeserialize);
                        if (callable == null)
                        {
                            Debugger.Instance.Log($"No NetworkDeserialize method found for {prop.NodePath}.{prop.Name}", Debugger.DebugLevel.ERROR);
                            continue;
                        }
                        data.properties[propertyIndex] = callable.Value.Call(wrapper.CurrentWorld, new Variant(), buffer, propNode);
                    }
                    else
                    {
                        var varVal = HLBytes.UnpackVariant(buffer, knownType: prop.VariantType);
                        data.properties[propertyIndex] = varVal.Value;
                    }
                }
            }
            return data;
        }

        public void Begin()
        {
            processingPropertiesUpdated.Clear();
            // Copy propertyUpdated to processingPropertiesUpdated
            foreach (var propIndex in propertyUpdated.Keys)
            {
                processingPropertiesUpdated[propIndex] = propertyUpdated[propIndex];
            }
            propertyUpdated.Clear();
        }

        public void Import(WorldRunner currentWorld, HLBuffer buffer, out NetNodeWrapper nodeOut)
        {
            nodeOut = wrapper;

            var data = Deserialize(buffer);
            foreach (var propIndex in data.properties.Keys)
            {
                var prop = ProtocolRegistry.Instance.UnpackProperty(wrapper.Node.SceneFilePath, propIndex);
                if (wrapper.Node.IsNodeReady())
                {
                    ImportProperty(prop, currentWorld.CurrentTick, data.properties[propIndex]);
                }
                else
                {
                    cachedPropertyChanges[propIndex] = data.properties[propIndex];
                }
            }

            return;
        }

        private int GetByteCountOfProperties()
        {
            return (ProtocolRegistry.Instance.GetPropertyCount(wrapper.Node.SceneFilePath) / BitConstants.BitsInByte) + 1;
        }

        private byte[] OrByteList(byte[] a, byte[] b)
        {
            var result = new byte[a.Length];
            for (var i = 0; i < a.Length; i++)
            {
                result[i] = (byte)(a[i] | b[i]);
            }
            return result;
        }

        private HashSet<int> nonDefaultProperties = new HashSet<int>();

        private Dictionary<NetPeer, Dictionary<Tick, byte[]>> peerBufferCache = new Dictionary<NetPeer, Dictionary<Tick, byte[]>>();
        // This should instead be a map of variable values that we can resend until acknowledgement

        private byte[] FilterPropsAgainstInterest(NetPeer peer, byte[] props)
        {
            var result = (byte[])props.Clone();
            var peerId = NetRunner.Instance.GetPeerId(peer);
            if (!wrapper.InterestLayers.ContainsKey(peerId) || wrapper.InterestLayers[peerId] == 0)
            {
                return new byte[GetByteCountOfProperties()];
            }
            for (var i = 0; i < props.Length; i++)
            {
                for (var j = 0; j < BitConstants.BitsInByte; j++)
                {
                    if ((props[i] & (byte)(1 << j)) == 0)
                    {
                        continue;
                    }
                    var propIndex = i * BitConstants.BitsInByte + j;
                    var prop = ProtocolRegistry.Instance.UnpackProperty(wrapper.Node.SceneFilePath, propIndex);
                    if ((prop.InterestMask & wrapper.InterestLayers[peerId]) == 0)
                    {
                        result[i] &= (byte)~(1 << j);
                    }
                }
            }
            return result;
        }

        public HLBuffer Export(WorldRunner currentWorld, NetPeer peerId)
        {
            var buffer = new HLBuffer();
            if (!peerInitialPropSync.ContainsKey(peerId))
            {
                peerInitialPropSync[peerId] = new byte[GetByteCountOfProperties()];
            }
            if (!currentWorld.HasSpawnedForClient(wrapper.NetId, peerId))
            {
                // GD.Print("Client ", peerId, " has not spawned ", wrapper.Node.Name);
                // The target client is not aware of this node yet. Don't send updates.
                return buffer;
            }

            // Prepare our default values
            if (!peerBufferCache.ContainsKey(peerId))
            {
                peerBufferCache[peerId] = new Dictionary<Tick, byte[]>();
            }
            byte[] propertiesUpdated = new byte[GetByteCountOfProperties()];
            for (var i = 0; i < propertiesUpdated.Length; i++)
            {
                propertiesUpdated[i] = 0;
            }

            // Determine which properties have changed since the last update
            foreach (var propIndex in processingPropertiesUpdated.Keys)
            {
                propertiesUpdated[propIndex / BitConstants.BitsInByte] |= (byte)(1 << (propIndex % BitConstants.BitsInByte));
            }

            foreach (var propIndex in nonDefaultProperties)
            {
                var byteIndex = propIndex / BitConstants.BitsInByte;
                var propSlot = (byte)(1 << (propIndex % BitConstants.BitsInByte));
                if ((peerInitialPropSync[peerId][byteIndex] & propSlot) == 0)
                {
                    propertiesUpdated[byteIndex] |= propSlot;
                    peerInitialPropSync[peerId][byteIndex] |= propSlot;
                }
            }

            // Store them in the cache to resend in the future until the client acknowledges having received the update
            peerBufferCache[peerId][currentWorld.CurrentTick] = propertiesUpdated;

            // Now collect every pending property update
            var hasPendingUpdates = false;
            foreach (var tick in peerBufferCache[peerId].Keys.OrderBy(x => x))
            {
                var filteredProps = FilterPropsAgainstInterest(peerId, peerBufferCache[peerId][tick]);
                propertiesUpdated = OrByteList(propertiesUpdated, filteredProps);
                if (!hasPendingUpdates)
                {
                    for (var i = 0; i < propertiesUpdated.Length; i++)
                    {
                        if (propertiesUpdated[i] != 0)
                        {
                            hasPendingUpdates = true;
                            break;
                        }
                    }
                }
            }

            if (!hasPendingUpdates)
            {
                return buffer;
            }

            // GD.Print($"FOR {wrapper.Node.GetPath()}: Sending prop updates: {BitConverter.ToString(propertiesUpdated)}");

            for (var i = 0; i < propertiesUpdated.Length; i++)
            {
                var propSegment = propertiesUpdated[i];
                HLBytes.Pack(buffer, propSegment);
            }
            for (var i = 0; i < propertiesUpdated.Length; i++)
            {
                var propSegment = propertiesUpdated[i];
                // Finally, pack the variable values as byte segments
                for (var j = 0; j < BitConstants.BitsInByte; j++)
                {
                    if ((propSegment & (byte)(1 << j)) == 0)
                    {
                        continue;
                    }

                    var propIndex = i * BitConstants.BitsInByte + j;

                    var prop = ProtocolRegistry.Instance.UnpackProperty(wrapper.Node.SceneFilePath, propIndex);
                    var propNode = wrapper.Node.GetNode(prop.NodePath);
                    var varVal = propNode.Get(prop.Name);
                    if (prop.VariantType == Variant.Type.Object)
                    {
                        var callable = ProtocolRegistry.Instance.GetStaticMethodCallable(prop, StaticMethodType.NetworkSerialize);
                        if (callable == null)
                        {
                            Debugger.Instance.Log($"No NetworkSerialize method found for {prop.NodePath}.{prop.Name}", Debugger.DebugLevel.ERROR);
                            continue;
                        }
                        HLBytes.Pack(buffer, callable.Value.Call(currentWorld, peerId, varVal).As<HLBuffer>().bytes);
                    }
                    else
                    {
                        try {
                            HLBytes.PackVariant(buffer, varVal, packLength: true);
                        }
                        catch (Exception ex)
                        {
                            Debugger.Instance.Log($"Error packing variant {prop.NodePath}.{prop.Name}: {ex.Message}", Debugger.DebugLevel.ERROR);
                        }
                    }
                    // GD.Print($"New packed value for ${prop.NodePath}.${prop.Name}: {BitConverter.ToString(buffer.bytes)}");
                }
            }

            return buffer;
        }

        public void Cleanup() { }

        public void Acknowledge(WorldRunner currentWorld, NetPeer peerId, Tick latestAck)
        {
            if (!peerBufferCache.ContainsKey(peerId))
            {
                return;
            }
            foreach (var tick in peerBufferCache[peerId].Keys.Where(x => x <= latestAck).ToList())
            {
                peerBufferCache[peerId].Remove(tick);
            }
        }

        private static Vector3 Lerp(Vector3 First, Vector3 Second, float Amount)
        {
            float retX = Mathf.Lerp(First.X, Second.X, Amount);
            float retY = Mathf.Lerp(First.Y, Second.Y, Amount);
            float retZ = Mathf.Lerp(First.Z, Second.Z, Amount);
            return new Vector3(retX, retY, retZ);
        }

        public override void _Process(double delta)
        {
            if (NetRunner.Instance.IsServer)
            {
                return;
            }

            foreach (var queueKey in lerpableChangeQueue.Keys.ToList())
            {
                var toLerp = lerpableChangeQueue[queueKey];
                var lerpNode = wrapper.Node.GetNode(toLerp.Prop.NodePath);
                if (toLerp.Weight < 1.0)
                {
                    // TODO: If this is too fast, it creates a jitter effect
                    // But if it's too slow it creates a lag / swimming effect
                    toLerp.Weight = Math.Min(toLerp.Weight + delta * 10, 1.0);
                    double result = -1;
                    if (lerpNode.HasMethod("NetworkLerp" + toLerp.Prop.Name))
                    {
                        result = (double)lerpNode.Call("NetworkLerp" + toLerp.Prop.Name, toLerp.From, toLerp.To, toLerp.Weight);
                        if (result >= 0)
                        {
                            toLerp.Weight = result;
                        }
                    }
                    if (result == -1)
                    {
                        if (toLerp.Prop.VariantType == Variant.Type.Quaternion)
                        {
                            var next_value = ((Quaternion)toLerp.From).Normalized().Slerp(((Quaternion)toLerp.To).Normalized(), (float)toLerp.Weight);
                            lerpNode.Set(toLerp.Prop.Name, next_value);
                        }
                        else if (toLerp.Prop.VariantType == Variant.Type.Vector3)
                        {
                            var next_value = Lerp((Vector3)toLerp.From, (Vector3)toLerp.To, (float)toLerp.Weight);
                            lerpNode.Set(toLerp.Prop.Name, next_value);
                        }
                        else
                        {
                            toLerp.Weight = 1.0;
                        }
                    }

                    if ((float)toLerp.Weight >= 1.0)
                    {
                        lerpNode.Set(toLerp.Prop.Name, toLerp.To);
                        lerpableChangeQueue.Remove(queueKey);
                        continue;
                    }
                }
                lerpableChangeQueue[queueKey] = toLerp;
            }
        }
    }
}