using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Nebula.Utility.Tools;

namespace Nebula.Serialization.Serializers
{
    public partial class NetPropertiesSerializer : RefCounted, IStateSerializer
    {
        private struct Data
        {
            public byte[] propertiesUpdated;
            public Dictionary<int, Variant> properties;
        }

        private NetworkController network;
        private Dictionary<int, Variant> cachedPropertyChanges = new();

        #region Interpolation State

        private struct SmoothState
        {
            public Variant Target;
        }
        private Dictionary<string, SmoothState> smoothStates = new();

        private struct BufferedState
        {
            public Tick Tick;
            public Variant Value;
        }
        private Dictionary<string, List<BufferedState>> bufferedStates = new();
        private const int MaxBufferSize = 30;

        #endregion

        private Dictionary<int, bool> propertyUpdated = new();
        private Dictionary<int, bool> processingPropertiesUpdated = new();

        private double TickInterval => 1.0 / NetRunner.TPS;

        private Dictionary<NetPeer, byte[]> peerInitialPropSync = new();

        public NetPropertiesSerializer(NetworkController _network)
        {
            network = _network;

            if (!network.IsNetScene())
            {
                return;
            }

            int byteCount = GetByteCountOfProperties();
            if (_propertiesUpdated == null || _propertiesUpdated.Length != byteCount)
            {
                _propertiesUpdated = new byte[byteCount];
                _filteredProps = new byte[byteCount];
            }

            if (NetRunner.Instance.IsServer)
            {
                network.RawNode.Connect("NetPropertyChanged", Callable.From((string nodePath, string propertyName) =>
                {
                    if (Protocol.LookupProperty(network.RawNode.SceneFilePath, nodePath, propertyName, out var prop))
                    {
                        propertyUpdated[prop.Index] = true;
                        nonDefaultProperties.Add(prop.Index);
                    }
                    else
                    {
                        Debugger.Instance.Log($"Property not found: {nodePath}:{propertyName}", Debugger.DebugLevel.ERROR);
                    }
                }));

                network.RawNode.Connect("InterestChanged", Callable.From((UUID peerId, long oldInterest, long newInterest) =>
                {
                    var peer = NetRunner.Instance.GetPeer(peerId);
                    if (peer == null || !peerInitialPropSync.ContainsKey(peer))
                        return;

                    foreach (var propIndex in nonDefaultProperties)
                    {
                        var prop = Protocol.UnpackProperty(network.RawNode.SceneFilePath, propIndex);

                        bool wasVisible = (prop.InterestMask & oldInterest) != 0;
                        bool isNowVisible = (prop.InterestMask & newInterest) != 0;

                        if (!wasVisible && isNowVisible)
                        {
                            ClearBit(peerInitialPropSync[peer], propIndex);

                            if (peerBufferCache.TryGetValue(peer, out var tickCache))
                            {
                                foreach (var mask in tickCache.Values)
                                {
                                    ClearBit(mask, propIndex);
                                }
                            }
                        }
                    }
                }));
            }
            else
            {
                foreach (var propIndex in cachedPropertyChanges.Keys)
                {
                    var prop = Protocol.UnpackProperty(network.RawNode.SceneFilePath, propIndex);
                    ImportProperty(prop, network.CurrentWorld.CurrentTick, cachedPropertyChanges[propIndex]);
                }
            }
        }

