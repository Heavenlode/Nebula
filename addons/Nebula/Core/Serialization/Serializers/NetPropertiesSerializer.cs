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

        private Dictionary<UUID, byte[]> peerInitialPropSync = new();
        
        // Cached to avoid Godot StringName allocations every access
        private string _cachedSceneFilePath;
        
        // Cached node lookups to avoid GetNode() allocations
        private Dictionary<StringName, Node> _nodePathCache = new();

        public NetPropertiesSerializer(NetworkController _network)
        {
            network = _network;
            
            // Cache SceneFilePath once to avoid Godot StringName allocations on every access
            _cachedSceneFilePath = network.RawNode.SceneFilePath;

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
                    // Fix #7: Use TryGetValue instead of ContainsKey + indexer
                    if (!peerInitialPropSync.TryGetValue(peerId, out var syncMask))
                        return;

                    foreach (var propIndex in nonDefaultProperties)
                    {
                        var prop = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);

                        bool wasVisible = (prop.InterestMask & oldInterest) != 0 
                            && (prop.InterestRequired & oldInterest) == prop.InterestRequired;
                        bool isNowVisible = (prop.InterestMask & newInterest) != 0
                            && (prop.InterestRequired & newInterest) == prop.InterestRequired;

                        if (!wasVisible && isNowVisible)
                        {
                            // Mark property as not-yet-synced so Export() will include it
                            ClearBit(syncMask, propIndex);
                        }
                    }
                };
            }
            else
            {
                foreach (var propIndex in cachedPropertyChanges.Keys)
                {
                    var prop = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
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
        /// Gets a node by path with caching to avoid GetNode() allocations.
        /// </summary>
        private Node GetCachedNode(StringName nodePath)
        {
            if (!_nodePathCache.TryGetValue(nodePath, out var node))
            {
                // Convert StringName to NodePath for GetNode - this allocates once per unique path
                node = network.RawNode.GetNode(new NodePath(nodePath.ToString()));
                _nodePathCache[nodePath] = node;
            }
            return node;
        }
        
        /// <summary>
        /// Imports a property value from the network. Uses cached old values and generated setters
        /// to avoid crossing the Godot boundary.
        /// </summary>
        public void ImportProperty(ProtocolNetProperty prop, Tick tick, ref PropertyCache newValue)
        {
            // Get the node that owns this property (cached to avoid GetNode allocations)
            var propNode = GetCachedNode(prop.NodePath);
            if (propNode is not INetNodeBase netNode)
            {
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Property node {prop.NodePath} is not INetNodeBase, cannot import");
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
                    // Use LocalIndex (cumulative class index) not Index (scene-global) - matches generated switch cases
                    // Call via base class type to use virtual dispatch (not interface dispatch)
                    if (propNode is NetNode3D nn3d)
                    {
                        nn3d.InvokePropertyChangeHandler(prop.LocalIndex, tick, ref oldValue, ref newValue);
                    }
                    else if (propNode is NetNode2D nn2d)
                    {
                        nn2d.InvokePropertyChangeHandler(prop.LocalIndex, tick, ref oldValue, ref newValue);
                    }
                    else if (propNode is NetNode nn)
                    {
                        nn.InvokePropertyChangeHandler(prop.LocalIndex, tick, ref oldValue, ref newValue);
                    }
                }
            }

            // Update cache (this is the target for interpolated properties)
            network.CachedProperties[prop.Index] = newValue;

            // For interpolated properties, don't set immediately - ProcessInterpolation will handle it
            // For non-interpolated properties, set via generated setter (no Godot boundary)
            if (!prop.Interpolate)
            {
                // Use LocalIndex (class-local) not Index (scene-global) for SetNetPropertyByIndex
                // Call via base class type (NetNode3D/NetNode2D/NetNode) to use virtual dispatch
                // instead of interface dispatch (which would call the empty default implementation)
                if (propNode is NetNode3D netNode3D)
                {
                    netNode3D.SetNetPropertyByIndex(prop.LocalIndex, ref newValue);
                }
                else if (propNode is NetNode2D netNode2D)
                {
                    netNode2D.SetNetPropertyByIndex(prop.LocalIndex, ref newValue);
                }
                else if (propNode is NetNode netNodeBase)
                {
                    netNodeBase.SetNetPropertyByIndex(prop.LocalIndex, ref newValue);
                }
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
                    var prop = Protocol.UnpackProperty(_cachedSceneFilePath, propertyIndex);

                    var cache = new PropertyCache();

                    if (prop.VariantType == SerialVariantType.Object)
                    {
                        // Custom types with NetworkDeserialize - use generated deserializer delegate (no reflection)
                        var deserializer = Protocol.GetDeserializer(prop.ClassIndex);
                        if (deserializer == null)
                        {
                            Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"No deserializer found for {prop.NodePath}.{prop.Name}");
                            continue;
                        }
                        // Pass existing cached value for delta encoding support
                        var existingValue = propertyIndex < network.CachedProperties.Length
                            ? network.CachedProperties[propertyIndex].RefValue
                            : null;
                        var result = deserializer(network.CurrentWorld, null, buffer, existingValue);
                        
                        // Store custom value types in their proper PropertyCache field
                        // This matches how NetworkController.SetCachedValue stores them on the server
                        SetDeserializedValueToCache(result, ref cache);
                    }
                    else if (prop.VariantType == SerialVariantType.Nil)
                    {
                        Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Property {prop.NodePath}.{prop.Name} has VariantType.Nil, cannot deserialize");
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
        /// Stores a deserialized custom type value in the correct PropertyCache field.
        /// Mirrors the logic in NetworkController.SetCachedValue to ensure server and client use the same fields.
        /// </summary>
        private static void SetDeserializedValueToCache(object result, ref PropertyCache cache)
        {
            cache.Type = SerialVariantType.Object;
            
            // Store custom value types in their proper field (matching NetworkController.SetCachedValue)
            switch (result)
            {
                case NetId netId:
                    cache.NetIdValue = netId;
                    break;
                case UUID uuid:
                    cache.UUIDValue = uuid;
                    break;
                default:
                    // Reference types and unknown value types go in RefValue
                    cache.RefValue = result;
                    break;
            }
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
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Unsupported cache type: {cache.Type}");
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
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"No serializer found for {prop.NodePath}.{prop.Name}");
                return;
            }
            
            // Reuse pooled buffer instead of allocating new one each time (Fix #3)
            _customTypeBuffer ??= new NetBuffer();
            _customTypeBuffer.Reset();
            serializer(currentWorld, peer, ref cache, _customTypeBuffer);
            NetWriter.WriteBytes(buffer, _customTypeBuffer.WrittenSpan);
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
            
            // Cache IsNodeReady() once before the loop to avoid repeated Godot calls
            bool isReady = network.RawNode.IsNodeReady();
            
            foreach (var propIndex in data.properties.Keys)
            {
                var prop = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                // Get a ref to the value in the dictionary for zero-copy
                ref var propValue = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(data.properties, propIndex);
                if (isReady)
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
            return (Protocol.GetPropertyCount(_cachedSceneFilePath) / BitConstants.BitsInByte) + 1;
        }

        private HashSet<int> nonDefaultProperties = new();
        private Dictionary<UUID, Dictionary<Tick, byte[]>> peerBufferCache = new();
        
        /// <summary>
        /// Maximum number of ticks to cache per peer before forced pruning.
        /// This prevents unbounded memory growth if acknowledgments are delayed.
        /// TPS/2 = ~500ms which is plenty of time for acks on a healthy connection.
        /// </summary>
        private static int MaxCachedTicksPerPeer = NetRunner.TPS / 2;
        
        // Pooled byte arrays for dirty masks to avoid per-tick allocations
        private Dictionary<UUID, byte[]> _peerDirtyMaskPool = new();
        
        // Pooled buffer for custom type serialization (Fix #3)
        private NetBuffer _customTypeBuffer;

        private bool TryGetInterestLayers(UUID peerId, out long layers)
        {
            layers = 0;
            if (!network.InterestLayers.TryGetValue(peerId, out layers))
                return false;
            return layers != 0;
        }

        private bool PeerHasInterestInProperty(int propIndex, long peerInterestLayers)
        {
            var prop = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
            bool hasAnyInterest = (prop.InterestMask & peerInterestLayers) != 0;
            bool hasAllRequired = (prop.InterestRequired & peerInterestLayers) == prop.InterestRequired;
            return hasAnyInterest && hasAllRequired;
        }

        // Removed EnumerateSetBits - it used yield return which allocates an enumerator.
        // Iteration is now inlined at each call site to avoid allocation.

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
        
        public void Export(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            int byteCount = GetByteCountOfProperties();

            Array.Clear(_propertiesUpdated, 0, byteCount);
            Array.Clear(_filteredProps, 0, byteCount);

            // Fix #7: Use TryGetValue instead of ContainsKey + indexer
            if (!peerInitialPropSync.TryGetValue(peerId, out var initialSync))
            {
                initialSync = new byte[byteCount];
                peerInitialPropSync[peerId] = initialSync;
            }

            if (!currentWorld.HasSpawnedForClient(network.NetId, peer))
            {
                return;
            }

            // Fix #7: Use TryGetValue instead of ContainsKey + indexer
            if (!peerBufferCache.TryGetValue(peerId, out var currentPeerCache))
            {
                currentPeerCache = new Dictionary<Tick, byte[]>();
                peerBufferCache[peerId] = currentPeerCache;
            }

            // Fix #4: Pool the dirty mask byte arrays
            if (!currentPeerCache.TryGetValue(currentWorld.CurrentTick, out var cachedMask))
            {
                // Try to get from pool, otherwise allocate
                if (!_peerDirtyMaskPool.TryGetValue(peerId, out cachedMask) || cachedMask.Length != byteCount)
                {
                    cachedMask = new byte[byteCount];
                    _peerDirtyMaskPool[peerId] = cachedMask;
                }
                else
                {
                    Array.Clear(cachedMask, 0, byteCount);
                }
                currentPeerCache[currentWorld.CurrentTick] = cachedMask;
            }
            
            // Fix #6: Build sorted ticks list ONCE for both pruning and iteration
            _sortedTicks.Clear();
            foreach (var tick in currentPeerCache.Keys)
            {
                _sortedTicks.Add(tick);
            }
            _sortedTicks.Sort();
            
            // SAFEGUARD: Prune oldest ticks if cache exceeds limit to prevent unbounded growth
            if (_sortedTicks.Count > MaxCachedTicksPerPeer)
            {
                int ticksToRemove = _sortedTicks.Count - MaxCachedTicksPerPeer;
                for (int i = 0; i < ticksToRemove; i++)
                {
                    currentPeerCache.Remove(_sortedTicks[i]);
                }
                // Remove pruned ticks from our sorted list so we don't iterate them below
                _sortedTicks.RemoveRange(0, ticksToRemove);
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
                if ((initialSync[byteIndex] & propSlot) == 0)
                {
                    cachedMask[byteIndex] |= propSlot;
                }
            }

            // Use already-sorted _sortedTicks list (Fix #6 - no duplicate sorting)
            foreach (var tick in _sortedTicks)
            {
                FilterPropsAgainstInterestNoAlloc(peer, currentPeerCache[tick], _filteredProps);
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

            // Fix #2: Mark props as synced - inline iteration instead of EnumerateSetBits
            for (var byteIdx = 0; byteIdx < _propertiesUpdated.Length; byteIdx++)
            {
                var b = _propertiesUpdated[byteIdx];
                if (b == 0) continue; // Fast skip
                for (var bitIdx = 0; bitIdx < 8; bitIdx++)
                {
                    if ((b & (1 << bitIdx)) != 0)
                    {
                        initialSync[byteIdx] |= (byte)(1 << bitIdx);
                    }
                }
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
                if (propSegment == 0) continue; // Fast skip for empty segments
                
                for (var j = 0; j < BitConstants.BitsInByte; j++)
                {
                    if ((propSegment & (byte)(1 << j)) == 0)
                    {
                        continue;
                    }

                    var propIndex = i * BitConstants.BitsInByte + j;
                    var prop = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                    
                    try
                    {
                        WriteFromCache(currentWorld, peer, buffer, prop, propIndex);
                    }
                    catch (Exception ex)
                    {
                        var innerMsg = ex.InnerException?.Message ?? ex.Message;
                        var innerStack = ex.InnerException?.StackTrace ?? ex.StackTrace;
                        Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Error serializing property {prop.NodePath}.{prop.Name} from cache: {innerMsg}\n{innerStack}");
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

            // Fix #2: Inline iteration instead of EnumerateSetBits (avoids enumerator allocation)
            for (var byteIndex = 0; byteIndex < dirtyPropsMask.Length; byteIndex++)
            {
                var b = dirtyPropsMask[byteIndex];
                if (b == 0) continue; // Fast skip for empty bytes
                for (var bitIndex = 0; bitIndex < 8; bitIndex++)
                {
                    if ((b & (1 << bitIndex)) != 0)
                    {
                        var propIndex = byteIndex * 8 + bitIndex;
                        if (!PeerHasInterestInProperty(propIndex, peerInterestLayers))
                        {
                            ClearBit(result, propIndex);
                        }
                    }
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
        public void CleanupPeer(UUID peerId)
        {
            peerBufferCache.Remove(peerId);
            peerInitialPropSync.Remove(peerId);
            _peerDirtyMaskPool.Remove(peerId);
        }

        // Reusable list to avoid LINQ allocation in Acknowledge
        private List<Tick> _ticksToRemove = new();
        
        public void Acknowledge(WorldRunner currentWorld, NetPeer peer, Tick latestAck)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
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
            //     Debugger.Instance.Log(Debugger.DebugLevel.VERBOSE, $"[NetPropertiesSerializer ACK] Peer {peerId.ID} acked tick {latestAck}, removed {_ticksToRemove.Count} ticks, {tickCache.Count} remaining for node {network.RawNode.Name}");
            // }
        }

    }
}
