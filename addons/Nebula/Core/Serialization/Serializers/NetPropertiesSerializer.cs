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

    /// <summary>
    /// The wire width of an integer property, resolved once from its declared subtype string.
    ///
    /// Every read and write path for Int properties used to re-derive this by string-switching
    /// on ProtocolNetProperty.Metadata.TypeIdentifier, which meant four separate switch blocks
    /// had to agree on the spelling of every alias ("int" / "Int" / "System.Int32" / ...). One
    /// missing case in any of them silently picks a different width than its counterpart and
    /// misaligns the rest of the stream. Resolving to this enum once, in the constructor, gives
    /// reader and writer a single shared source of truth.
    /// </summary>
    public enum IntWidth : byte
    {
        /// <summary>long / ulong / unrecognised subtype. Stored in PropertyCache.LongValue.</summary>
        Int64 = 0,
        /// <summary>byte / sbyte. Stored in PropertyCache.ByteValue.</summary>
        Byte,
        /// <summary>short. Stored in PropertyCache.IntValue.</summary>
        Int16,
        /// <summary>ushort. Stored in PropertyCache.IntValue.</summary>
        UInt16,
        /// <summary>int. Stored in PropertyCache.IntValue.</summary>
        Int32,
        /// <summary>uint. Stored in PropertyCache.IntValue.</summary>
        UInt32,
    }

    public partial class NetPropertiesSerializer : RefCounted, IStateSerializer
    {
        private struct Data
        {
            public byte[] propertiesUpdated;
            public Dictionary<int, PropertyCache> properties;
        }

        /// <summary>
        /// A record of which primitive properties were included in the export at a given tick.
        /// Ring-indexed by tick % SNAPSHOT_RING_SIZE; Tick disambiguates stale slots.
        /// </summary>
        private struct SentRecord
        {
            public Tick Tick;
            public long SentMask;
        }

        /// <summary>
        /// A snapshot of all property values at a given tick.
        /// Server: values as exported. Client: values as applied.
        /// Ring-indexed by tick % SNAPSHOT_RING_SIZE; Tick disambiguates stale slots.
        /// </summary>
        private struct TickSnapshot
        {
            public Tick Tick;
            public PropertyCache[] Values;
        }

        /// <summary>
        /// Per-peer property state for snapshot-delta encoding.
        ///
        /// Correctness invariant: pending (unacked) dirty properties are re-included in
        /// EVERY export until acked. So when a peer acks tick N, the tick-N packet
        /// contained the tick-N value of every then-unacked property, and every other
        /// acked property was unchanged at N — meaning the client's applied state at any
        /// acked send-tick exactly equals the server's snapshot at that tick. Deltas are
        /// therefore computed against the snapshot at the latest acked send-tick, and the
        /// client applies them against its own recorded state at that tick.
        /// </summary>
        private struct PeerPropertyState
        {
            public byte[] AckedMask;               // Bit mask: has an ack confirmed the peer received this property?
            public byte[] PendingDirtyMask;        // Properties sent but not yet acked (for re-sending)
            public SentRecord[] SentHistory;       // Which props were sent at each recent tick
            public Tick LatestAckedTick;           // Latest acked tick at which this node sent data (-1 = none)
            public byte[] DeltaChain;              // Consecutive delta sends per prop; forces periodic absolute refresh
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

        /// <summary>Pre-cached: is this property an object property (INetSerializable)?</summary>
        private readonly bool[] _propIsObject;

        /// <summary>Pre-cached: property class indices for object properties (for lifecycle callbacks)</summary>
        private readonly int[] _propClassIndex;

        /// <summary>Pre-cached: is this property a per-peer property (different value for each peer)?</summary>
        private readonly bool[] _propIsPerPeer;

        /// <summary>
        /// Pre-cached interest metadata. These come from the [NetProperty] attribute and are
        /// baked into the generated protocol tables at build time - they declare which layers
        /// a property belongs to and never change at runtime. The mutable half of the interest
        /// check is the PEER's layers, which Export re-reads every tick via TryGetInterestLayers.
        /// </summary>
        private readonly long[] _propInterestMask;
        private readonly long[] _propInterestRequired;

        /// <summary>Pre-cached: wire width for Int properties, resolved once from the subtype string.</summary>
        private readonly IntWidth[] _propIntWidth;

        /// <summary>Pre-cached: chunk budget handed to object/custom-type serializers.</summary>
        private readonly int[] _propChunkBudget;

        /// <summary>Pre-cached size in bytes of the property presence mask.</summary>
        private readonly int _byteCount;

        /// <summary>Pre-cached: does this scene have any object (INetSerializable) properties?</summary>
        private readonly bool _hasObjectProps;

        /// <summary>Handler registered on network.InterestChanged; kept so it can be unsubscribed.</summary>
        private readonly Action<UUID, long, long> _interestChangedHandler;

        /// <summary>Whether FlushPendingChanges was connected to RawNode.Ready (client only).</summary>
        private readonly bool _readyHandlerAttached;

        /// <summary>
        /// Small delta threshold - deltas below this use half-float encoding.
        /// Based on half-float precision (~0.1 unit at magnitude 1024).
        /// </summary>
        private const float SmallDeltaThreshold = 1024f;
        private const float SmallDeltaThresholdSq = SmallDeltaThreshold * SmallDeltaThreshold;

        /// <summary>
        /// Depth of the tick snapshot rings (server value history / client applied history).
        /// Must exceed MAX_DELTA_AGE so any baseline the server picks is still resolvable.
        /// </summary>
        private const int SNAPSHOT_RING_SIZE = 32;

        /// <summary>
        /// Oldest baseline (in ticks) the server will delta against. Older acks (stalled
        /// peer, interest regain after a long gap) fall back to absolute values. ~1s at 30Hz.
        /// </summary>
        private const int MAX_DELTA_AGE = 30;

        /// <summary>
        /// After this many consecutive delta sends of a property, force one absolute send.
        /// Bounds accumulated half-float quantization drift on the client.
        /// </summary>
        private const int REFRESH_CHAIN = 30;

        /// <summary>
        /// Server: ring of property value snapshots per exported tick, shared by all peers.
        /// Captured in Begin(). Indexed by tick % SNAPSHOT_RING_SIZE.
        /// </summary>
        private TickSnapshot[] _tickValueRing;

        /// <summary>
        /// Client: ring of applied server state per received tick. Written after each import.
        /// Indexed by tick % SNAPSHOT_RING_SIZE.
        /// </summary>
        private TickSnapshot[] _appliedRing;

        /// <summary>
        /// Client: the most recent tick written to _appliedRing (-1 = none).
        /// New entries copy forward from this one so unsent properties carry their values.
        /// </summary>
        private Tick _lastAppliedTick = -1;

        public NetPropertiesSerializer(NetworkController _network)
        {
            network = _network;

            // Cache SceneFilePath once to avoid Godot StringName allocations on every access
            _cachedSceneFilePath = network.RawNode.SceneFilePath;

            if (!network.IsNetScene())
            {
                // A non-NetScene node has no networked properties, so WorldRunner never
                // registers it for export and Export/Import are unreachable. Everything is
                // still initialised to empty rather than left null so that a stray call
                // degrades to "nothing to send" instead of a NullReferenceException.
                _propertyCount = 0;
                _byteCount = 0;
                _propSupportsDelta = Array.Empty<bool>();
                _propTypes = Array.Empty<SerialVariantType>();
                _propIsObject = Array.Empty<bool>();
                _propClassIndex = Array.Empty<int>();
                _propIsPerPeer = Array.Empty<bool>();
                _propInterestMask = Array.Empty<long>();
                _propInterestRequired = Array.Empty<long>();
                _propIntWidth = Array.Empty<IntWidth>();
                _propChunkBudget = Array.Empty<int>();
                _propertiesUpdated = Array.Empty<byte>();
                return;
            }

            // Pre-cache property metadata for zero-allocation hot path
            _propertyCount = Protocol.GetPropertyCount(_cachedSceneFilePath);

            // Defense in depth behind the NEBULA004 build-time check: the 64-bit dirty
            // mask and fixed-size CachedProperties cannot represent more properties, and
            // exceeding the limit silently corrupts sync (bit 64 aliases bit 0). Fail
            // loudly at scene setup rather than desyncing at runtime.
            if (_propertyCount > BitConstants.MaxSceneProperties)
            {
                throw new InvalidOperationException(
                    $"NetScene '{_cachedSceneFilePath}' has {_propertyCount} networked properties, exceeding the maximum of {BitConstants.MaxSceneProperties} per scene. " +
                    "Move properties onto nested NetScenes (which have their own limit), or aggregate related values into a single property such as a NetArray or a custom INetSerializable type.");
            }

            _propSupportsDelta = new bool[_propertyCount];
            _propTypes = new SerialVariantType[_propertyCount];
            _propIsObject = new bool[_propertyCount];
            _propClassIndex = new int[_propertyCount];
            _propIsPerPeer = new bool[_propertyCount];
            _propInterestMask = new long[_propertyCount];
            _propInterestRequired = new long[_propertyCount];
            _propIntWidth = new IntWidth[_propertyCount];
            _propChunkBudget = new int[_propertyCount];

            for (int i = 0; i < _propertyCount; i++)
            {
                var prop = Protocol.UnpackProperty(_cachedSceneFilePath, i);
                _propTypes[i] = prop.VariantType;
                _propSupportsDelta[i] = SupportsDelta(prop.VariantType);
                _propIsObject[i] = prop.IsObjectProperty;
                _propClassIndex[i] = prop.ClassIndex;
                _propIsPerPeer[i] = prop.IsPerPeer;
                _propInterestMask[i] = prop.InterestMask;
                _propInterestRequired[i] = prop.InterestRequired;
                _propIntWidth[i] = ResolveIntWidth(prop.Metadata.TypeIdentifier);
                _propChunkBudget[i] = prop.ChunkBudget;
                if (prop.IsObjectProperty) _hasObjectProps = true;
            }

            _byteCount = (_propertyCount + BitConstants.BitsInByte - 1) / BitConstants.BitsInByte;
            _propertiesUpdated = new byte[_byteCount];

            if (NetRunner.Instance.IsServer)
            {
                // Dirty tracking is now handled by NetworkController.MarkDirty() which sets DirtyMask
                // and populates CachedProperties. No more Godot signal subscription needed.

                _interestChangedHandler = (UUID peerId, long oldInterest, long newInterest) =>
                {
                    // Handle interest changes for peerInitialPropSync
                    if (!peerInitialPropSync.TryGetValue(peerId, out var syncMask))
                        return;

                    foreach (var propIndex in nonDefaultProperties)
                    {
                        if (propIndex >= _propertyCount) continue;

                        long interestMask = _propInterestMask[propIndex];
                        long interestRequired = _propInterestRequired[propIndex];

                        bool wasVisible = (interestMask & oldInterest) != 0
                            && (interestRequired & oldInterest) == interestRequired;
                        bool isNowVisible = (interestMask & newInterest) != 0
                            && (interestRequired & newInterest) == interestRequired;

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
                network.InterestChanged += _interestChangedHandler;
            }
            else
            {
                // Properties can arrive before RawNode._Ready has run; Import stashes them
                // in cachedPropertyChanges. The tick is acked regardless, so the server
                // never resends them — flushing when the node becomes ready is the only
                // delivery path for those values.
                network.RawNode.Ready += FlushPendingChanges;
                _readyHandlerAttached = true;
            }
        }

        /// <summary>
        /// Detaches from NetworkController/RawNode events.
        ///
        /// The serializer normally dies with its node, but a NetworkController can outlive
        /// a serializer instance (world migration rebuilds the serializer array), and a
        /// closure left subscribed would keep firing against dead per-peer state - and
        /// accumulate one more handler per rebuild.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && network != null)
            {
                if (_interestChangedHandler != null)
                {
                    network.InterestChanged -= _interestChangedHandler;
                }
                // Only detach what was actually attached - Godot logs an error when asked to
                // disconnect a connection that was never made.
                if (_readyHandlerAttached && network.RawNode != null && GodotObject.IsInstanceValid(network.RawNode))
                {
                    network.RawNode.Ready -= FlushPendingChanges;
                }
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Resolves the declared subtype string of an Int property to its wire width. Called
        /// once per property at construction; every hot path reads <see cref="_propIntWidth"/>.
        /// The alias set must stay in sync with NetReader.ReadAbsoluteValue's Int case.
        /// </summary>
        private static IntWidth ResolveIntWidth(string subtype)
        {
            switch (subtype)
            {
                case "byte":
                case "System.Byte":
                case "sbyte":
                case "System.SByte":
                    return IntWidth.Byte;
                case "short":
                case "System.Int16":
                    return IntWidth.Int16;
                case "ushort":
                case "System.UInt16":
                    return IntWidth.UInt16;
                case "int":
                case "Int":
                case "System.Int32":
                    return IntWidth.Int32;
                case "uint":
                case "System.UInt32":
                    return IntWidth.UInt32;
                default:
                    // long, ulong, or an unrecognised subtype - the reader's default is Int64 too.
                    return IntWidth.Int64;
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
                Array.Clear(state.AckedMask, 0, state.AckedMask.Length);
                Array.Clear(state.PendingDirtyMask, 0, state.PendingDirtyMask.Length);
                Array.Clear(state.SentHistory, 0, state.SentHistory.Length);
                for (int i = 0; i < state.SentHistory.Length; i++)
                {
                    state.SentHistory[i].Tick = -1;
                }
                Array.Clear(state.DeltaChain, 0, state.DeltaChain.Length);
                state.LatestAckedTick = -1;
                state.IsInitialized = true;
                return state;
            }

            var fresh = new PeerPropertyState
            {
                AckedMask = new byte[_byteCount],
                PendingDirtyMask = new byte[_byteCount],
                SentHistory = new SentRecord[SNAPSHOT_RING_SIZE],
                LatestAckedTick = -1,
                DeltaChain = new byte[_propertyCount],
                IsInitialized = true
            };
            for (int i = 0; i < fresh.SentHistory.Length; i++)
            {
                fresh.SentHistory[i].Tick = -1;
            }
            return fresh;
        }

        /// <summary>
        /// Float equality that treats NaN as equal to itself.
        ///
        /// Plain == reports NaN != NaN, so a property that ever goes NaN would be seen as
        /// "changed" on every single import forever, re-firing NotifyOnChange handlers each
        /// tick. Single.Equals is the standard reflexive comparison (it also folds -0 and +0).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool FloatEquals(float a, float b) => a.Equals(b);

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
                SerialVariantType.Float => FloatEquals(a.FloatValue, b.FloatValue),
                SerialVariantType.String => a.StringValue == b.StringValue,
                SerialVariantType.Vector2 => FloatEquals(a.Vec2Value.X, b.Vec2Value.X)
                    && FloatEquals(a.Vec2Value.Y, b.Vec2Value.Y),
                SerialVariantType.Vector3 => FloatEquals(a.Vec3Value.X, b.Vec3Value.X)
                    && FloatEquals(a.Vec3Value.Y, b.Vec3Value.Y)
                    && FloatEquals(a.Vec3Value.Z, b.Vec3Value.Z),
                SerialVariantType.Quaternion => FloatEquals(a.QuatValue.X, b.QuatValue.X)
                    && FloatEquals(a.QuatValue.Y, b.QuatValue.Y)
                    && FloatEquals(a.QuatValue.Z, b.QuatValue.Z)
                    && FloatEquals(a.QuatValue.W, b.QuatValue.W),
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
            // Debugger.Instance.Log($"[ImportProperty] START - prop.Index={prop.Index}, prop.LocalIndex={prop.LocalIndex}, prop.NodePath={prop.NodePath}, prop.Name={prop.Name}");

            // Get the node that owns this property (cached to avoid GetNode allocations)
            Node propNode;
            try
            {
                propNode = GetCachedNode(prop.NodePath);
                // Debugger.Instance.Log($"[ImportProperty] GetCachedNode returned: {propNode?.GetType().Name ?? "null"}, Name={propNode?.Name ?? "null"}");
            }
            catch (System.Exception ex)
            {
                // Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"[ImportProperty] GetCachedNode threw: {ex.Message}");
                throw;
            }

            if (propNode is not INetNodeBase netNode)
            {
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Property node {prop.NodePath} is not INetNodeBase, cannot import");
                return;
            }

            // Debugger.Instance.Log($"[ImportProperty] Accessing CachedProperties[{prop.Index}], Length={network.CachedProperties.Length}");
            // Get old value from cache (no Godot boundary crossing)
            ref var oldValue = ref network.CachedProperties[prop.Index];

            // For object types (INetSerializable), always consider them changed when received.
            // These types (like NetArray) are deserialized in-place and track their own changes
            // internally. The fact that we received data means something changed.
            // For value types, do a proper equality check.
            bool valueChanged = (prop.VariantType == SerialVariantType.Object)
                || !PropertyCacheEquals(ref oldValue, ref newValue);

            // Copy old value BEFORE updating cache (needed for callback after value changes)
            PropertyCache oldValueSnapshot = oldValue;

            // Update cache (this is the target for interpolated properties and reconciliation)
            network.CachedProperties[prop.Index] = newValue;

            // Store in snapshot buffer for interpolation (client-side, interpolated properties only)
            if (NetRunner.Instance.IsClient && network.IsWorldReady && prop.Interpolate)
            {
                network.UpdateSnapshotProperty(prop.Index, ref newValue);
            }

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
                // Debugger.Instance.Log($"[ImportProperty] Calling SetNetPropertyByIndex - propNode.Type={propNode.GetType().Name}, LocalIndex={prop.LocalIndex}");
                try
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
                    // Debugger.Instance.Log($"[ImportProperty] SetNetPropertyByIndex completed successfully");
                }
                catch (System.Exception ex)
                {
                    // Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"[ImportProperty] SetNetPropertyByIndex threw: {ex.GetType().Name}: {ex.Message}\nStack: {ex.StackTrace}");
                    throw;
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

        private Data Deserialize(NetBuffer buffer, Tick currentTick)
        {
            int startPos = buffer.ReadPosition;
            int byteCount = _byteCount;

            var data = new Data
            {
                propertiesUpdated = new byte[byteCount],
                properties = new()
            };

            for (byte i = 0; i < data.propertiesUpdated.Length; i++)
            {
                data.propertiesUpdated[i] = NetReader.ReadByte(buffer);
            }

            // ============================================================
            // BASELINE RESOLUTION (snapshot-delta)
            // ============================================================
            // Deltas in this payload were computed by the server against its value
            // snapshot at baselineTick - a tick this client received, applied, and acked.
            // They must be applied against OUR recorded state at that same tick, never
            // against the running value (which may include newer in-flight updates).
            int baselineAge = NetReader.ReadByte(buffer);
            PropertyCache[] baselineValues = null;
            bool discardPayload = false;

            // Scratch baseline handed to ReadDeltaOrAbsolute when this payload has no
            // resolvable baseline. A local (not a shared static) so that a future edit which
            // writes through the ref cannot leak state across nodes or across ticks.
            PropertyCache noBaseline = default;
            if (baselineAge > 0)
            {
                Tick baselineTick = currentTick - baselineAge;
                if (_appliedRing != null)
                {
                    ref var baseEntry = ref _appliedRing[baselineTick % SNAPSHOT_RING_SIZE];
                    if (baseEntry.Values != null && baseEntry.Tick == baselineTick)
                    {
                        baselineValues = baseEntry.Values;
                    }
                }

                if (baselineValues == null)
                {
                    // Should be unreachable: the server only picks acked send-ticks within
                    // MAX_DELTA_AGE, and we record an entry for every applied payload.
                    // Parse the payload to keep the stream aligned, but discard the values.
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                        $"[NetPropertiesSerializer.Deserialize] NetId={network.NetId} missing applied-state baseline for tick {baselineTick} (age {baselineAge}). Discarding payload.");
                    discardPayload = true;
                }
            }

            // ============================================================
            // TWO-PASS DESERIALIZATION (must match server Export order)
            // Pass 1: Read PRIMITIVE properties first
            // Pass 2: Read OBJECT properties second
            // ============================================================

            // Pass 1: Read PRIMITIVE properties (non-IsObjectProperty)
            // Note: We use IsObjectProperty (INetSerializable vs INetValue) NOT VariantType
            // to match the server's Export order which uses _propIsObject[]
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
                    if (string.IsNullOrEmpty(prop.Name))
                    {
                        continue;
                    }

                    // Skip IsObjectProperty (INetSerializable) - handled in Pass 2
                    if (prop.IsObjectProperty)
                    {
                        continue;
                    }

                    // Bounded by _propertyCount, not CachedProperties.Length: the latter is a
                    // fixed 64 regardless of how many properties this scene declares, so it
                    // would admit indices that have no entry in the pre-cached metadata arrays.
                    if (propertyIndex >= _propertyCount)
                    {
                        Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"[NetPropertiesSerializer.Deserialize] propertyIndex {propertyIndex} >= property count {_propertyCount}! Skipping property.");
                        continue;
                    }
                    ref var existingCache = ref network.CachedProperties[propertyIndex];

                    // int propStartPos = buffer.ReadPosition;
                    var cache = new PropertyCache();

                    if (prop.VariantType == SerialVariantType.Nil)
                    {
                        Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Property {prop.NodePath}.{prop.Name} has VariantType.Nil, cannot deserialize");
                        continue;
                    }

                    // INetValue types with Object VariantType (like UUID) need special handling
                    // They're written with delta encoding wrapper (Absolute flag byte first) but use custom deserializer
                    if (prop.VariantType == SerialVariantType.Object)
                    {
                        // Read the delta encoding flag byte (will always be Absolute for Object types since they don't support delta)
                        var flags = (DeltaEncodingFlags)NetReader.ReadByte(buffer);
                        if (flags != DeltaEncodingFlags.Absolute)
                        {
                            Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Expected Absolute encoding for INetValue Object type {prop.NodePath}.{prop.Name}, got {flags}");
                        }

                        // Use the deserializer for the value type
                        var deserializer = Protocol.GetDeserializer(prop.ClassIndex);
                        if (deserializer == null)
                        {
                            Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"No deserializer found for INetValue {prop.NodePath}.{prop.Name}");
                            continue;
                        }
                        var existingValue = existingCache.RefValue;
                        var result = deserializer(network.CurrentWorld, default, buffer, existingValue);
                        SetDeserializedValueToCache(result, ref cache);
                    }
                    else
                    {
                        // Read the value, applying deltas against the baseline snapshot.
                        // With no baseline (absolute payload or discard mode) a scratch
                        // default is passed - deltas can't occur in a well-formed absolute
                        // payload. The absolute path still needs the raw subtype string,
                        // since NetReader.ReadAbsoluteValue is shared with other call sites.
                        if (baselineValues != null)
                        {
                            ReadDeltaOrAbsolute(buffer, prop.VariantType, _propIntWidth[propertyIndex], prop.Metadata.TypeIdentifier, ref baselineValues[propertyIndex], ref cache);
                        }
                        else
                        {
                            ReadDeltaOrAbsolute(buffer, prop.VariantType, _propIntWidth[propertyIndex], prop.Metadata.TypeIdentifier, ref noBaseline, ref cache);
                        }
                    }

                    // Debugger.Instance.Log(Debugger.DebugLevel.VERBOSE, $"[Props.R] idx={propertyIndex} '{prop.NodePath}.{prop.Name}' type={prop.VariantType} bytes={buffer.ReadPosition - propStartPos}");

                    if (!discardPayload)
                    {
                        data.properties[propertyIndex] = cache;
                    }
                }
            }

            // Pass 2: Read OBJECT properties (IsObjectProperty = INetSerializable types)
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
                    if (string.IsNullOrEmpty(prop.Name))
                    {
                        continue;
                    }

                    // Only process IsObjectProperty (INetSerializable) in this pass
                    if (!prop.IsObjectProperty)
                    {
                        continue;
                    }

                    if (propertyIndex >= _propertyCount)
                    {
                        Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"[NetPropertiesSerializer.Deserialize] propertyIndex {propertyIndex} >= property count {_propertyCount}! Skipping property.");
                        continue;
                    }
                    ref var existingCache = ref network.CachedProperties[propertyIndex];

                    // int propStartPos = buffer.ReadPosition;
                    var cache = new PropertyCache();

                    var deserializer = Protocol.GetDeserializer(prop.ClassIndex);
                    if (deserializer == null)
                    {
                        Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"No deserializer found for {prop.NodePath}.{prop.Name}");
                        continue;
                    }
                    var existingValue = existingCache.RefValue;
                    var result = deserializer(network.CurrentWorld, default, buffer, existingValue);
                    SetDeserializedValueToCache(result, ref cache);

                    // Debugger.Instance.Log(Debugger.DebugLevel.VERBOSE, $"[Props.R] idx={propertyIndex} '{prop.NodePath}.{prop.Name}' type=Object bytes={buffer.ReadPosition - propStartPos}");

                    data.properties[propertyIndex] = cache;
                }
            }

            // ============================================================
            // RECORD APPLIED STATE (snapshot-delta)
            // ============================================================
            // Store this tick's post-apply state so future payloads can delta against it.
            // Entries copy forward from the previous one (or CachedProperties for the
            // first), so properties not in this payload keep their carried values - which,
            // by the resend-until-acked invariant, match the server's snapshot exactly.
            if (!discardPayload && _propertyCount > 0)
            {
                _appliedRing ??= new TickSnapshot[SNAPSHOT_RING_SIZE];
                ref var entry = ref _appliedRing[currentTick % SNAPSHOT_RING_SIZE];
                entry.Values ??= new PropertyCache[_propertyCount];

                if (_lastAppliedTick >= 0)
                {
                    ref var prevEntry = ref _appliedRing[_lastAppliedTick % SNAPSHOT_RING_SIZE];
                    if (prevEntry.Values != null && prevEntry.Tick == _lastAppliedTick && _lastAppliedTick != currentTick)
                    {
                        Array.Copy(prevEntry.Values, entry.Values, _propertyCount);
                    }
                }
                else
                {
                    Array.Copy(network.CachedProperties, entry.Values, _propertyCount);
                }

                foreach (var propIndex in data.properties.Keys)
                {
                    if (propIndex >= _propertyCount) continue;
                    ref var decoded = ref CollectionsMarshal.GetValueRefOrNullRef(data.properties, propIndex);
                    entry.Values[propIndex] = decoded;
                }

                entry.Tick = currentTick;
                _lastAppliedTick = currentTick;
            }

            // Debugger.Instance.Log(Debugger.DebugLevel.VERBOSE, $"[Props.Import] NetId={network.NetId} total={buffer.ReadPosition - startPos} endPos={buffer.ReadPosition}");
            return data;
        }

        /// <summary>
        /// Reads a property value with delta decoding support. Deltas are applied against
        /// the baseline snapshot value (the client's recorded state at the packet's
        /// declared baseline tick), never against the running value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadDeltaOrAbsolute(NetBuffer buffer, SerialVariantType type, IntWidth intWidth, string subtype, ref PropertyCache baseline, ref PropertyCache cache)
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
                    ReadSmallDelta(buffer, type, intWidth, subtype, ref baseline, ref cache);
                    break;

                case DeltaEncodingFlags.DeltaFull:
                    // Full delta (same type as property)
                    ReadFullDelta(buffer, type, intWidth, subtype, ref baseline, ref cache);
                    break;

                default:
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Unknown delta encoding flag: {flags}");
                    ReadAbsoluteValue(buffer, type, subtype, ref cache);
                    break;
            }
        }

        /// <summary>
        /// Reads an absolute property value (no delta).
        /// Delegates to NetReader.ReadAbsoluteValue for reuse.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadAbsoluteValue(NetBuffer buffer, SerialVariantType type, string subtype, ref PropertyCache cache)
        {
            NetReader.ReadAbsoluteValue(buffer, type, subtype, ref cache);
        }

        /// <summary>
        /// Reads a small delta (half-float/short) and applies it to the baseline value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadSmallDelta(NetBuffer buffer, SerialVariantType type, IntWidth intWidth, string subtype, ref PropertyCache baseline, ref PropertyCache cache)
        {
            switch (type)
            {
                case SerialVariantType.Float:
                    float deltaF = NetReader.ReadHalfFloat(buffer);
                    cache.FloatValue = baseline.FloatValue + deltaF;
                    break;

                case SerialVariantType.Int:
                    // Small delta uses Int16 for all integer types
                    short deltaS = NetReader.ReadInt16(buffer);
                    // Store result in the field this width uses (see IntWidth)
                    cache.LongValue = 0; // Clear first
                    switch (intWidth)
                    {
                        case IntWidth.Byte:
                            cache.ByteValue = (byte)(baseline.ByteValue + deltaS);
                            break;
                        case IntWidth.Int64:
                            cache.LongValue = baseline.LongValue + deltaS;
                            break;
                        default:
                            cache.IntValue = baseline.IntValue + deltaS;
                            break;
                    }
                    break;

                case SerialVariantType.Vector2:
                    float dx2 = NetReader.ReadHalfFloat(buffer);
                    float dy2 = NetReader.ReadHalfFloat(buffer);
                    cache.Vec2Value = new Vector2(baseline.Vec2Value.X + dx2, baseline.Vec2Value.Y + dy2);
                    break;

                case SerialVariantType.Vector3:
                    float dx3 = NetReader.ReadHalfFloat(buffer);
                    float dy3 = NetReader.ReadHalfFloat(buffer);
                    float dz3 = NetReader.ReadHalfFloat(buffer);
                    cache.Vec3Value = new Vector3(
                        baseline.Vec3Value.X + dx3,
                        baseline.Vec3Value.Y + dy3,
                        baseline.Vec3Value.Z + dz3);
                    break;

                default:
                    // Fallback to absolute for unsupported small delta types
                    Debugger.Instance.Log(Debugger.DebugLevel.WARN, $"Small delta not supported for type {type}, reading absolute");
                    ReadAbsoluteValue(buffer, type, subtype, ref cache);
                    break;
            }
        }

        /// <summary>
        /// Reads a full delta and applies it to the baseline value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadFullDelta(NetBuffer buffer, SerialVariantType type, IntWidth intWidth, string subtype, ref PropertyCache baseline, ref PropertyCache cache)
        {
            switch (type)
            {
                case SerialVariantType.Float:
                    float deltaF = NetReader.ReadFloat(buffer);
                    cache.FloatValue = baseline.FloatValue + deltaF;
                    break;

                case SerialVariantType.Int:
                    // Full delta uses the same size as the property type for larger deltas.
                    // Must mirror WriteDelta's Int case exactly - see IntWidth.
                    cache.LongValue = 0; // Clear first
                    switch (intWidth)
                    {
                        case IntWidth.Byte:
                            // Byte types use Int16 for full delta (more range than byte)
                            short deltaB = NetReader.ReadInt16(buffer);
                            cache.ByteValue = (byte)(baseline.ByteValue + deltaB);
                            break;
                        case IntWidth.Int16:
                        case IntWidth.UInt16:
                            short deltaS = NetReader.ReadInt16(buffer);
                            cache.IntValue = baseline.IntValue + deltaS;
                            break;
                        case IntWidth.Int32:
                        case IntWidth.UInt32:
                            int deltaI = NetReader.ReadInt32(buffer);
                            cache.IntValue = baseline.IntValue + deltaI;
                            break;
                        default:
                            // Int64: long, ulong, or an unrecognised subtype
                            long deltaL = NetReader.ReadInt64(buffer);
                            cache.LongValue = baseline.LongValue + deltaL;
                            break;
                    }
                    break;

                case SerialVariantType.Vector2:
                    Vector2 deltaV2 = NetReader.ReadVector2(buffer);
                    cache.Vec2Value = baseline.Vec2Value + deltaV2;
                    break;

                case SerialVariantType.Vector3:
                    Vector3 deltaV3 = NetReader.ReadVector3(buffer);
                    cache.Vec3Value = baseline.Vec3Value + deltaV3;
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
        private void WriteCustomTypeFromCache(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, int propIndex, ref PropertyCache cache)
        {
            var serializer = Protocol.GetSerializer(_propClassIndex[propIndex]);
            if (serializer == null)
            {
                var missing = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"No serializer found for {missing.NodePath}.{missing.Name}");
                return;
            }

            // Reuse pooled buffer instead of allocating new one each time
            _customTypeBuffer ??= new NetBuffer();
            _customTypeBuffer.Reset();
            // Note: For object types, the serializer returns bool (true if wrote data)
            // But here we're in the absolute value path, so we always expect data to be written
            serializer(currentWorld, peer, ref cache, _customTypeBuffer, _propChunkBudget[propIndex]);
            NetWriter.WriteBytes(buffer, _customTypeBuffer.WrittenSpan);
        }

        public void Begin()
        {
            // Snapshot the dirty mask and clear the original
            processingDirtyMask = network.DirtyMask;
            network.ClearDirtyMask();

            // Track which properties have ever been set (for initial sync to new peers).
            // Bounded by _propertyCount, not the mask width: indices at or above it have no
            // metadata and would fault the pre-cached arrays if a stray bit ever set one.
            for (int i = 0; i < _propertyCount; i++)
            {
                if ((processingDirtyMask & (1L << i)) != 0)
                {
                    nonDefaultProperties.Add(i);
                }
            }

            // Capture this tick's property values for delta baselines. Runs once per node
            // per tick, before any per-peer Export. All peers' deltas for this tick are
            // computed against entries of this ring at their respective acked ticks.
            if (_propertyCount > 0 && NetRunner.Instance.IsServer && network.CurrentWorld != null)
            {
                _tickValueRing ??= new TickSnapshot[SNAPSHOT_RING_SIZE];
                Tick tick = network.CurrentWorld.CurrentTick;
                ref var entry = ref _tickValueRing[tick % SNAPSHOT_RING_SIZE];
                entry.Values ??= new PropertyCache[_propertyCount];
                Array.Copy(network.CachedProperties, entry.Values, _propertyCount);
                entry.Tick = tick;
            }
        }

        /// <summary>
        /// Applies property values that arrived while RawNode was not yet ready.
        /// Invoked via RawNode.Ready and defensively from Import. Must run before any
        /// newer payload is applied so stashed (older) values cannot overwrite it.
        /// </summary>
        private void FlushPendingChanges()
        {
            if (cachedPropertyChanges.Count == 0)
                return;

            Tick tick = network.CurrentWorld != null ? network.CurrentWorld.CurrentTick : 0;
            foreach (var propIndex in cachedPropertyChanges.Keys)
            {
                var prop = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                ref var cachedValue = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(cachedPropertyChanges, propIndex);
                ImportProperty(prop, tick, ref cachedValue);
            }
            cachedPropertyChanges.Clear();
        }

        public void Import(WorldRunner currentWorld, NetBuffer buffer, out NetworkController nodeOut)
        {
            nodeOut = network;

            var data = Deserialize(buffer, currentWorld.CurrentTick);

            // Cache IsNodeReady() once before the loop to avoid repeated Godot calls
            bool isReady = network.RawNode.IsNodeReady();

            // If the node became ready between packets, apply any stashed pre-ready
            // values before this payload so the newer values win.
            if (isReady)
            {
                FlushPendingChanges();
            }

            // Begin snapshot for this tick (client-side only, for interpolation)
            if (NetRunner.Instance.IsClient && network.IsWorldReady)
            {
                network.BeginSnapshotForTick(currentWorld.CurrentTick);
            }

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

        /// <summary>
        /// Tests a property's build-time interest declaration against a peer's CURRENT layers.
        /// <paramref name="peerInterestLayers"/> is read fresh from network.InterestLayers on
        /// every export, so gaining or losing interest takes effect immediately; only the
        /// property-side declaration (constant per scene) comes from the pre-cached arrays.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool PeerHasInterestInProperty(int propIndex, long peerInterestLayers)
        {
            if ((uint)propIndex >= (uint)_propertyCount) return false;
            long interestRequired = _propInterestRequired[propIndex];
            bool hasAnyInterest = (_propInterestMask[propIndex] & peerInterestLayers) != 0;
            bool hasAllRequired = (interestRequired & peerInterestLayers) == interestRequired;
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
            // Only export if spawn data has been sent AND not despawning/despawned
            // NotSpawned: SpawnSerializer hasn't written spawn data yet
            // Despawning/Despawned: Node is being removed, no point sending property updates
            var spawnState = currentWorld.GetClientSpawnState(network.NetId, peer);
            if (spawnState == WorldRunner.ClientSpawnState.NotSpawned ||
                spawnState == WorldRunner.ClientSpawnState.Despawning ||
                spawnState == WorldRunner.ClientSpawnState.Despawned)
            {
                return;
            }

            // For nested scenes, don't export until parent spawn is at least being sent
            if (network.NetParent != null)
            {
                var parentSpawnState = currentWorld.GetClientSpawnState(network.NetParent.NetId, peer);
                if (parentSpawnState == WorldRunner.ClientSpawnState.NotSpawned)
                {
                    return;
                }
            }

            var peerId = NetRunner.Instance.GetPeerId(peer);
            int byteCount = _byteCount;

            // Snapshot AND CLEAR per-peer dirty mask for this peer
            long perPeerDirty = 0;
            if (network.PerPeerDirtyMask != null &&
                network.PerPeerDirtyMask.TryGetValue(peerId, out perPeerDirty))
            {
                network.PerPeerDirtyMask[peerId] = 0; // Clear after snapshot
            }

            Array.Clear(_propertiesUpdated, 0, byteCount);

            if (!peerInitialPropSync.TryGetValue(peerId, out var initialSync))
            {
                initialSync = new byte[byteCount];
                peerInitialPropSync[peerId] = initialSync;
            }

            // Zero-alloc dictionary access via ref for delta state
            ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(_peerStates, peerId, out bool isNew);
            if (isNew || !state.IsInitialized)
            {
                state = CreateOrGetPooledState();
            }

            // Filter against interest layers first
            if (!TryGetInterestLayers(peerId, out var peerInterestLayers))
            {
                return;
            }

            // Build dirty mask for PRIMITIVE properties only from processingDirtyMask
            // Object properties (INetSerializable) are handled separately - they self-filter
            // Per-peer properties use per-peer dirty mask instead of global
            for (int propIndex = 0; propIndex < 64 && propIndex < _propertyCount; propIndex++)
            {
                // Skip object properties - they will be called unconditionally and self-filter
                if (_propIsObject[propIndex]) continue;

                // For per-peer properties, use per-peer dirty mask
                // For broadcast properties, use global processingDirtyMask
                bool isDirty;
                if (_propIsPerPeer[propIndex])
                {
                    isDirty = (perPeerDirty & (1L << propIndex)) != 0;
                }
                else
                {
                    isDirty = (processingDirtyMask & (1L << propIndex)) != 0;
                }

                if (isDirty)
                {
                    _propertiesUpdated[propIndex / BitConstants.BitsInByte] |= (byte)(1 << (propIndex % BitConstants.BitsInByte));
                }
            }

            // Include non-default PRIMITIVE properties that haven't been synced yet
            foreach (var propIndex in nonDefaultProperties)
            {
                // Skip object properties
                if (_propIsObject[propIndex]) continue;

                var byteIndex = propIndex / BitConstants.BitsInByte;
                var propSlot = (byte)(1 << (propIndex % BitConstants.BitsInByte));
                if ((initialSync[byteIndex] & propSlot) == 0)
                {
                    _propertiesUpdated[byteIndex] |= propSlot;
                }
            }

            // Include PRIMITIVE properties that were sent but not yet acknowledged (for re-sending)
            for (var i = 0; i < state.PendingDirtyMask.Length && i < _propertiesUpdated.Length; i++)
            {
                // Only include primitive properties from pending mask
                var pendingByte = state.PendingDirtyMask[i];
                for (int j = 0; j < 8; j++)
                {
                    int propIndex = i * 8 + j;
                    if (propIndex >= _propertyCount) break;
                    if (_propIsObject[propIndex]) continue; // Skip objects
                    if ((pendingByte & (1 << j)) != 0)
                    {
                        _propertiesUpdated[i] |= (byte)(1 << j);
                    }
                }
            }

            // Apply interest filter to primitive properties
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

            // ============================================================
            // BASELINE SELECTION (snapshot-delta)
            // ============================================================
            // Delta against the server's value snapshot at the peer's latest acked
            // send-tick. The client applies deltas against its own recorded state at
            // that same tick, so in-flight packets can never compound.
            Tick currentTick = currentWorld.CurrentTick;
            int baselineAge = 0;
            PropertyCache[] baselineValues = null;
            if (state.LatestAckedTick >= 0 && _tickValueRing != null)
            {
                int age = currentTick - state.LatestAckedTick;
                if (age >= 1 && age <= MAX_DELTA_AGE)
                {
                    ref var baseEntry = ref _tickValueRing[state.LatestAckedTick % SNAPSHOT_RING_SIZE];
                    if (baseEntry.Values != null && baseEntry.Tick == state.LatestAckedTick)
                    {
                        baselineAge = age;
                        baselineValues = baseEntry.Values;
                    }
                }
            }

            // ============================================================
            // RESERVE-AND-BACKFILL PATTERN
            // ============================================================

            // Reserve space for the mask (we'll backfill it after writing properties)
            int maskStartPos = buffer.WritePosition;
            for (var i = 0; i < byteCount; i++)
            {
                NetWriter.WriteByte(buffer, 0); // Placeholder
            }

            // Baseline age header: 0 = every property in this payload is absolute
            NetWriter.WriteByte(buffer, (byte)baselineAge);

            // Track which properties actually got written (for combined mask)
            // Start with primitive mask
            byte[] actualMask = new byte[byteCount];
            Array.Copy(_propertiesUpdated, actualMask, byteCount);

            // Write PRIMITIVE properties (only dirty ones)
            for (var i = 0; i < byteCount; i++)
            {
                var propSegment = _propertiesUpdated[i];
                if (propSegment == 0) continue;

                for (var j = 0; j < BitConstants.BitsInByte; j++)
                {
                    if ((propSegment & (byte)(1 << j)) == 0) continue;

                    var propIndex = i * BitConstants.BitsInByte + j;
                    // Skip object properties - handled in next loop
                    if (_propIsObject[propIndex]) continue;

                    int propStartPos = buffer.WritePosition;
                    try
                    {
                        // Get current value - for per-peer properties, look up in per-peer storage
                        PropertyCache current;
                        if (_propIsPerPeer[propIndex] &&
                            network.PerPeerValues != null &&
                            network.PerPeerValues[propIndex] != null &&
                            network.PerPeerValues[propIndex].TryGetValue(peerId, out current))
                        {
                            // Use per-peer value
                        }
                        else
                        {
                            // Fallback: default value from CachedProperties
                            current = network.CachedProperties[propIndex];
                        }

                        bool hasAcked = (state.AckedMask[i] & (1 << j)) != 0;

                        // Delta requires: a resolvable baseline, a confirmed-received prop,
                        // and not a per-peer prop (their values have no shared snapshot).
                        // DeltaChain forces a periodic absolute refresh to bound drift.
                        bool useDelta = baselineValues != null
                            && hasAcked
                            && !_propIsPerPeer[propIndex]
                            && _propSupportsDelta[propIndex]
                            && state.DeltaChain[propIndex] < REFRESH_CHAIN;

                        if (useDelta)
                        {
                            WriteDelta(buffer, propIndex, ref current, ref baselineValues[propIndex]);
                            state.DeltaChain[propIndex]++;
                        }
                        else
                        {
                            WriteAbsolute(currentWorld, peer, buffer, propIndex, ref current);
                            state.DeltaChain[propIndex] = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        var prop = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                        Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                            $"Error serializing property {prop.NodePath}.{prop.Name}: {ex.InnerException?.Message ?? ex.Message}");
                        // Clear the bit AND rewind - stray partial bytes would desync the
                        // whole stream for every property and node after this one
                        actualMask[i] &= (byte)~(1 << j);
                        buffer.WritePosition = propStartPos;
                    }
                }
            }

            // Write OBJECT properties (INetSerializable) - always call, they self-filter
            // These return true if they wrote data, false if nothing to send
            for (int propIndex = 0; propIndex < _propertyCount; propIndex++)
            {
                if (!_propIsObject[propIndex]) continue;

                // Check interest for this property
                if (!PeerHasInterestInProperty(propIndex, peerInterestLayers)) continue;

                var classIndex = _propClassIndex[propIndex];
                if (classIndex < 0) continue;

                var serializer = Protocol.GetSerializer(classIndex);
                if (serializer == null) continue;

                ref var cache = ref network.CachedProperties[propIndex];

                // Remember position in case we need to rewind
                int startPos = buffer.WritePosition;

                try
                {
                    // Object serializers return true if they wrote data
                    bool wroteData = serializer(currentWorld, peer, ref cache, buffer, _propChunkBudget[propIndex]);

                    if (wroteData)
                    {
                        // Set the bit in the actual mask
                        int byteIdx = propIndex / 8;
                        int bitIdx = propIndex % 8;
                        actualMask[byteIdx] |= (byte)(1 << bitIdx);
                    }
                    else
                    {
                        // Rewind buffer - nothing was written
                        buffer.WritePosition = startPos;
                    }
                }
                catch (Exception ex)
                {
                    var prop = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                        $"Error serializing object property {prop.NodePath}.{prop.Name}: {ex.InnerException?.Message ?? ex.Message}");
                    // Rewind on error
                    buffer.WritePosition = startPos;
                }
            }

            // Check if anything was actually written
            bool hasAnyData = false;
            for (var i = 0; i < byteCount; i++)
            {
                if (actualMask[i] != 0)
                {
                    hasAnyData = true;
                    break;
                }
            }

            if (!hasAnyData)
            {
                // Nothing to send - rewind buffer to before mask
                buffer.WritePosition = maskStartPos;
                return;
            }

            // Update tracking state. PendingDirtyMask and SentHistory only track PRIMITIVE
            // properties - object properties (INetSerializable) manage their own per-peer
            // pending/resend state and are acked through their own tick-gated callbacks.
            long primitiveSentMask = 0;
            for (var byteIdx = 0; byteIdx < byteCount; byteIdx++)
            {
                var b = actualMask[byteIdx];
                if (b == 0) continue;
                initialSync[byteIdx] |= b;

                for (int bit = 0; bit < 8; bit++)
                {
                    if ((b & (1 << bit)) == 0) continue;
                    int propIdx = byteIdx * 8 + bit;
                    if (propIdx >= _propertyCount || _propIsObject[propIdx]) continue;
                    state.PendingDirtyMask[byteIdx] |= (byte)(1 << bit);
                    if (propIdx < 64)
                    {
                        primitiveSentMask |= 1L << propIdx;
                    }
                }
            }

            // Record what was sent at this tick so a future ack can be matched to exactly
            // the values the peer received (the heart of tick-correlated acknowledgment)
            ref var sentRecord = ref state.SentHistory[currentTick % SNAPSHOT_RING_SIZE];
            sentRecord.Tick = currentTick;
            sentRecord.SentMask = primitiveSentMask;

            // BACKFILL: Go back and write the actual mask
            // We need to overwrite the placeholder bytes we wrote earlier
            // NetWriter writes at WritePosition, so we save it, set to maskStartPos, write, then restore
            int endPos = buffer.WritePosition;
            buffer.WritePosition = maskStartPos;
            for (var i = 0; i < byteCount; i++)
            {
                NetWriter.WriteByte(buffer, actualMask[i]);
            }
            buffer.WritePosition = endPos;

            // Debugger.Instance.Log(Debugger.DebugLevel.VERBOSE, $"[Props.Export] NetId={network.NetId} mask=[{string.Join(",", actualMask.Select(b => $"0x{b:X2}"))}] total={endPos - maskStartPos}");
        }

        /// <summary>
        /// Writes an absolute property value with its encoding flag byte.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteAbsolute(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, int propIndex, ref PropertyCache current)
        {
            // Quaternion: use smallest-three compression
            if (_propTypes[propIndex] == SerialVariantType.Quaternion)
            {
                NetWriter.WriteByte(buffer, (byte)(DeltaEncodingFlags.Absolute | DeltaEncodingFlags.QuatCompressed));
                NetWriter.WriteQuatSmallestThree(buffer, current.QuatValue);
            }
            else
            {
                NetWriter.WriteByte(buffer, (byte)DeltaEncodingFlags.Absolute);
                WriteAbsoluteValue(currentWorld, peer, buffer, propIndex, ref current);
            }
        }

        /// <summary>
        /// Writes a property value as a delta against the snapshot value at the peer's
        /// acked baseline tick. Caller guarantees the type supports delta encoding.
        /// </summary>
        private void WriteDelta(
            NetBuffer buffer,
            int propIndex,
            ref PropertyCache current,
            ref PropertyCache baseline)
        {
            var type = _propTypes[propIndex];

            switch (type)
            {
                case SerialVariantType.Float:
                    float deltaF = current.FloatValue - baseline.FloatValue;
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
                    // Read current and baseline from the field this width uses (see IntWidth).
                    // Must mirror ReadSmallDelta/ReadFullDelta's Int cases exactly.
                    var intWidth = _propIntWidth[propIndex];
                    long currentVal, baselineVal;

                    switch (intWidth)
                    {
                        case IntWidth.Byte:
                            currentVal = current.ByteValue;
                            baselineVal = baseline.ByteValue;
                            break;
                        case IntWidth.Int64:
                            currentVal = current.LongValue;
                            baselineVal = baseline.LongValue;
                            break;
                        default:
                            currentVal = current.IntValue;
                            baselineVal = baseline.IntValue;
                            break;
                    }

                    long deltaL = currentVal - baselineVal;
                    // Use small encoding for deltas that fit in short range
                    if (deltaL >= short.MinValue && deltaL <= short.MaxValue)
                    {
                        NetWriter.WriteByte(buffer, (byte)DeltaEncodingFlags.DeltaSmall);
                        NetWriter.WriteInt16(buffer, (short)deltaL);
                    }
                    else
                    {
                        // Full delta - write appropriate size based on width
                        NetWriter.WriteByte(buffer, (byte)DeltaEncodingFlags.DeltaFull);
                        switch (intWidth)
                        {
                            case IntWidth.Byte:
                            case IntWidth.Int16:
                            case IntWidth.UInt16:
                                NetWriter.WriteInt16(buffer, (short)deltaL);
                                break;
                            case IntWidth.Int32:
                            case IntWidth.UInt32:
                                NetWriter.WriteInt32(buffer, (int)deltaL);
                                break;
                            default:
                                NetWriter.WriteInt64(buffer, deltaL);
                                break;
                        }
                    }
                    break;

                case SerialVariantType.Vector2:
                    Vector2 deltaV2 = current.Vec2Value - baseline.Vec2Value;
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
                    Vector3 deltaV3 = current.Vec3Value - baseline.Vec3Value;
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
                    // Caller gates on _propSupportsDelta, so this is unreachable; guard anyway
                    throw new NotSupportedException($"WriteDelta: type {type} does not support delta encoding");
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
                    // Sized integer types (enums, byte, short, int, long). Must mirror
                    // NetReader.ReadAbsoluteValue's Int case exactly - a width mismatch
                    // misaligns every value after this one in the packet.
                    switch (_propIntWidth[propIndex])
                    {
                        case IntWidth.Byte:
                            NetWriter.WriteByte(buffer, cache.ByteValue);
                            break;
                        case IntWidth.Int16:
                            NetWriter.WriteInt16(buffer, (short)cache.IntValue);
                            break;
                        case IntWidth.UInt16:
                            NetWriter.WriteUInt16(buffer, (ushort)cache.IntValue);
                            break;
                        case IntWidth.Int32:
                            NetWriter.WriteInt32(buffer, cache.IntValue);
                            break;
                        case IntWidth.UInt32:
                            NetWriter.WriteUInt32(buffer, (uint)cache.IntValue);
                            break;
                        default:
                            // Int64: long, ulong, or an unrecognised subtype
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
                    WriteCustomTypeFromCache(currentWorld, peer, buffer, propIndex, ref cache);
                    break;
                default:
                    var nilProp = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Unsupported cache type: {cache.Type} for property '{nilProp.NodePath}.{nilProp.Name}' (scene '{_cachedSceneFilePath}'). An uninitialized/absolute-written property here writes no value but its presence bit is set, desyncing the whole tick stream.");
                    break;
            }
        }

        public void Cleanup()
        {
            // NOTE: This is called every tick after ExportState(), NOT when the object is destroyed.
            // Do not clear per-peer caches here - that would break state synchronization!
            // Use CleanupPeer() for per-peer cleanup on disconnect instead.

            // End-of-tick hook for object properties with global dirty state (e.g. NetArray):
            // every peer's Export has now absorbed the global dirty bits into per-peer
            // pending state, so the object can safely clear its global set.
            if (!_hasObjectProps || !NetRunner.Instance.IsServer) return;
            for (int i = 0; i < _propertyCount; i++)
            {
                if (!_propIsObject[i]) continue;
                if (network.CachedProperties[i].RefValue is INetExportAware exportAware)
                {
                    exportAware.OnExportComplete();
                }
            }
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

            // Call OnPeerDisconnected on all object properties
            for (int i = 0; i < _propertyCount; i++)
            {
                if (!_propIsObject[i]) continue;

                var classIndex = _propClassIndex[i];
                if (classIndex < 0) continue;

                var onDisconnected = Protocol.GetOnPeerDisconnected(classIndex);
                if (onDisconnected == null) continue;

                ref var cache = ref network.CachedProperties[i];
                if (cache.RefValue != null)
                {
                    onDisconnected(cache.RefValue, peerId);
                }
            }
        }

        public bool Acknowledge(WorldRunner currentWorld, NetPeer peer, Tick latestAck)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);

            // Call OnPeerAcknowledge on all object properties (they gate on the tick themselves)
            for (int i = 0; i < _propertyCount; i++)
            {
                if (!_propIsObject[i]) continue;

                var classIndex = _propClassIndex[i];
                if (classIndex < 0) continue;

                var onAck = Protocol.GetOnPeerAcknowledge(classIndex);
                if (onAck == null) continue;

                ref var cache = ref network.CachedProperties[i];
                if (cache.RefValue != null)
                {
                    onAck(cache.RefValue, peerId, latestAck);
                }
            }

            // Zero-alloc ref access
            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(_peerStates, peerId);
            if (Unsafe.IsNullRef(ref state) || !state.IsInitialized)
            {
                // No primitive state yet; object props were still notified above
                return _hasObjectProps;
            }

            // An ack for tick N proves the peer received the packet exported at N, which
            // contained the tick-N value of every then-pending property. It proves nothing
            // about sends at later ticks, so only the tick-N record is committed.
            ref var ackedRecord = ref state.SentHistory[latestAck % SNAPSHOT_RING_SIZE];
            if (ackedRecord.Tick == latestAck)
            {
                long ackedSent = ackedRecord.SentMask;

                if (ackedSent != 0)
                {
                    // Mark these props as confirmed-received (enables delta encoding)
                    for (int i = 0; i < state.AckedMask.Length; i++)
                    {
                        int shift = i * 8;
                        if (shift >= 64) break;
                        state.AckedMask[i] |= (byte)((ackedSent >> shift) & 0xFF);
                    }

                    // Stop resending only props NOT re-sent after the acked tick - a prop
                    // sent again later carries a newer value the client may not have yet
                    long laterSent = 0;
                    for (int i = 0; i < state.SentHistory.Length; i++)
                    {
                        if (state.SentHistory[i].Tick > latestAck)
                        {
                            laterSent |= state.SentHistory[i].SentMask;
                        }
                    }

                    long clearMask = ackedSent & ~laterSent;
                    for (int i = 0; i < state.PendingDirtyMask.Length; i++)
                    {
                        int shift = i * 8;
                        if (shift >= 64) break;
                        state.PendingDirtyMask[i] &= (byte)~((clearMask >> shift) & 0xFF);
                    }
                }

                // The baseline must be a tick at which THIS node's data was received, so
                // the client is guaranteed to have a matching applied-state ring entry.
                // Only advance on ticks with a SentHistory record (node exported that tick).
                if (latestAck > state.LatestAckedTick)
                {
                    state.LatestAckedTick = latestAck;
                }
            }

            // Still pending if any primitive props await an ack (object props conservatively
            // keep the node in the pending set; re-adding on next export is cheap)
            if (_hasObjectProps) return true;
            for (int i = 0; i < state.PendingDirtyMask.Length; i++)
            {
                if (state.PendingDirtyMask[i] != 0) return true;
            }
            return false;
        }

    }
}