        public void ImportProperty(ProtocolNetProperty prop, Tick tick, Variant value)
        {
            var propNode = network.RawNode.GetNode(prop.NodePath);
            var propId = $"{prop.NodePath}:{prop.Name}";

            // Fire change callbacks
            Variant oldVal = propNode.Get(prop.Name);
            if (!oldVal.Equals(value))
            {
                var friendlyPropName = prop.Name;
                if (friendlyPropName.StartsWith("network_"))
                {
                    friendlyPropName = friendlyPropName["network_".Length..];
                }
                if (propNode.HasSignal("OnNetworkChange" + prop.Name))
                {
                    propNode.EmitSignal("OnNetworkChange" + prop.Name, tick, oldVal, value);
                }
                else if (propNode.HasMethod("OnNetworkChange" + prop.Name))
                {
                    propNode.Call("OnNetworkChange" + prop.Name, tick, oldVal, value);
                }
                else if (propNode.HasMethod("_on_network_change_" + friendlyPropName))
                {
                    propNode.Call("_on_network_change_" + friendlyPropName, tick, oldVal, value);
                }
            }

#if DEBUG
            var netProps = GetMeta("NETWORK_PROPS", new Godot.Collections.Dictionary()).AsGodotDictionary();
            netProps[propId] = value;
            SetMeta("NETWORK_PROPS", netProps);
#endif

            switch (prop.LerpMode)
            {
                case NetLerpMode.None:
                    propNode.Set(prop.Name, value);
                    break;

                case NetLerpMode.Smooth:
                    smoothStates[propId] = new SmoothState { Target = value };
                    break;

                case NetLerpMode.Buffered:
                    if (!bufferedStates.ContainsKey(propId))
                    {
                        bufferedStates[propId] = new List<BufferedState>();
                    }
                    bufferedStates[propId].Add(new BufferedState { Tick = tick, Value = value });

                    while (bufferedStates[propId].Count > MaxBufferSize)
                    {
                        bufferedStates[propId].RemoveAt(0);
                    }
                    break;
            }
        }

