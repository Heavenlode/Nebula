using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Godot;
using Nebula.Utility.Tools;

namespace Nebula.Serialization.Serializers
{
    /// <summary>
    /// Delta encoding flags for property serialization.
    /// </summary>
    [Flags]
    public enum DeltaEncodingFlags : byte
    {
        /// <summary>Full value (initial sync, non-delta types, teleport)</summary>
        Absolute = 0,
        /// <summary>Small delta: half-float/short encoding</summary>
        DeltaSmall = 1,
        /// <summary>Full delta: same type as property</summary>
        DeltaFull = 2,
        /// <summary>Quaternion uses smallest-three encoding (6 bytes)</summary>
        QuatCompressed = 0x80,
    }

    public partial class NetPropertiesSerializer : RefCounted, IStateSerializer
    {
        private struct Data
        {
            public byte[] propertiesUpdated;
            public Dictionary<int, PropertyCache> properties;
        }

        /// <summary>
        /// Per-peer property state for delta encoding.
        /// </summary>
        private struct PeerPropertyState
        {
            public PropertyCache[] LastAcked;      // Last acknowledged values (swapped on ACK)
            public PropertyCache[] Pending;        // Currently in-flight values
            public byte[] AckedMask;               // Bit mask: has peer ever acked this property?
            public byte[] PendingDirtyMask;        // Properties sent but not yet acked (for re-sending)
            public bool IsInitialized;
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

        // ============================================================
        // DELTA ENCODING STATE
        // ============================================================
        
        /// <summary>Main state dictionary - access via CollectionsMarshal refs only</summary>
        private Dictionary<UUID, PeerPropertyState> _peerStates = new();
        
        /// <summary>Pool of pre-allocated states to avoid allocation on peer join</summary>
        private Stack<PeerPropertyState> _statePool = new();
        
        /// <summary>Pre-cached property count</summary>
        private readonly int _propertyCount;
        
        /// <summary>Pre-cached: does this property type support delta encoding?</summary>
        private readonly bool[] _propSupportsDelta;
        
        /// <summary>Pre-cached property types</summary>
        private readonly SerialVariantType[] _propTypes;

        /// <summary>
        /// Small delta threshold - deltas below this use half-float encoding.
        /// Based on half-float precision (~0.1 unit at magnitude 1024).
        /// </summary>
        private const float SmallDeltaThreshold = 1024f;
        private const float SmallDeltaThresholdSq = SmallDeltaThreshold * SmallDeltaThreshold;

        public NetPropertiesSerializer(NetworkController _network)
        {
            network = _network;
            
            // Cache SceneFilePath once to avoid Godot StringName allocations on every access
            _cachedSceneFilePath = network.RawNode.SceneFilePath;

            if (!network.IsNetScene())
            {
                _propertyCount = 0;
                _propSupportsDelta = Array.Empty<bool>();
                _propTypes = Array.Empty<SerialVariantType>();
                return;
            }

            // Pre-cache property metadata for zero-allocation hot path
            _propertyCount = Protocol.GetPropertyCount(_cachedSceneFilePath);
            _propSupportsDelta = new bool[_propertyCount];
            _propTypes = new SerialVariantType[_propertyCount];
            
            for (int i = 0; i < _propertyCount; i++)
            {
                var prop = Protocol.UnpackProperty(_cachedSceneFilePath, i);
                _propTypes[i] = prop.VariantType;
                _propSupportsDelta[i] = SupportsDelta(prop.VariantType);
            }

            int byteCount = GetByteCountOfProperties();
            if (_propertiesUpdated == null || _propertiesUpdated.Length != byteCount)
            {
                _propertiesUpdated = new byte[byteCount];
            }

            if (NetRunner.Instance.IsServer)
            {
                // Dirty tracking is now handled by NetworkController.MarkDirty() which sets DirtyMask
                // and populates CachedProperties. No more Godot signal subscription needed.

                network.InterestChanged += (UUID peerId, long oldInterest, long newInterest) =>
                {
                    // Handle interest changes for peerInitialPropSync
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
                            
                            // Also clear the acked mask for delta encoding
                            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(_peerStates, peerId);
                            if (!Unsafe.IsNullRef(ref state) && state.IsInitialized)
                            {
                                ClearBit(state.AckedMask, propIndex);
                            }
                        }
                    }
                };
            }
            else
            {
                foreach (var propIndex in cachedPropertyChanges.Keys)
                {
                    var prop = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                    ref var cachedValue = ref CollectionsMarshal.GetValueRefOrNullRef(cachedPropertyChanges, propIndex);
                    ImportProperty(prop, network.CurrentWorld.CurrentTick, ref cachedValue);
                }
            }
        }

