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
            public Dictionary<int, PropertyCache> properties;
        }

        private NetworkController network;
        private Dictionary<int, PropertyCache> cachedPropertyChanges = new();

        // Dirty mask snapshot at Begin()
        private long processingDirtyMask = 0;

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
                // Dirty tracking is now handled by NetworkController.MarkDirty() which sets DirtyMask
                // and populates CachedProperties. No more Godot signal subscription needed.

                network.InterestChanged += (UUID peerId, long oldInterest, long newInterest) =>
                {
                    var peer = NetRunner.Instance.GetPeer(peerId);
                    if (!peer.IsSet || !peerInitialPropSync.ContainsKey(peer))
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
                };
            }
            else
            {
                foreach (var propIndex in cachedPropertyChanges.Keys)
                {
                    var prop = Protocol.UnpackProperty(network.RawNode.SceneFilePath, propIndex);
                    ref var cachedValue = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(cachedPropertyChanges, propIndex);
                    ImportProperty(prop, network.CurrentWorld.CurrentTick, ref cachedValue);
                }
            }
        }

        /// <summary>
        /// Compares two PropertyCache values for equality based on their type.
        /// </summary>
        private static bool PropertyCacheEquals(ref PropertyCache a, ref PropertyCache b)
        {
            if (a.Type != b.Type) return false;

            return a.Type switch
            {
                SerialVariantType.Bool => a.BoolValue == b.BoolValue,
                SerialVariantType.Int => a.LongValue == b.LongValue,
                SerialVariantType.Float => a.FloatValue == b.FloatValue,
                SerialVariantType.String => a.StringValue == b.StringValue,
                SerialVariantType.Vector2 => a.Vec2Value == b.Vec2Value,
                SerialVariantType.Vector3 => a.Vec3Value == b.Vec3Value,
                SerialVariantType.Quaternion => a.QuatValue == b.QuatValue,
                SerialVariantType.PackedByteArray => ReferenceEquals(a.RefValue, b.RefValue) || (a.RefValue is byte[] ba && b.RefValue is byte[] bb && ba.AsSpan().SequenceEqual(bb)),
                SerialVariantType.PackedInt32Array => ReferenceEquals(a.RefValue, b.RefValue) || (a.RefValue is int[] ia && b.RefValue is int[] ib && ia.AsSpan().SequenceEqual(ib)),
                SerialVariantType.PackedInt64Array => ReferenceEquals(a.RefValue, b.RefValue) || (a.RefValue is long[] la && b.RefValue is long[] lb && la.AsSpan().SequenceEqual(lb)),
                SerialVariantType.Object => ReferenceEquals(a.RefValue, b.RefValue) || object.Equals(a.RefValue, b.RefValue),
                _ => false
            };
        }

        /// <summary>
        /// Imports a property value from the network. Uses cached old values and generated setters
        /// to avoid crossing the Godot boundary.
        /// </summary>
        public void ImportProperty(ProtocolNetProperty prop, Tick tick, ref PropertyCache newValue)
        {
            // Get the node that owns this property
            var propNode = network.RawNode.GetNode(prop.NodePath);
            if (propNode is not INetNodeBase netNode)
            {
                Debugger.Instance.Log($"Property node {prop.NodePath} is not INetNodeBase, cannot import", Debugger.DebugLevel.ERROR);
                return;
            }

            // Get old value from cache (no Godot boundary crossing)
            ref var oldValue = ref network.CachedProperties[prop.Index];

            bool valueChanged = !PropertyCacheEquals(ref oldValue, ref newValue);

            // Fire change callbacks if value changed
            if (valueChanged)
            {
                if (prop.NotifyOnChange)
                {
                    netNode.InvokePropertyChangeHandler(prop.Index, tick, ref oldValue, ref newValue);
                }
            }

            // Update cache (this is the target for interpolated properties)
            network.CachedProperties[prop.Index] = newValue;

            // For interpolated properties, don't set immediately - ProcessInterpolation will handle it
            // For non-interpolated properties, set via generated setter (no Godot boundary)
            if (!prop.Interpolate)
            {
                netNode.SetNetPropertyByIndex(prop.Index, ref newValue);
            }
        }

        private Data Deserialize(NetBuffer buffer)
        {
            var data = new Data
            {
                propertiesUpdated = new byte[GetByteCountOfProperties()],
                properties = new()
            };

            for (byte i = 0; i < data.propertiesUpdated.Length; i++)
            {
                data.propertiesUpdated[i] = NetReader.ReadByte(buffer);
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

                    var cache = new PropertyCache();

                    if (prop.VariantType == SerialVariantType.Object)
                    {
                        // Custom types with NetworkDeserialize - use generated deserializer delegate (no reflection)
                        var deserializer = Protocol.GetDeserializer(prop.ClassIndex);
                        if (deserializer == null)
                        {
                            Debugger.Instance.Log($"No deserializer found for {prop.NodePath}.{prop.Name}", Debugger.DebugLevel.ERROR);
                            continue;
                        }
                        // Pass existing cached value for delta encoding support
                        var existingValue = propertyIndex < network.CachedProperties.Length
                            ? network.CachedProperties[propertyIndex].RefValue
                            : null;
                        var result = deserializer(network.CurrentWorld, null, buffer, existingValue);
                        cache.Type = SerialVariantType.Object;
                        cache.RefValue = result;
                    }
                    else if (prop.VariantType == SerialVariantType.Nil)
                    {
                        Debugger.Instance.Log($"Property {prop.NodePath}.{prop.Name} has VariantType.Nil, cannot deserialize", Debugger.DebugLevel.ERROR);
                        continue;
                    }
                    else
                    {
                        ReadPropertyToCache(buffer, prop.VariantType, ref cache);
                    }

                    data.properties[propertyIndex] = cache;
                }
            }
            return data;
        }

        /// <summary>
        /// Reads a property value directly into a PropertyCache. Zero boxing for primitive types.
        /// </summary>
        private static void ReadPropertyToCache(NetBuffer buffer, SerialVariantType type, ref PropertyCache cache)
        {
            cache.Type = type;
            switch (type)
            {
                case SerialVariantType.Bool:
                    cache.BoolValue = NetReader.ReadBool(buffer);
                    break;
                case SerialVariantType.Int:
                    cache.LongValue = NetReader.ReadInt64(buffer);
                    break;
                case SerialVariantType.Float:
                    cache.FloatValue = NetReader.ReadFloat(buffer);
                    break;
                case SerialVariantType.String:
                    cache.StringValue = NetReader.ReadString(buffer);
                    break;
                case SerialVariantType.Vector2:
                    cache.Vec2Value = NetReader.ReadVector2(buffer);
                    break;
                case SerialVariantType.Vector3:
                    cache.Vec3Value = NetReader.ReadVector3(buffer);
                    break;
                case SerialVariantType.Quaternion:
                    cache.QuatValue = NetReader.ReadQuaternion(buffer);
                    break;
                case SerialVariantType.PackedByteArray:
                    cache.RefValue = NetReader.ReadBytesWithLength(buffer);
                    break;
                case SerialVariantType.PackedInt32Array:
                    cache.RefValue = NetReader.ReadInt32Array(buffer);
                    break;
                case SerialVariantType.PackedInt64Array:
                    cache.RefValue = NetReader.ReadInt64Array(buffer);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported property type: {type}");
            }
        }

        /// <summary>
        /// Writes a property value from the cache. No Godot calls, no boxing.
        /// </summary>
        private void WriteFromCache(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, ProtocolNetProperty prop, int propIndex)
        {
            ref var cache = ref network.CachedProperties[propIndex];
            
            switch (cache.Type)
            {
                case SerialVariantType.Bool:
                    NetWriter.WriteBool(buffer, cache.BoolValue);
                    break;
                case SerialVariantType.Int:
                    NetWriter.WriteInt64(buffer, cache.LongValue);
                    break;
                case SerialVariantType.Float:
                    NetWriter.WriteFloat(buffer, cache.FloatValue);
                    break;
                case SerialVariantType.String:
                    NetWriter.WriteString(buffer, cache.StringValue ?? "");
                    break;
                case SerialVariantType.Vector2:
                    NetWriter.WriteVector2(buffer, cache.Vec2Value);
                    break;
                case SerialVariantType.Vector3:
                    NetWriter.WriteVector3(buffer, cache.Vec3Value);
                    break;
                case SerialVariantType.Quaternion:
                    NetWriter.WriteQuaternion(buffer, cache.QuatValue);
                    break;
                case SerialVariantType.PackedByteArray:
                    NetWriter.WriteBytesWithLength(buffer, cache.RefValue as byte[] ?? Array.Empty<byte>());
                    break;
                case SerialVariantType.PackedInt32Array:
                    NetWriter.WriteInt32Array(buffer, cache.RefValue as int[] ?? Array.Empty<int>());
                    break;
                case SerialVariantType.PackedInt64Array:
                    NetWriter.WriteInt64Array(buffer, cache.RefValue as long[] ?? Array.Empty<long>());
                    break;
                case SerialVariantType.Object:
                    // Custom types with peer-dependent serialization
                    WriteCustomTypeFromCache(currentWorld, peer, buffer, prop, ref cache);
                    break;
                default:
                    Debugger.Instance.Log($"Unsupported cache type: {cache.Type}", Debugger.DebugLevel.ERROR);
                    break;
            }
        }
        
        /// <summary>
        /// Writes a custom type from the cache using a generated serializer delegate.
        /// The delegate knows which PropertyCache field to access (no type-specific code needed here).
        /// </summary>
        private void WriteCustomTypeFromCache(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, ProtocolNetProperty prop, ref PropertyCache cache)
        {
            var serializer = Protocol.GetSerializer(prop.ClassIndex);
            if (serializer == null)
            {
                Debugger.Instance.Log($"No serializer found for {prop.NodePath}.{prop.Name}", Debugger.DebugLevel.ERROR);
                return;
            }
            
            using var tempBuffer = new NetBuffer();
            serializer(currentWorld, peer, ref cache, tempBuffer);
            NetWriter.WriteBytes(buffer, tempBuffer.WrittenSpan);
        }

        public void Begin()
        {
            // Snapshot the dirty mask and clear the original
            processingDirtyMask = network.DirtyMask;
            network.ClearDirtyMask();
            
            // Track which properties have ever been set (for initial sync to new peers)
            for (int i = 0; i < 64; i++)
            {
                if ((processingDirtyMask & (1L << i)) != 0)
                {
                    nonDefaultProperties.Add(i);
                }
            }
        }

        public void Import(WorldRunner currentWorld, NetBuffer buffer, out NetworkController nodeOut)
        {
            nodeOut = network;

            var data = Deserialize(buffer);

            foreach (var propIndex in data.properties.Keys)
            {
                var prop = Protocol.UnpackProperty(network.RawNode.SceneFilePath, propIndex);
                // Get a ref to the value in the dictionary for zero-copy
                ref var propValue = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(data.properties, propIndex);
                if (network.RawNode.IsNodeReady())
                {
                    ImportProperty(prop, currentWorld.CurrentTick, ref propValue);
                }
                else
                {
                    cachedPropertyChanges[propIndex] = propValue;
                }
            }
        }

        private int GetByteCountOfProperties()
        {
            return (Protocol.GetPropertyCount(network.RawNode.SceneFilePath) / BitConstants.BitsInByte) + 1;
        }

        private HashSet<int> nonDefaultProperties = new();
        private Dictionary<NetPeer, Dictionary<Tick, byte[]>> peerBufferCache = new();
        
        /// <summary>
        /// Maximum number of ticks to cache per peer before forced pruning.
        /// This prevents unbounded memory growth if acknowledgments are delayed.
        /// TPS/2 = ~500ms which is plenty of time for acks on a healthy connection.
        /// </summary>
        private static int MaxCachedTicksPerPeer = NetRunner.TPS / 2;

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

        private byte[] _propertiesUpdated;
        private byte[] _filteredProps;
        private List<Tick> _sortedTicks = new();

        // Diagnostic counters for memory leak detection
        private static int _diagnosticCounter = 0;
        private static int _diagnosticLogInterval = 100; // Log every N ticks
        
        public void Export(WorldRunner currentWorld, NetPeer peerId, NetBuffer buffer)
        {
            int byteCount = GetByteCountOfProperties();

            Array.Clear(_propertiesUpdated, 0, byteCount);
            Array.Clear(_filteredProps, 0, byteCount);

            if (!peerInitialPropSync.ContainsKey(peerId))
            {
                peerInitialPropSync[peerId] = new byte[byteCount];
            }

            if (!currentWorld.HasSpawnedForClient(network.NetId, peerId))
            {
                return;
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
            
            // DIAGNOSTIC: Log cache sizes periodically to detect memory leaks
            // _diagnosticCounter++;
            // if (_diagnosticCounter % _diagnosticLogInterval == 0)
            // {
            //     int totalTicksAcrossPeers = 0;
            //     int diagPeerCount = peerBufferCache.Count;
            //     var perPeerInfo = new System.Text.StringBuilder();
            //     foreach (var kvp in peerBufferCache)
            //     {
            //         totalTicksAcrossPeers += kvp.Value.Count;
            //         perPeerInfo.Append($"[Peer {kvp.Key.ID}: {kvp.Value.Count} ticks] ");
            //     }
            //     Debugger.Instance.Log($"[NetPropertiesSerializer DIAG] Node={network.RawNode.Name} tick={currentWorld.CurrentTick} peerBufferCache: {diagPeerCount} peers, {totalTicksAcrossPeers} total cached ticks | {perPeerInfo}", Debugger.DebugLevel.VERBOSE);
                
            //     // Warn if cache is growing too large
            //     if (totalTicksAcrossPeers > 500)
            //     {
            //         Debugger.Instance.Log($"[NetPropertiesSerializer WARN] Large tick cache detected! {totalTicksAcrossPeers} cached ticks - possible ack issue or memory leak", Debugger.DebugLevel.WARN);
            //     }
            // }
            
            // SAFEGUARD: Prune oldest ticks if cache exceeds limit to prevent unbounded growth
            var currentPeerCache = peerBufferCache[peerId];
            if (currentPeerCache.Count > MaxCachedTicksPerPeer)
            {
                // Find and remove oldest ticks
                _sortedTicks.Clear();
                foreach (var tick in currentPeerCache.Keys)
                {
                    _sortedTicks.Add(tick);
                }
                _sortedTicks.Sort();
                
                int ticksToRemove = currentPeerCache.Count - MaxCachedTicksPerPeer;
                for (int i = 0; i < ticksToRemove; i++)
                {
                    currentPeerCache.Remove(_sortedTicks[i]);
                }
            }

            // Convert dirty mask to byte array format for existing logic
            for (int propIndex = 0; propIndex < 64; propIndex++)
            {
                if ((processingDirtyMask & (1L << propIndex)) != 0)
                {
                    cachedMask[propIndex / BitConstants.BitsInByte] |= (byte)(1 << (propIndex % BitConstants.BitsInByte));
                }
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
                return;
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
                NetWriter.WriteByte(buffer, _propertiesUpdated[i]);
            }

            // Serialize property values from cache (no Godot calls)
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
                    
                    try
                    {
                        WriteFromCache(currentWorld, peerId, buffer, prop, propIndex);
                    }
                    catch (Exception ex)
                    {
                        var innerMsg = ex.InnerException?.Message ?? ex.Message;
                        var innerStack = ex.InnerException?.StackTrace ?? ex.StackTrace;
                        Debugger.Instance.Log($"Error serializing property {prop.NodePath}.{prop.Name} from cache: {innerMsg}\n{innerStack}", Debugger.DebugLevel.ERROR);
                    }
                }
            }
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

        public void Cleanup() 
        {
            // NOTE: This is called every tick after ExportState(), NOT when the object is destroyed.
            // Do not clear per-peer caches here - that would break state synchronization!
            // Use CleanupPeer() for per-peer cleanup on disconnect instead.
        }
        
        /// <summary>
        /// Removes all cached data for a specific peer. Call this when a peer disconnects.
        /// </summary>
        public void CleanupPeer(NetPeer peer)
        {
            // if (peerBufferCache.Remove(peer, out var tickCache))
            // {
            //     Debugger.Instance.Log($"[NetPropertiesSerializer] Cleaned up {tickCache.Count} cached ticks for disconnected peer", Debugger.DebugLevel.VERBOSE);
            // }
            peerInitialPropSync.Remove(peer);
        }

        // Reusable list to avoid LINQ allocation in Acknowledge
        private List<Tick> _ticksToRemove = new();
        
        public void Acknowledge(WorldRunner currentWorld, NetPeer peerId, Tick latestAck)
        {
            if (!peerBufferCache.TryGetValue(peerId, out var tickCache))
            {
                return;
            }
            
            int beforeCount = tickCache.Count;
            
            // Avoid LINQ .ToList() allocation - reuse list
            _ticksToRemove.Clear();
            foreach (var tick in tickCache.Keys)
            {
                if (tick <= latestAck)
                {
                    _ticksToRemove.Add(tick);
                }
            }
            
            foreach (var tick in _ticksToRemove)
            {
                tickCache.Remove(tick);
            }
            
            // DIAGNOSTIC: Log when acknowledgments are cleaning up ticks
            // if (_ticksToRemove.Count > 0)
            // {
            //     Debugger.Instance.Log($"[NetPropertiesSerializer ACK] Peer {peerId.ID} acked tick {latestAck}, removed {_ticksToRemove.Count} ticks, {tickCache.Count} remaining for node {network.RawNode.Name}", Debugger.DebugLevel.VERBOSE);
            // }
        }

    }
}