        private Data Deserialize(HLBuffer buffer)
        {
            var data = new Data
            {
                propertiesUpdated = new byte[GetByteCountOfProperties()],
                properties = new()
            };

            for (byte i = 0; i < data.propertiesUpdated.Length; i++)
            {
                data.propertiesUpdated[i] = HLBytes.UnpackByte(buffer);
            }

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
                    var prop = Protocol.UnpackProperty(network.RawNode.SceneFilePath, propertyIndex);

                    if (prop.VariantType == SerialVariantType.Object)
                    {
                        var node = network.RawNode.GetNode(prop.NodePath);
                        var propNode = node.Get(prop.Name).As<GodotObject>();
                        var method = Protocol.GetStaticMethod(prop, StaticMethodType.NetworkDeserialize);
                        if (method == null)
                        {
                            Debugger.Instance.Log($"No NetworkDeserialize method found for {prop.NodePath}.{prop.Name}", Debugger.DebugLevel.ERROR);
                            continue;
                        }
                        var result = method.Invoke(null, new object[] { network.CurrentWorld, new Variant(), buffer, propNode });
                        data.properties[propertyIndex] = Variant.From(result);
                    }
                    else
                    {
                        if (prop.VariantType == SerialVariantType.Nil)
                        {
                            Debugger.Instance.Log($"Property {prop.NodePath}.{prop.Name} has VariantType.Nil, cannot deserialize", Debugger.DebugLevel.ERROR);
                        }
                        var godotType = Protocol.ToGodotVariantType(prop.VariantType);
                        var varVal = HLBytes.UnpackVariant(buffer, knownType: godotType);
                        data.properties[propertyIndex] = varVal.Value;
                    }
                }
            }
            return data;
        }

        public void Begin()
        {
            processingPropertiesUpdated.Clear();
            foreach (var propIndex in propertyUpdated.Keys)
            {
                processingPropertiesUpdated[propIndex] = propertyUpdated[propIndex];
            }
            propertyUpdated.Clear();
        }

        public void Import(WorldRunner currentWorld, HLBuffer buffer, out NetworkController nodeOut)
        {
            nodeOut = network;

            var data = Deserialize(buffer);
            foreach (var propIndex in data.properties.Keys)
            {
                var prop = Protocol.UnpackProperty(network.RawNode.SceneFilePath, propIndex);
                if (network.RawNode.IsNodeReady())
                {
                    ImportProperty(prop, currentWorld.CurrentTick, data.properties[propIndex]);
                }
                else
                {
                    cachedPropertyChanges[propIndex] = data.properties[propIndex];
                }
            }
        }

        private int GetByteCountOfProperties()
        {
            return (Protocol.GetPropertyCount(network.RawNode.SceneFilePath) / BitConstants.BitsInByte) + 1;
        }

        private HashSet<int> nonDefaultProperties = new();
        private Dictionary<NetPeer, Dictionary<Tick, byte[]>> peerBufferCache = new();

        private bool TryGetInterestLayers(UUID peerId, out long layers)
        {
            layers = 0;
            if (!network.InterestLayers.TryGetValue(peerId, out layers))
                return false;
            return layers != 0;
        }

        private bool PeerHasInterestInProperty(int propIndex, long peerInterestLayers)
        {
            var prop = Protocol.UnpackProperty(network.RawNode.SceneFilePath, propIndex);
            return (prop.InterestMask & peerInterestLayers) != 0;
        }

        private static IEnumerable<int> EnumerateSetBits(byte[] mask)
        {
            for (var byteIndex = 0; byteIndex < mask.Length; byteIndex++)
            {
                var b = mask[byteIndex];
                for (var bitIndex = 0; bitIndex < 8; bitIndex++)
                {
                    if ((b & (1 << bitIndex)) != 0)
                    {
                        yield return byteIndex * 8 + bitIndex;
                    }
                }
            }
        }

        private static void ClearBit(byte[] mask, int bitIndex)
        {
            var byteIndex = bitIndex / 8;
            var bitOffset = bitIndex % 8;
            mask[byteIndex] &= (byte)~(1 << bitOffset);
        }

        private HLBuffer _buffer = new();
        private byte[] _propertiesUpdated;
        private byte[] _filteredProps;
        private List<Tick> _sortedTicks = new();

        public HLBuffer Export(WorldRunner currentWorld, NetPeer peerId)
        {
            _buffer.Clear();

            int byteCount = GetByteCountOfProperties();

            Array.Clear(_propertiesUpdated, 0, byteCount);
            Array.Clear(_filteredProps, 0, byteCount);

            if (!peerInitialPropSync.ContainsKey(peerId))
            {
                peerInitialPropSync[peerId] = new byte[byteCount];
            }

            if (!currentWorld.HasSpawnedForClient(network.NetId, peerId))
            {
                return _buffer;
            }

            if (!peerBufferCache.ContainsKey(peerId))
            {
                peerBufferCache[peerId] = new Dictionary<Tick, byte[]>();
            }

            if (!peerBufferCache[peerId].TryGetValue(currentWorld.CurrentTick, out var cachedMask))
            {
                cachedMask = new byte[byteCount];
                peerBufferCache[peerId][currentWorld.CurrentTick] = cachedMask;
            }

            foreach (var propIndex in processingPropertiesUpdated.Keys)
            {
                cachedMask[propIndex / BitConstants.BitsInByte] |= (byte)(1 << (propIndex % BitConstants.BitsInByte));
            }

            foreach (var propIndex in nonDefaultProperties)
            {
                var byteIndex = propIndex / BitConstants.BitsInByte;
                var propSlot = (byte)(1 << (propIndex % BitConstants.BitsInByte));
                if ((peerInitialPropSync[peerId][byteIndex] & propSlot) == 0)
                {
                    cachedMask[byteIndex] |= propSlot;
                }
            }

            _sortedTicks.Clear();
            foreach (var tick in peerBufferCache[peerId].Keys)
            {
                _sortedTicks.Add(tick);
            }
            _sortedTicks.Sort();

            foreach (var tick in _sortedTicks)
            {
                FilterPropsAgainstInterestNoAlloc(peerId, peerBufferCache[peerId][tick], _filteredProps);
                OrByteListInPlace(_propertiesUpdated, _filteredProps);
            }

            // Check if there's anything to send
            bool hasPendingUpdates = false;
            for (var i = 0; i < _propertiesUpdated.Length; i++)
            {
                if (_propertiesUpdated[i] != 0)
                {
                    hasPendingUpdates = true;
                    break;
                }
            }

            if (!hasPendingUpdates)
            {
                return _buffer;
            }

            // Mark props as synced
            foreach (var propIndex in EnumerateSetBits(_propertiesUpdated))
            {
                var byteIndex = propIndex / BitConstants.BitsInByte;
                var propSlot = (byte)(1 << (propIndex % BitConstants.BitsInByte));
                peerInitialPropSync[peerId][byteIndex] |= propSlot;
            }

            // Serialize the mask
            for (var i = 0; i < _propertiesUpdated.Length; i++)
            {
                HLBytes.Pack(_buffer, _propertiesUpdated[i]);
            }

            // Serialize property values
            for (var i = 0; i < _propertiesUpdated.Length; i++)
            {
                var propSegment = _propertiesUpdated[i];
                for (var j = 0; j < BitConstants.BitsInByte; j++)
                {
                    if ((propSegment & (byte)(1 << j)) == 0)
                    {
                        continue;
                    }

                    var propIndex = i * BitConstants.BitsInByte + j;
                    var prop = Protocol.UnpackProperty(network.RawNode.SceneFilePath, propIndex);
                    var propNode = network.RawNode.GetNode(prop.NodePath);
                    var varVal = propNode.Get(prop.Name);

                    if (prop.VariantType == SerialVariantType.Object)
                    {
                        var method = Protocol.GetStaticMethod(prop, StaticMethodType.NetworkSerialize);
                        if (method == null)
                        {
                            Debugger.Instance.Log($"No NetworkSerialize method found for {prop.NodePath}.{prop.Name}", Debugger.DebugLevel.ERROR);
                            continue;
                        }
                        var result = method.Invoke(null, new object[] { currentWorld, peerId, varVal });
                        HLBytes.Pack(_buffer, ((HLBuffer)result).bytes);
                    }
                    else
                    {
                        try
                        {
                            HLBytes.PackVariant(_buffer, varVal, packLength: true);
                        }
                        catch (Exception ex)
                        {
                            Debugger.Instance.Log($"Error packing variant {prop.NodePath}.{prop.Name}: {ex.Message}", Debugger.DebugLevel.ERROR);
                        }
                    }
                }
            }

            return _buffer;
        }

        private void FilterPropsAgainstInterestNoAlloc(NetPeer peer, byte[] dirtyPropsMask, byte[] result)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            if (!TryGetInterestLayers(peerId, out var peerInterestLayers))
            {
                Array.Clear(result, 0, result.Length);
                return;
            }

            Array.Copy(dirtyPropsMask, result, dirtyPropsMask.Length);

            foreach (var propIndex in EnumerateSetBits(dirtyPropsMask))
            {
                if (!PeerHasInterestInProperty(propIndex, peerInterestLayers))
                {
                    ClearBit(result, propIndex);
                }
            }
        }

        private void OrByteListInPlace(byte[] dest, byte[] src)
        {
            for (var i = 0; i < dest.Length; i++)
            {
                dest[i] |= src[i];
            }
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

        public void _Process(double delta)
        {
            if (NetRunner.Instance.IsServer)
            {
                return;
            }

            ProcessSmoothProperties(delta);
            ProcessBufferedProperties(delta);
        }

        private void ProcessSmoothProperties(double delta)
        {
            foreach (var propId in smoothStates.Keys.ToList())
            {
                var state = smoothStates[propId];

                var parts = propId.Split(':');
                var nodePath = parts[0];
                var propName = parts[1];

                var propNode = network.RawNode.GetNode(nodePath);
                if (propNode == null) continue;

                if (!Protocol.LookupProperty(network.RawNode.SceneFilePath, nodePath, propName, out var prop))
                    continue;

                float smoothSpeed = prop.LerpParam > 0 ? prop.LerpParam : 15f;
                float t = 1f - Mathf.Exp(-smoothSpeed * (float)delta);

                if (propNode.HasMethod("NetworkSmooth" + propName))
                {
                    propNode.Call("NetworkSmooth" + propName, state.Target, t);
                }
                else
                {
                    var current = propNode.Get(propName);
                    var target = state.Target;

                    if (prop.VariantType == SerialVariantType.Vector3)
                    {
                        var result = ((Vector3)current).Lerp((Vector3)target, t);
                        propNode.Set(propName, result);
                    }
                    else if (prop.VariantType == SerialVariantType.Quaternion)
                    {
                        var currentQuat = ((Quaternion)current).Normalized();
                        var targetQuat = ((Quaternion)target).Normalized();
                        if (currentQuat.Dot(targetQuat) < 0)
                            targetQuat = -targetQuat;
                        var result = currentQuat.Slerp(targetQuat, t);
                        propNode.Set(propName, result);
                    }
                    else if (prop.VariantType == SerialVariantType.Float)
                    {
                        var result = Mathf.Lerp((float)current, (float)target, t);
                        propNode.Set(propName, result);
                    }
                    else if (prop.VariantType == SerialVariantType.Object)
                    {
                        Debugger.Instance.Log($"Smooth mode requires NetworkSmooth{propName} method for object type {propId}", Debugger.DebugLevel.WARN);
                    }
                }
            }
        }

        private double _renderTick = -1;

        private void ProcessBufferedProperties(double delta)
        {
            int currentTick = network.CurrentWorld?.CurrentTick ?? 0;

            int delayTicks = 2;
            foreach (var propId in bufferedStates.Keys)
            {
                var parts = propId.Split(':');
                if (Protocol.LookupProperty(network.RawNode.SceneFilePath, parts[0], parts[1], out var prop))
                {
                    delayTicks = prop.LerpParam > 0 ? (int)prop.LerpParam : 2;
                    break;
                }
            }

            double targetRenderTick = currentTick - delayTicks;

            if (_renderTick < 0)
            {
                _renderTick = targetRenderTick;
            }

            double nominalAdvance = delta * NetRunner.TPS;
            double error = targetRenderTick - _renderTick;
            double correction = error * 0.2;
            double advance = nominalAdvance + correction;
            advance = Math.Clamp(advance, 0.0, nominalAdvance * 2.0);

            _renderTick += advance;

            foreach (var propId in bufferedStates.Keys.ToList())
            {
                var buffer = bufferedStates[propId];
                if (buffer.Count < 2) continue;

                var parts = propId.Split(':');
                var nodePath = parts[0];
                var propName = parts[1];
                var propNode = network.RawNode.GetNode(nodePath);
                if (propNode == null) continue;

                if (!Protocol.LookupProperty(network.RawNode.SceneFilePath, nodePath, propName, out var prop))
                    continue;

                BufferedState? before = null;
                BufferedState? after = null;

                for (int i = 0; i < buffer.Count - 1; i++)
                {
                    if (buffer[i].Tick <= _renderTick && buffer[i + 1].Tick >= _renderTick)
                    {
                        before = buffer[i];
                        after = buffer[i + 1];
                        break;
                    }
                }

                while (buffer.Count > 3 && buffer[0].Tick < _renderTick - 2)
                {
                    buffer.RemoveAt(0);
                }

                if (before == null || after == null)
                {
                    if (buffer.Count > 0)
                    {
                        propNode.Set(propName, buffer[^1].Value);
                    }
                    continue;
                }

                double span = after.Value.Tick - before.Value.Tick;
                float t = span > 0 ? (float)((_renderTick - before.Value.Tick) / span) : 0f;
                t = Mathf.Clamp(t, 0f, 1f);

                if (propNode.HasMethod("NetworkBufferedLerp" + propName))
                {
                    propNode.Call("NetworkBufferedLerp" + propName, before.Value.Value, after.Value.Value, t);
                }
                else
                {
                    if (prop.VariantType == SerialVariantType.Vector3)
                    {
                        var result = ((Vector3)before.Value.Value).Lerp((Vector3)after.Value.Value, t);
                        propNode.Set(propName, result);
                    }
                    else if (prop.VariantType == SerialVariantType.Quaternion)
                    {
                        var fromQuat = ((Quaternion)before.Value.Value).Normalized();
                        var toQuat = ((Quaternion)after.Value.Value).Normalized();
                        if (fromQuat.Dot(toQuat) < 0)
                            toQuat = -toQuat;
                        var result = fromQuat.Slerp(toQuat, t);
                        propNode.Set(propName, result);
                    }
                    else if (prop.VariantType == SerialVariantType.Float)
                    {
                        var result = Mathf.Lerp((float)before.Value.Value, (float)after.Value.Value, t);
                        propNode.Set(propName, result);
                    }
                    else if (prop.VariantType == SerialVariantType.Object)
                    {
                        Debugger.Instance.Log($"Buffered mode requires NetworkBufferedLerp{propName} method for object type {propId}", Debugger.DebugLevel.WARN);
                    }
                }
            }
        }
    }
}