        /// <summary>
        /// Determines if a property type supports delta encoding.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool SupportsDelta(SerialVariantType type)
        {
            return type switch
            {
                SerialVariantType.Float => true,
                SerialVariantType.Int => true,
                SerialVariantType.Vector2 => true,
                SerialVariantType.Vector3 => true,
                // Quaternion uses compressed absolute, not delta
                // Bool, String, arrays, Object don't support delta
                _ => false
            };
        }

        /// <summary>
        /// Creates a new PeerPropertyState, either from pool or fresh allocation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private PeerPropertyState CreateOrGetPooledState()
        {
            if (_statePool.Count > 0)
            {
                var state = _statePool.Pop();
                // Clear the arrays for reuse
                Array.Clear(state.LastAcked, 0, state.LastAcked.Length);
                Array.Clear(state.Pending, 0, state.Pending.Length);
                Array.Clear(state.AckedMask, 0, state.AckedMask.Length);
                Array.Clear(state.PendingDirtyMask, 0, state.PendingDirtyMask.Length);
                state.IsInitialized = true;
                return state;
            }
            
            int byteCount = GetByteCountOfProperties();
            return new PeerPropertyState
            {
                LastAcked = new PropertyCache[_propertyCount],
                Pending = new PropertyCache[_propertyCount],
                AckedMask = new byte[byteCount],
                PendingDirtyMask = new byte[byteCount],
                IsInitialized = true
            };
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
            
            // Copy old value BEFORE updating cache (needed for callback after value changes)
            PropertyCache oldValueSnapshot = oldValue;

            // Update cache (this is the target for interpolated properties and reconciliation)
            network.CachedProperties[prop.Index] = newValue;

            // ============================================================
            // PREDICTION CHECK: For owned predicted entities, don't directly
            // apply server state - reconciliation will handle it in WorldRunner.
            // We still update CachedProperties above for reconciliation comparison.
            // Only skip immediate application for predicted properties on owned entities
            // that aren't currently resimulating.
            // EXCEPTION: During initial spawn (IsWorldReady=false), always apply the
            // value so the entity starts with the correct server state.
            // ============================================================
            bool isOwnedPredicted = network.IsCurrentOwner 
                && prop.Predicted 
                && !network.IsResimulating 
                && NetRunner.Instance.IsClient
                && network.IsWorldReady;  // Allow initial spawn to apply values
            
            if (isOwnedPredicted)
            {
                // Don't apply immediately - reconciliation in WorldRunner will handle
                // The value is already in CachedProperties for StoreConfirmedState
                // Fire callback after cache update but before return (property not set yet for predicted)
                if (valueChanged && prop.NotifyOnChange)
                {
                    FirePropertyChangeCallback(propNode, prop.LocalIndex, tick, ref oldValueSnapshot, ref newValue);
                }
                return;
            }

            // For interpolated properties, don't set immediately - ProcessInterpolation will handle it
            // EXCEPTION: During initial spawn (IsWorldReady=false), apply directly so entity starts correct
            // For non-interpolated properties, set via generated setter (no Godot boundary)
            if (!prop.Interpolate || !network.IsWorldReady)
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
            
            // Fire change callbacks AFTER value is set (cache updated, property set if applicable)
            if (valueChanged && prop.NotifyOnChange)
            {
                FirePropertyChangeCallback(propNode, prop.LocalIndex, tick, ref oldValueSnapshot, ref newValue);
            }
        }
        
