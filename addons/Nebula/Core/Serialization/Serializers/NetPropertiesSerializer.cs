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

        #region Interpolation State

        /// <summary>
        /// For Smooth mode: tracks current target per property
        /// </summary>
        private struct SmoothState
        {
            public Variant Target;
        }
        private Dictionary<string, SmoothState> smoothStates = new();

        /// <summary>
        /// For Buffered mode: stores timestamped state history
        /// </summary>
        private struct BufferedState
        {
            public Tick Tick;
            public Variant Value;
        }
        private Dictionary<string, List<BufferedState>> bufferedStates = new();
        private const int MaxBufferSize = 30;

        #endregion

        private Dictionary<int, bool> propertyUpdated = new Dictionary<int, bool>();
        private Dictionary<int, bool> processingPropertiesUpdated = new Dictionary<int, bool>();

        private double TickInterval => 1.0 / NetRunner.TPS;

        private Dictionary<NetPeer, byte[]> peerInitialPropSync = new Dictionary<NetPeer, byte[]>();

        public void Setup()
        {
            wrapper = new NetNodeWrapper(GetParent());
            Name = "NetPropertiesSerializer";

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

                wrapper.Network.Connect("InterestChanged", Callable.From((UUID peerId, long oldInterest, long newInterest) =>
                {
                    // Debugger.Instance.Log($"Interest changed for peer {peerId} on node {wrapper.Node.Name}: {newInterest}");

                    var peer = NetRunner.Instance.GetPeer(peerId);
                    if (peer == null || !peerInitialPropSync.ContainsKey(peer))
                        return;

                    foreach (var propIndex in nonDefaultProperties)
                    {
                        var prop = ProtocolRegistry.Instance.UnpackProperty(wrapper.Node.SceneFilePath, propIndex);

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
                    var prop = ProtocolRegistry.Instance.UnpackProperty(wrapper.Node.SceneFilePath, propIndex);
                    ImportProperty(prop, wrapper.CurrentWorld.CurrentTick, cachedPropertyChanges[propIndex]);
                }
            }
        }

        public void ImportProperty(ProtocolNetProperty prop, Tick tick, Variant value)
        {
            var propNode = wrapper.Node.GetNode(prop.NodePath);
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
                    // Snap immediately
                    propNode.Set(prop.Name, value);
                    break;

                case NetLerpMode.Smooth:
                    // Just update target - _Process will chase it
                    smoothStates[propId] = new SmoothState { Target = value };
                    break;

                case NetLerpMode.Buffered:
                    // Add to buffer with timestamp
                    if (!bufferedStates.ContainsKey(propId))
                    {
                        bufferedStates[propId] = new List<BufferedState>();
                    }
                    bufferedStates[propId].Add(new BufferedState { Tick = tick, Value = value });

                    // Prune old states
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
                properties = new Dictionary<int, Variant>()
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
                    var prop = ProtocolRegistry.Instance.UnpackProperty(wrapper.Node.SceneFilePath, propertyIndex);
                    if (prop.VariantType == Variant.Type.Object)
                    {
                        var node = wrapper.Node.GetNode(prop.NodePath);
                        var propNode = node.Get(prop.Name).As<GodotObject>();
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
                        if (prop.VariantType == Variant.Type.Nil)
                        {
                            Debugger.Instance.Log($"Property {prop.NodePath}.{prop.Name} has VariantType.Nil, cannot deserialize", Debugger.DebugLevel.ERROR);
                        }
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
            foreach (var propIndex in propertyUpdated.Keys)
            {
                processingPropertiesUpdated[propIndex] = propertyUpdated[propIndex];
            }
            _dirtyMask = new byte[GetByteCountOfProperties()];
            Array.Clear(_dirtyMask, 0, _dirtyMask.Length);
            foreach (var propIndex in processingPropertiesUpdated.Keys)
            {
                _dirtyMask[propIndex / BitConstants.BitsInByte] |= (byte)(1 << (propIndex % BitConstants.BitsInByte));
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
                // Debugger.Instance.Log($"Importing property {prop.NodePath}.{prop.Name} (Index {propIndex})", Debugger.DebugLevel.INFO);
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

        private byte[] FilterPropsAgainstInterest(NetPeer peer, byte[] dirtyPropsMask)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            wrapper.InterestLayers.TryGetValue(peerId, out var debugVal);
            if (!TryGetInterestLayers(peerId, out var peerInterestLayers))
            {
                return new byte[GetByteCountOfProperties()];
            }

            var filteredMask = (byte[])dirtyPropsMask.Clone();

            foreach (var propIndex in EnumerateSetBits(dirtyPropsMask))
            {
                if (!PeerHasInterestInProperty(propIndex, peerInterestLayers))
                {
                    ClearBit(filteredMask, propIndex);
                }
            }

            return filteredMask;
        }

        private bool TryGetInterestLayers(UUID peerId, out long layers)
        {
            layers = 0;
            if (!wrapper.InterestLayers.TryGetValue(peerId, out layers))
                return false;
            return layers != 0;
        }

        private bool PeerHasInterestInProperty(int propIndex, long peerInterestLayers)
        {
            var prop = ProtocolRegistry.Instance.UnpackProperty(wrapper.Node.SceneFilePath, propIndex);
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

        private HLBuffer _buffer = new HLBuffer();
        private byte[] _dirtyMask;
        private byte[] _propertiesUpdated;
        private byte[] _filteredProps;
        private List<Tick> _sortedTicks = new();

        public HLBuffer Export(WorldRunner currentWorld, NetPeer peerId)
        {
            _buffer.Clear();

            int byteCount = GetByteCountOfProperties();

            // Lazy init or reuse
            if (_propertiesUpdated == null || _propertiesUpdated.Length != byteCount)
            {
                _propertiesUpdated = new byte[byteCount];
                _filteredProps = new byte[byteCount];
            }

            Array.Clear(_propertiesUpdated, 0, byteCount);

            if (!peerInitialPropSync.ContainsKey(peerId))
            {
                peerInitialPropSync[peerId] = new byte[byteCount];
            }

            if (!currentWorld.HasSpawnedForClient(wrapper.NetId, peerId))
            {
                return _buffer;
            }

            if (!peerBufferCache.ContainsKey(peerId))
            {
                peerBufferCache[peerId] = new Dictionary<Tick, byte[]>();
            }

            foreach (var propIndex in processingPropertiesUpdated.Keys)
            {
                _dirtyMask[propIndex / BitConstants.BitsInByte] |= (byte)(1 << (propIndex % BitConstants.BitsInByte));
            }

            foreach (var propIndex in nonDefaultProperties)
            {
                var byteIndex = propIndex / BitConstants.BitsInByte;
                var propSlot = (byte)(1 << (propIndex % BitConstants.BitsInByte));
                if ((peerInitialPropSync[peerId][byteIndex] & propSlot) == 0)
                {
                    _dirtyMask[byteIndex] |= propSlot;
                }
            }

            peerBufferCache[peerId][currentWorld.CurrentTick] = _dirtyMask; // NOTE: see below

            // Replace LINQ with manual sort
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

            // Mark props as synced only now that they passed filtering
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
                    var prop = ProtocolRegistry.Instance.UnpackProperty(wrapper.Node.SceneFilePath, propIndex);
                    var propNode = wrapper.Node.GetNode(prop.NodePath);
                    var varVal = propNode.Get(prop.Name);

                    if (prop.VariantType == Variant.Type.Object)
                    {
                        var callable = ProtocolRegistry.Instance.GetStaticMethodCallable(prop, StaticMethodType.NetworkSerialize);
                        if (!callable.HasValue)
                        {
                            Debugger.Instance.Log($"No NetworkSerialize method found for {prop.NodePath}.{prop.Name}", Debugger.DebugLevel.ERROR);
                            continue;
                        }
                        HLBytes.Pack(_buffer, callable.Value.Call(currentWorld, peerId, varVal).As<HLBuffer>().bytes);
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

        public override void _Process(double delta)
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

                // Parse propId to get node and property
                var parts = propId.Split(':');
                var nodePath = parts[0];
                var propName = parts[1];

                var propNode = wrapper.Node.GetNode(nodePath);
                if (propNode == null) continue;

                // Look up the property to get LerpParam
                if (!ProtocolRegistry.Instance.LookupProperty(wrapper.Node.SceneFilePath, nodePath, propName, out var prop))
                    continue;

                float smoothSpeed = prop.LerpParam > 0 ? prop.LerpParam : 15f;
                float t = 1f - Mathf.Exp(-smoothSpeed * (float)delta);

                // Check for custom lerp method
                if (propNode.HasMethod("NetworkSmooth" + propName))
                {
                    propNode.Call("NetworkSmooth" + propName, state.Target, t);
                }
                else
                {
                    // Default smoothing by type
                    var current = propNode.Get(propName);
                    var target = state.Target;

                    if (prop.VariantType == Variant.Type.Vector3)
                    {
                        var result = ((Vector3)current).Lerp((Vector3)target, t);
                        propNode.Set(propName, result);
                    }
                    else if (prop.VariantType == Variant.Type.Quaternion)
                    {
                        var currentQuat = ((Quaternion)current).Normalized();
                        var targetQuat = ((Quaternion)target).Normalized();
                        if (currentQuat.Dot(targetQuat) < 0)
                            targetQuat = -targetQuat;
                        var result = currentQuat.Slerp(targetQuat, t);
                        propNode.Set(propName, result);
                    }
                    else if (prop.VariantType == Variant.Type.Float)
                    {
                        var result = Mathf.Lerp((float)current, (float)target, t);
                        propNode.Set(propName, result);
                    }
                    else if (prop.VariantType == Variant.Type.Object)
                    {
                        // For complex objects, require custom handler
                        Debugger.Instance.Log($"Smooth mode requires NetworkSmooth{propName} method for object type {propId}", Debugger.DebugLevel.WARN);
                    }
                }
            }
        }
        private double _renderTick = -1;

        private void ProcessBufferedProperties(double delta)
        {
            int currentTick = wrapper.CurrentWorld?.CurrentTick ?? 0;

            // Get delay from first buffered property (or use default)
            int delayTicks = 2;
            foreach (var propId in bufferedStates.Keys)
            {
                var parts = propId.Split(':');
                if (ProtocolRegistry.Instance.LookupProperty(wrapper.Node.SceneFilePath, parts[0], parts[1], out var prop))
                {
                    delayTicks = prop.LerpParam > 0 ? (int)prop.LerpParam : 2;
                    break;
                }
            }

            // Where we SHOULD be based on server time
            double targetRenderTick = currentTick - delayTicks;

            // Initialize on first frame
            if (_renderTick < 0)
            {
                _renderTick = targetRenderTick;
            }

            // Nominal advance per frame at server tick rate
            double nominalAdvance = delta * NetRunner.TPS;

            // How far off are we? Positive = behind, Negative = ahead
            double error = targetRenderTick - _renderTick;

            // Adaptive correction: converge toward target over ~5 frames
            // If behind, this adds to advance (speed up)
            // If ahead, this subtracts from advance (slow down)
            double correction = error * 0.2;

            double advance = nominalAdvance + correction;

            // CRITICAL: Never go backward. Never jump more than 2x speed.
            advance = Math.Clamp(advance, 0.0, nominalAdvance * 2.0);

            _renderTick += advance;

            // Now process each buffered property using the smooth _renderTick
            foreach (var propId in bufferedStates.Keys.ToList())
            {
                var buffer = bufferedStates[propId];
                if (buffer.Count < 2) continue;

                var parts = propId.Split(':');
                var nodePath = parts[0];
                var propName = parts[1];
                var propNode = wrapper.Node.GetNode(nodePath);
                if (propNode == null) continue;

                if (!ProtocolRegistry.Instance.LookupProperty(wrapper.Node.SceneFilePath, nodePath, propName, out var prop))
                    continue;

                // Find two states to interpolate between
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

                // Prune old states
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

                // Check for custom lerp method
                if (propNode.HasMethod("NetworkBufferedLerp" + propName))
                {
                    propNode.Call("NetworkBufferedLerp" + propName, before.Value.Value, after.Value.Value, t);
                }
                else
                {
                    // Default interpolation by type
                    if (prop.VariantType == Variant.Type.Vector3)
                    {
                        var result = ((Vector3)before.Value.Value).Lerp((Vector3)after.Value.Value, t);
                        propNode.Set(propName, result);
                    }
                    else if (prop.VariantType == Variant.Type.Quaternion)
                    {
                        var fromQuat = ((Quaternion)before.Value.Value).Normalized();
                        var toQuat = ((Quaternion)after.Value.Value).Normalized();
                        if (fromQuat.Dot(toQuat) < 0)
                            toQuat = -toQuat;
                        var result = fromQuat.Slerp(toQuat, t);
                        propNode.Set(propName, result);
                    }
                    else if (prop.VariantType == Variant.Type.Float)
                    {
                        var result = Mathf.Lerp((float)before.Value.Value, (float)after.Value.Value, t);
                        propNode.Set(propName, result);
                    }
                    else if (prop.VariantType == Variant.Type.Object)
                    {
                        Debugger.Instance.Log($"Buffered mode requires NetworkBufferedLerp{propName} method for object type {propId}", Debugger.DebugLevel.WARN);
                    }
                }
            }
        }
    }
}