        /// <summary>
        /// Helper to fire property change callbacks via the correct base class type for virtual dispatch.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FirePropertyChangeCallback(Node propNode, int localIndex, Tick tick, ref PropertyCache oldValue, ref PropertyCache newValue)
        {
            // Use LocalIndex (cumulative class index) not Index (scene-global) - matches generated switch cases
            // Call via base class type to use virtual dispatch (not interface dispatch)
            if (propNode is NetNode3D nn3d)
            {
                nn3d.InvokePropertyChangeHandler(localIndex, tick, ref oldValue, ref newValue);
            }
            else if (propNode is NetNode2D nn2d)
            {
                nn2d.InvokePropertyChangeHandler(localIndex, tick, ref oldValue, ref newValue);
            }
            else if (propNode is NetNode nn)
            {
                nn.InvokePropertyChangeHandler(localIndex, tick, ref oldValue, ref newValue);
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
                    
                    // Get existing value for delta reconstruction
                    ref var existingCache = ref network.CachedProperties[propertyIndex];

                    if (prop.VariantType == SerialVariantType.Object)
                    {
                        // Custom types with NetworkDeserialize - always absolute (flag byte still present)
                        var flags = (DeltaEncodingFlags)NetReader.ReadByte(buffer);
                        
                        var deserializer = Protocol.GetDeserializer(prop.ClassIndex);
                        if (deserializer == null)
                        {
                            Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"No deserializer found for {prop.NodePath}.{prop.Name}");
                            continue;
                        }
                        var existingValue = existingCache.RefValue;
                        var result = deserializer(network.CurrentWorld, default, buffer, existingValue);
                        SetDeserializedValueToCache(result, ref cache);
                    }
                    else if (prop.VariantType == SerialVariantType.Nil)
                    {
                        Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Property {prop.NodePath}.{prop.Name} has VariantType.Nil, cannot deserialize");
                        continue;
                    }
                    else
                    {
                        // Read delta-encoded property (pass subtype for sized int types)
                        ReadDeltaOrAbsolute(buffer, prop.VariantType, prop.Metadata.TypeIdentifier, ref existingCache, ref cache);
                    }

                    data.properties[propertyIndex] = cache;
                }
            }
            return data;
        }

        /// <summary>
        /// Reads a property value with delta decoding support.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadDeltaOrAbsolute(NetBuffer buffer, SerialVariantType type, string subtype, ref PropertyCache existing, ref PropertyCache cache)
        {
            var flags = (DeltaEncodingFlags)NetReader.ReadByte(buffer);
            cache.Type = type;
            
            // Check for quaternion compressed encoding
            if ((flags & DeltaEncodingFlags.QuatCompressed) != 0)
            {
                cache.QuatValue = NetReader.ReadQuatSmallestThree(buffer);
                return;
            }
            
            // Get base encoding type (mask out compression flags)
            var encoding = flags & (DeltaEncodingFlags)0x7F;
            
            switch (encoding)
            {
                case DeltaEncodingFlags.Absolute:
                    // Full absolute value
                    ReadAbsoluteValue(buffer, type, subtype, ref cache);
                    break;
                    
                case DeltaEncodingFlags.DeltaSmall:
                    // Small delta (half-float/short encoding)
                    ReadSmallDelta(buffer, type, subtype, ref existing, ref cache);
                    break;
                    
                case DeltaEncodingFlags.DeltaFull:
                    // Full delta (same type as property)
                    ReadFullDelta(buffer, type, subtype, ref existing, ref cache);
                    break;
                    
                default:
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Unknown delta encoding flag: {flags}");
                    ReadAbsoluteValue(buffer, type, subtype, ref cache);
                    break;
            }
        }

        /// <summary>
        /// Reads an absolute property value (no delta).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadAbsoluteValue(NetBuffer buffer, SerialVariantType type, string subtype, ref PropertyCache cache)
        {
            switch (type)
            {
                case SerialVariantType.Bool:
                    cache.BoolValue = NetReader.ReadBool(buffer);
                    break;
                case SerialVariantType.Int:
                    // Check subtype for sized integer types (enums, byte, short, int, long)
                    // Clear LongValue first to ensure upper bytes are zero
                    cache.LongValue = 0;
                    switch (subtype)
                    {
                        case "byte":
                        case "System.Byte":
                            cache.ByteValue = NetReader.ReadByte(buffer);
                            break;
                        case "sbyte":
                        case "System.SByte":
                            cache.ByteValue = NetReader.ReadByte(buffer);
                            break;
                        case "short":
                        case "System.Int16":
                            cache.IntValue = NetReader.ReadInt16(buffer);
                            break;
                        case "ushort":
                        case "System.UInt16":
                            cache.IntValue = NetReader.ReadUInt16(buffer);
                            break;
                        case "int":
                        case "Int":
                        case "System.Int32":
                            cache.IntValue = NetReader.ReadInt32(buffer);
                            break;
                        case "uint":
                        case "System.UInt32":
                            cache.IntValue = (int)NetReader.ReadUInt32(buffer);
                            break;
                        default:
                            // Default to Int64 for long, ulong, or unknown subtypes
                            cache.LongValue = NetReader.ReadInt64(buffer);
                            break;
                    }
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
        /// Reads a small delta (half-float/short) and applies to existing value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadSmallDelta(NetBuffer buffer, SerialVariantType type, string subtype, ref PropertyCache existing, ref PropertyCache cache)
        {
            switch (type)
            {
                case SerialVariantType.Float:
                    float deltaF = NetReader.ReadHalfFloat(buffer);
                    cache.FloatValue = existing.FloatValue + deltaF;
                    break;
                    
                case SerialVariantType.Int:
                    // Small delta uses Int16 for all integer types
                    short deltaS = NetReader.ReadInt16(buffer);
                    // Store result in the appropriate field based on subtype
                    cache.LongValue = 0; // Clear first
                    switch (subtype)
                    {
                        case "byte":
                        case "System.Byte":
                        case "sbyte":
                        case "System.SByte":
                            cache.ByteValue = (byte)(existing.ByteValue + deltaS);
                            break;
                        case "short":
                        case "System.Int16":
                        case "ushort":
                        case "System.UInt16":
                        case "int":
                        case "Int":
                        case "System.Int32":
                        case "uint":
                        case "System.UInt32":
                            cache.IntValue = existing.IntValue + deltaS;
                            break;
                        default:
                            cache.LongValue = existing.LongValue + deltaS;
                            break;
                    }
                    break;
                    
                case SerialVariantType.Vector2:
                    float dx2 = NetReader.ReadHalfFloat(buffer);
                    float dy2 = NetReader.ReadHalfFloat(buffer);
                    cache.Vec2Value = new Vector2(existing.Vec2Value.X + dx2, existing.Vec2Value.Y + dy2);
                    break;
                    
                case SerialVariantType.Vector3:
                    float dx3 = NetReader.ReadHalfFloat(buffer);
                    float dy3 = NetReader.ReadHalfFloat(buffer);
                    float dz3 = NetReader.ReadHalfFloat(buffer);
                    cache.Vec3Value = new Vector3(
                        existing.Vec3Value.X + dx3,
                        existing.Vec3Value.Y + dy3,
                        existing.Vec3Value.Z + dz3);
                    break;
                    
                default:
                    // Fallback to absolute for unsupported small delta types
                    Debugger.Instance.Log(Debugger.DebugLevel.WARN, $"Small delta not supported for type {type}, reading absolute");
                    ReadAbsoluteValue(buffer, type, subtype, ref cache);
                    break;
            }
        }

        /// <summary>
        /// Reads a full delta and applies to existing value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadFullDelta(NetBuffer buffer, SerialVariantType type, string subtype, ref PropertyCache existing, ref PropertyCache cache)
        {
            switch (type)
            {
                case SerialVariantType.Float:
                    float deltaF = NetReader.ReadFloat(buffer);
                    cache.FloatValue = existing.FloatValue + deltaF;
                    break;
                    
                case SerialVariantType.Int:
                    // Full delta uses the same size as the property type for larger deltas
                    cache.LongValue = 0; // Clear first
                    switch (subtype)
                    {
                        case "byte":
                        case "System.Byte":
                        case "sbyte":
                        case "System.SByte":
                            // Byte types use Int16 for full delta (more range than byte)
                            short deltaB = NetReader.ReadInt16(buffer);
                            cache.ByteValue = (byte)(existing.ByteValue + deltaB);
                            break;
                        case "short":
                        case "System.Int16":
                        case "ushort":
                        case "System.UInt16":
                            short deltaS = NetReader.ReadInt16(buffer);
                            cache.IntValue = existing.IntValue + deltaS;
                            break;
                        case "int":
                        case "Int":
                        case "System.Int32":
                        case "uint":
                        case "System.UInt32":
                            int deltaI = NetReader.ReadInt32(buffer);
                            cache.IntValue = existing.IntValue + deltaI;
                            break;
                        default:
                            // Default to Int64 for long, ulong, or unknown subtypes
                            long deltaL = NetReader.ReadInt64(buffer);
                            cache.LongValue = existing.LongValue + deltaL;
                            break;
                    }
                    break;
                    
                case SerialVariantType.Vector2:
                    Vector2 deltaV2 = NetReader.ReadVector2(buffer);
                    cache.Vec2Value = existing.Vec2Value + deltaV2;
                    break;
                    
                case SerialVariantType.Vector3:
                    Vector3 deltaV3 = NetReader.ReadVector3(buffer);
                    cache.Vec3Value = existing.Vec3Value + deltaV3;
                    break;
                    
                default:
                    // Fallback to absolute for unsupported delta types
                    Debugger.Instance.Log(Debugger.DebugLevel.WARN, $"Full delta not supported for type {type}, reading absolute");
                    ReadAbsoluteValue(buffer, type, subtype, ref cache);
                    break;
            }
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
        
        // Pooled buffer for custom type serialization
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
        
        public void Export(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            int byteCount = GetByteCountOfProperties();

            Array.Clear(_propertiesUpdated, 0, byteCount);

            if (!peerInitialPropSync.TryGetValue(peerId, out var initialSync))
            {
                initialSync = new byte[byteCount];
                peerInitialPropSync[peerId] = initialSync;
            }

            if (!currentWorld.HasSpawnedForClient(network.NetId, peer))
            {
                return;
            }

            // Zero-alloc dictionary access via ref for delta state
            ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(_peerStates, peerId, out bool isNew);
            if (isNew || !state.IsInitialized)
            {
                state = CreateOrGetPooledState();
            }

            // Build dirty mask from processingDirtyMask
            for (int propIndex = 0; propIndex < 64 && propIndex < _propertyCount; propIndex++)
            {
                if ((processingDirtyMask & (1L << propIndex)) != 0)
                {
                    _propertiesUpdated[propIndex / BitConstants.BitsInByte] |= (byte)(1 << (propIndex % BitConstants.BitsInByte));
                }
            }

            // Include non-default properties that haven't been synced yet
            foreach (var propIndex in nonDefaultProperties)
            {
                var byteIndex = propIndex / BitConstants.BitsInByte;
                var propSlot = (byte)(1 << (propIndex % BitConstants.BitsInByte));
                if ((initialSync[byteIndex] & propSlot) == 0)
                {
                    _propertiesUpdated[byteIndex] |= propSlot;
                }
            }

            // Include properties that were sent but not yet acknowledged (for re-sending)
            for (var i = 0; i < state.PendingDirtyMask.Length && i < _propertiesUpdated.Length; i++)
            {
                _propertiesUpdated[i] |= state.PendingDirtyMask[i];
            }

            // Filter against interest layers
            if (!TryGetInterestLayers(peerId, out var peerInterestLayers))
            {
                return;
            }

            // Apply interest filter inline
            for (var byteIndex = 0; byteIndex < _propertiesUpdated.Length; byteIndex++)
            {
                var b = _propertiesUpdated[byteIndex];
                if (b == 0) continue;
                for (var bitIndex = 0; bitIndex < 8; bitIndex++)
                {
                    if ((b & (1 << bitIndex)) != 0)
                    {
                        var propIndex = byteIndex * 8 + bitIndex;
                        if (!PeerHasInterestInProperty(propIndex, peerInterestLayers))
                        {
                            _propertiesUpdated[byteIndex] &= (byte)~(1 << bitIndex);
                        }
                    }
                }
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

            // Mark props as synced in initialSync (for initial spawn tracking)
            // and add to PendingDirtyMask (for re-sending until acked)
            for (var byteIdx = 0; byteIdx < _propertiesUpdated.Length; byteIdx++)
            {
                var b = _propertiesUpdated[byteIdx];
                if (b == 0) continue;
                initialSync[byteIdx] |= b;
                state.PendingDirtyMask[byteIdx] |= b;
            }

            // Serialize the dirty mask
            for (var i = 0; i < _propertiesUpdated.Length; i++)
            {
                NetWriter.WriteByte(buffer, _propertiesUpdated[i]);
            }

            // Serialize property values with delta encoding
            for (var i = 0; i < _propertiesUpdated.Length; i++)
            {
                var propSegment = _propertiesUpdated[i];
                if (propSegment == 0) continue;
                
                for (var j = 0; j < BitConstants.BitsInByte; j++)
                {
                    if ((propSegment & (byte)(1 << j)) == 0)
                    {
                        continue;
                    }

                    var propIndex = i * BitConstants.BitsInByte + j;
                    
                    try
                    {
                        ref var current = ref network.CachedProperties[propIndex];
                        ref var acked = ref state.LastAcked[propIndex];
                        bool hasAcked = (state.AckedMask[i] & (1 << j)) != 0;
                        
                        // Write with delta encoding
                        WriteDeltaOrAbsolute(currentWorld, peer, buffer, propIndex, ref current, ref acked, hasAcked);
                        
                        // Track in pending state (struct copy, no allocation)
                        state.Pending[propIndex] = current;
                    }
                    catch (Exception ex)
                    {
                        var prop = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                        var innerMsg = ex.InnerException?.Message ?? ex.Message;
                        var innerStack = ex.InnerException?.StackTrace ?? ex.StackTrace;
                        Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Error serializing property {prop.NodePath}.{prop.Name}: {innerMsg}\n{innerStack}");
                    }
                }
            }
        }

        /// <summary>
        /// Writes a property value with delta encoding when applicable.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteDeltaOrAbsolute(
            WorldRunner currentWorld,
            NetPeer peer,
            NetBuffer buffer,
            int propIndex,
            ref PropertyCache current,
            ref PropertyCache acked,
            bool hasAcked)
        {
            var type = _propTypes[propIndex];
            
            // Non-delta types or first sync: send absolute
            if (!hasAcked || !_propSupportsDelta[propIndex])
            {
                // Quaternion: use smallest-three compression
                if (type == SerialVariantType.Quaternion)
                {
                    NetWriter.WriteByte(buffer, (byte)(DeltaEncodingFlags.Absolute | DeltaEncodingFlags.QuatCompressed));
                    NetWriter.WriteQuatSmallestThree(buffer, current.QuatValue);
                }
                else
                {
                    NetWriter.WriteByte(buffer, (byte)DeltaEncodingFlags.Absolute);
                    WriteAbsoluteValue(currentWorld, peer, buffer, propIndex, ref current);
                }
                return;
            }
            
            // Delta encoding path
            switch (type)
            {
                case SerialVariantType.Float:
                    float deltaF = current.FloatValue - acked.FloatValue;
                    if (MathF.Abs(deltaF) < SmallDeltaThreshold)
                    {
                        NetWriter.WriteByte(buffer, (byte)DeltaEncodingFlags.DeltaSmall);
                        NetWriter.WriteHalfFloat(buffer, deltaF);
                    }
                    else
                    {
                        NetWriter.WriteByte(buffer, (byte)DeltaEncodingFlags.DeltaFull);
                        NetWriter.WriteFloat(buffer, deltaF);
                    }
                    break;
                    
                case SerialVariantType.Int:
                    // Get the property subtype to read from the correct field
                    var intProp = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                    var intSubtype = intProp.Metadata.TypeIdentifier;
                    long currentVal, ackedVal;
                    
                    // Read current and acked values from the appropriate field
                    switch (intSubtype)
                    {
                        case "byte":
                        case "System.Byte":
                        case "sbyte":
                        case "System.SByte":
                            currentVal = current.ByteValue;
                            ackedVal = acked.ByteValue;
                            break;
                        case "short":
                        case "System.Int16":
                        case "ushort":
                        case "System.UInt16":
                        case "int":
                        case "Int":
                        case "System.Int32":
                        case "uint":
                        case "System.UInt32":
                            currentVal = current.IntValue;
                            ackedVal = acked.IntValue;
                            break;
                        default:
                            currentVal = current.LongValue;
                            ackedVal = acked.LongValue;
                            break;
                    }
                    
                    long deltaL = currentVal - ackedVal;
                    // Use small encoding for deltas that fit in short range
                    if (deltaL >= short.MinValue && deltaL <= short.MaxValue)
                    {
                        NetWriter.WriteByte(buffer, (byte)DeltaEncodingFlags.DeltaSmall);
                        NetWriter.WriteInt16(buffer, (short)deltaL);
                    }
                    else
                    {
                        // Full delta - write appropriate size based on subtype
                        NetWriter.WriteByte(buffer, (byte)DeltaEncodingFlags.DeltaFull);
                        switch (intSubtype)
                        {
                            case "byte":
                            case "System.Byte":
                            case "sbyte":
                            case "System.SByte":
                            case "short":
                            case "System.Int16":
                            case "ushort":
                            case "System.UInt16":
                                NetWriter.WriteInt16(buffer, (short)deltaL);
                                break;
                            case "int":
                            case "Int":
                            case "System.Int32":
                            case "uint":
                            case "System.UInt32":
                                NetWriter.WriteInt32(buffer, (int)deltaL);
                                break;
                            default:
                                NetWriter.WriteInt64(buffer, deltaL);
                                break;
                        }
                    }
                    break;
                    
                case SerialVariantType.Vector2:
                    Vector2 deltaV2 = current.Vec2Value - acked.Vec2Value;
                    float mag2 = deltaV2.LengthSquared();
                    if (mag2 < SmallDeltaThresholdSq)
                    {
                        NetWriter.WriteByte(buffer, (byte)DeltaEncodingFlags.DeltaSmall);
                        NetWriter.WriteHalfFloat(buffer, deltaV2.X);
                        NetWriter.WriteHalfFloat(buffer, deltaV2.Y);
                    }
                    else
                    {
                        NetWriter.WriteByte(buffer, (byte)DeltaEncodingFlags.DeltaFull);
                        NetWriter.WriteVector2(buffer, deltaV2);
                    }
                    break;
                    
                case SerialVariantType.Vector3:
                    Vector3 deltaV3 = current.Vec3Value - acked.Vec3Value;
                    float mag3 = deltaV3.LengthSquared();
                    if (mag3 < SmallDeltaThresholdSq)
                    {
                        NetWriter.WriteByte(buffer, (byte)DeltaEncodingFlags.DeltaSmall);
                        NetWriter.WriteHalfFloat(buffer, deltaV3.X);
                        NetWriter.WriteHalfFloat(buffer, deltaV3.Y);
                        NetWriter.WriteHalfFloat(buffer, deltaV3.Z);
                    }
                    else
                    {
                        NetWriter.WriteByte(buffer, (byte)DeltaEncodingFlags.DeltaFull);
                        NetWriter.WriteVector3(buffer, deltaV3);
                    }
                    break;
                    
                default:
                    // Fallback to absolute for any other types
                    NetWriter.WriteByte(buffer, (byte)DeltaEncodingFlags.Absolute);
                    WriteAbsoluteValue(currentWorld, peer, buffer, propIndex, ref current);
                    break;
            }
        }

        /// <summary>
        /// Writes an absolute property value (no delta encoding).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteAbsoluteValue(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, int propIndex, ref PropertyCache cache)
        {
            switch (cache.Type)
            {
                case SerialVariantType.Bool:
                    NetWriter.WriteBool(buffer, cache.BoolValue);
                    break;
                case SerialVariantType.Int:
                    // Check metadata for sized integer types (enums, byte, short, int, long)
                    var intProp = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                    switch (intProp.Metadata.TypeIdentifier)
                    {
                        case "byte":
                        case "System.Byte":
                            NetWriter.WriteByte(buffer, cache.ByteValue);
                            break;
                        case "sbyte":
                        case "System.SByte":
                            NetWriter.WriteByte(buffer, (byte)cache.ByteValue);
                            break;
                        case "short":
                        case "System.Int16":
                            NetWriter.WriteInt16(buffer, (short)cache.IntValue);
                            break;
                        case "ushort":
                        case "System.UInt16":
                            NetWriter.WriteUInt16(buffer, (ushort)cache.IntValue);
                            break;
                        case "int":
                        case "Int":
                        case "System.Int32":
                            NetWriter.WriteInt32(buffer, cache.IntValue);
                            break;
                        case "uint":
                        case "System.UInt32":
                            NetWriter.WriteUInt32(buffer, (uint)cache.IntValue);
                            break;
                        default:
                            // Default to Int64 for long, ulong, or unknown subtypes
                            NetWriter.WriteInt64(buffer, cache.LongValue);
                            break;
                    }
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
                    var prop = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                    WriteCustomTypeFromCache(currentWorld, peer, buffer, prop, ref cache);
                    break;
                default:
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Unsupported cache type: {cache.Type}");
                    break;
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
        /// Returns the PeerPropertyState to the pool for reuse.
        /// </summary>
        public void CleanupPeer(UUID peerId)
        {
            peerInitialPropSync.Remove(peerId);
            
            // Return the state to the pool for reuse
            if (_peerStates.TryGetValue(peerId, out var state) && state.IsInitialized)
            {
                _statePool.Push(state);
            }
            _peerStates.Remove(peerId);
        }
        
        public void Acknowledge(WorldRunner currentWorld, NetPeer peer, Tick latestAck)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            
            // Zero-alloc ref access
            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(_peerStates, peerId);
            if (Unsafe.IsNullRef(ref state) || !state.IsInitialized)
            {
                return;
            }
            
            // O(1) array swap - Pending becomes LastAcked
            (state.LastAcked, state.Pending) = (state.Pending, state.LastAcked);
            
            // Mark all pending properties as acked (for delta encoding initial sync detection)
            for (int i = 0; i < state.AckedMask.Length && i < state.PendingDirtyMask.Length; i++)
            {
                state.AckedMask[i] |= state.PendingDirtyMask[i];
            }
            
            // Clear pending dirty mask - these properties are now acknowledged
            Array.Clear(state.PendingDirtyMask, 0, state.PendingDirtyMask.Length);
        }

    }
}
