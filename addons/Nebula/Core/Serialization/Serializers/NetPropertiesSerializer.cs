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
        // Two bits on the wire (NetPropertiesSerializer.EncodingBits). A quaternion has no
        // flag of its own: the reader knows the type, and quaternion absolutes are always
        // smallest-three packed.
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
        /// <summary>
        /// Decoded values from a single received payload.
        ///
        /// The decoded set lives in the serializer's <see cref="_decodedMask"/> /
        /// <see cref="_decodedValues"/> scratch rather than in a per-packet Dictionary, so
        /// importing a packet allocates nothing. Consumers iterate set bits of the mask in
        /// property-index order and read the value out of the array; there is no hashing.
        /// </summary>
        private readonly struct Data
        {
            /// <summary>Which property indices this payload actually decoded a value for.</summary>
            public readonly byte[] DecodedMask;
            /// <summary>Values by property index; only indices set in DecodedMask are meaningful.</summary>
            public readonly PropertyCache[] Values;

            public Data(byte[] decodedMask, PropertyCache[] values)
            {
                DecodedMask = decodedMask;
                Values = values;
            }
        }

        /// <summary>
        /// A record of which primitive properties were included in the export at a given tick.
        /// Ring-indexed by tick % SNAPSHOT_RING_SIZE; Tick disambiguates stale slots.
        /// </summary>
        private struct SentRecord
        {
            public Tick Tick;
            public long SentMask;
            /// <summary>
            /// The subset of <see cref="SentMask"/> that carried a FRESHLY CHANGED value
            /// (from the tick's dirty snapshot), as opposed to a pending resend or a settle
            /// absolute of a value the peer was already sent. Only these invalidate an
            /// earlier ack: a resend of the same value after the acked tick is not "a newer
            /// value the client may not have yet". Counting resends made the ack-clear
            /// unreachable whenever acks lagged a tick or more - every pending bit was resent
            /// at T+1 before the ack for T landed, so it was always "sent later", so it was
            /// never cleared, so it was resent again - the whole section, every tick,
            /// forever, for any peer whose RTT exceeds one tick.
            /// </summary>
            public long DirtySentMask;
            /// <summary>
            /// The section's presence mask as it went on the WIRE (primitives, node refs AND
            /// object properties). The mask-reuse bit compares against this, never against
            /// <see cref="SentMask"/>, which omits object properties.
            /// </summary>
            public long WireMask;
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
            /// <summary>Client: the wire mask of the section applied at this tick (mask reuse reference).</summary>
            public long Mask;
        }

        /// <summary>
        /// Per-peer property state for snapshot-delta encoding.
        ///
        /// Correctness invariant: pending (unacked) dirty properties are re-included in
        /// EVERY export until acked. So when a peer acks tick N, the tick-N packet
        /// contained the tick-N value of every then-unacked property, and every other
        /// acked property was unchanged at N — meaning the client's applied state at any
        /// acked send-tick equals the server's snapshot at that tick, up to delta rounding.
        /// Deltas are therefore computed against the snapshot at the latest acked send-tick,
        /// and the client applies them against its own recorded state at that tick.
        ///
        /// Delta rounding is tracked in LossyMask: a small delta encodes half-precision
        /// components, so the client's reconstructed value can drift a few micro-units from
        /// the server's. Mid-stream that is invisible, but the LAST send before a property
        /// goes quiet must not be lossy or the client is stranded off the true value forever
        /// (e.g. an exact server-side zero reading as ~6e-6). Export therefore schedules one
        /// forced absolute ("settle absolute") for any lossy-flagged property that stopped
        /// changing. Quaternions are exempt: their absolute encoding (smallest-three) is
        /// itself lossy, so exactness on the wire was never available for them.
        /// </summary>
        private struct PeerPropertyState
        {
            /// <summary>
            /// This node owes THIS peer nothing: the last full Export ended with an empty
            /// section and no latent obligations (no pending resends, no lossy settles, no
            /// per-peer dirt, no exception/deferral mid-run, and the peer's interest touches
            /// no poll-required object prop). While true - and while the node has no fresh
            /// broadcast dirt and no per-peer dirt - the whole Export prologue is skipped.
            ///
            /// DISCIPLINE (the invariant that makes this safe): cleared at the top of EVERY
            /// full run, re-set only at the empty-section end under the raw-mask conditions.
            /// "Set on None" alone is unsound - a bypassed dirty tick banks pending bits and
            /// can return to a stale true flag, stranding the resend-until-acked machinery
            /// after one lost packet. Also cleared by ClearPeerState (pool reuse, baseline
            /// reset) and by any interest change.
            /// </summary>
            public bool Settled;
            public byte[] AckedMask;               // Bit mask: has an ack confirmed the peer received this property?
            public byte[] PendingDirtyMask;        // Properties sent but not yet acked (for re-sending)
            public SentRecord[] SentHistory;       // Which props were sent at each recent tick
            public Tick LatestAckedTick;           // Latest acked tick at which this node sent data (-1 = none)
            public byte[] DeltaChain;              // Consecutive delta sends per prop; forces periodic absolute refresh
            public byte[] LossyMask;               // Peer's applied value may be inexact (a lossy delta landed since the last absolute)
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

        // Cached StringName -> NodePath conversions. Kept separate from _nodePathCache so
        // that re-resolving a stale node does not re-allocate the path.
        private Dictionary<StringName, NodePath> _nodePathConversionCache = new();

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
        /// <summary>
        /// Object properties that hold a NODE REFERENCE rather than in-place-mutated content.
        /// These are the only object properties whose dirty bit is trustworthy — see
        /// <see cref="Protocol.IsNodeReferenceClass"/> — so they are the only ones gated on it.
        /// </summary>
        private readonly bool[] _propIsNodeRef;

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

        // ─── Wire quantization ([NetProperty(Quantize = step)]) ───
        //
        // Resolved once from the protocol table like IntWidth, and read by both the writer
        // and the reader so the two can never disagree on a property's encoding. See
        // QuantizedCodec for the wire forms and the exactness contract.
        /// <summary>Grid step per property; 0 = not quantized (float/half encoding).</summary>
        private readonly float[] _propQuantStep;
        /// <summary>Smallest-three bits per component for quantized quaternions; 0 otherwise.</summary>
        private readonly byte[] _propQuantBits;
        /// <summary>Vector3 sent as an octahedral unit direction.</summary>
        private readonly bool[] _propUnitVector;
        /// <summary>Integer code count of a quantized grid property (0 for quaternions).</summary>
        private readonly byte[] _propQuantComponents;
        /// <summary>Bit per quantized property, for the dirty filter's fast skip.</summary>
        private long _quantizedMask;
        /// <summary>
        /// Server-side dead-band state: the last grid codes that PASSED the dirty filter, per
        /// property (MaxComponents slots each; a quaternion uses one for its packed word).
        /// Compared against the last passed value, never the previous tick, so a slowly
        /// creeping value accumulates until it crosses a cell instead of being filtered
        /// forever. Unset (bit clear in <see cref="_gridSeededMask"/>) means "pass", so the
        /// first dirty tick after a spawn or teleport always ships.
        /// </summary>
        private int[] _lastGridCodes;
        private long _gridSeededMask;

        /// <summary>Pre-cached size in bytes of the property presence mask.</summary>
        private readonly int _byteCount;

        /// <summary>
        /// Bytes reserved at the head of a section for the presence mask before its contents
        /// are known: <see cref="PresenceMask.WorstCaseBits"/> of <see cref="_byteCount"/>.
        /// Wide masks ship two-level (header + nonzero bytes), so the reservation is the
        /// worst case and the section is compacted once the mask is final. Every budget check
        /// measures against this width, never the compact one - see the backfill in ExportCore.
        /// </summary>
        private readonly int _worstCaseHeaderBits;
        /// <summary>
        /// Properties whose value is written with a BYTE codec (object properties, INetValue
        /// objects, strings, packed arrays). Those codecs auto-align to the body scratch, which
        /// is at phase 0; for their bytes to land byte-aligned in the final stream the body
        /// must start on a byte boundary there too. So a section whose wire mask touches any
        /// of these pads once between header and body - both sides decide that from the mask,
        /// and a section of bit-coded values (the steady-state case) pays nothing.
        /// </summary>
        private readonly long _byteCodedMask;
        private const int BodyAlignPadBits = BitConstants.BitsInByte - 1;

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
        private static readonly bool TraceWire = System.Environment.GetEnvironmentVariable("NEBULA_TRACE_WIRE") != null;

        /// <summary>
        /// After this many consecutive delta sends of a property, force one absolute send.
        /// Bounds accumulated half-float quantization drift on the client.
        /// </summary>
        private const int REFRESH_CHAIN = 30;

        /// <summary>
        /// The baseline-age header byte written right after the presence mask
        /// (0 = every property in the payload is absolute).
        /// </summary>
        private const int MaskModeBits = 1;
        /// <summary>Baseline age on the wire; MAX_DELTA_AGE = 30 fits.</summary>
        private const int AgeBits = 5;
        /// <summary>Per-primitive encoding selector (DeltaEncodingFlags).</summary>
        private const int EncodingBits = 2;

        /// <summary>
        /// The smallest possible property write: a DeltaEncodingFlags byte plus a
        /// one-byte value (e.g. bool). A section budget below
        /// [<see cref="_worstCaseHeaderBits"/> + <see cref="AgeBits"/> + this] cannot
        /// ship anything.
        /// </summary>
        private const int MinPropertyWriteBits = EncodingBits + 1;

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
                _propIsNodeRef = Array.Empty<bool>();
                _propTypes = Array.Empty<SerialVariantType>();
                _propIsObject = Array.Empty<bool>();
                _propClassIndex = Array.Empty<int>();
                _propIsPerPeer = Array.Empty<bool>();
                _propInterestMask = Array.Empty<long>();
                _propInterestRequired = Array.Empty<long>();
                _propIntWidth = Array.Empty<IntWidth>();
                _propChunkBudget = Array.Empty<int>();
                _propQuantStep = Array.Empty<float>();
                _propQuantBits = Array.Empty<byte>();
                _propUnitVector = Array.Empty<bool>();
                _propQuantComponents = Array.Empty<byte>();
                _propertiesUpdated = Array.Empty<byte>();
                _actualMask = Array.Empty<byte>();
                _dirtyOnlyMask = Array.Empty<byte>();
                _leftoverMask = Array.Empty<byte>();
                _decodedMask = Array.Empty<byte>();
                _decodedValues = Array.Empty<PropertyCache>();
                _incomingMask = Array.Empty<byte>();
                _initSyncEligibleBytes = Array.Empty<byte>();
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

            // Before the metadata loop, which sizes byte-indexed masks off it - assigning
            // it after the loop is exactly the ctor-order bug that once killed EVERY net
            // scene's props serializer (new byte[0], then index into it) and shipped a
            // server that spawned nothing while the unit suite stayed green, because the
            // suite only exercises the Protocol-free ctor.
            _byteCount = (_propertyCount + BitConstants.BitsInByte - 1) / BitConstants.BitsInByte;
            _worstCaseHeaderBits = MaskModeBits + AgeBits + PresenceMask.WorstCaseBits(_byteCount, _propertyCount) + BodyAlignPadBits;

            _propSupportsDelta = new bool[_propertyCount];
            _propIsNodeRef = new bool[_propertyCount];
            _propTypes = new SerialVariantType[_propertyCount];
            _propIsObject = new bool[_propertyCount];
            _propClassIndex = new int[_propertyCount];
            _propIsPerPeer = new bool[_propertyCount];
            _propInterestMask = new long[_propertyCount];
            _propInterestRequired = new long[_propertyCount];
            _propIntWidth = new IntWidth[_propertyCount];
            _propChunkBudget = new int[_propertyCount];
            _propQuantStep = new float[_propertyCount];
            _propQuantBits = new byte[_propertyCount];
            _propUnitVector = new bool[_propertyCount];
            _propQuantComponents = new byte[_propertyCount];

            for (int i = 0; i < _propertyCount; i++)
            {
                var prop = Protocol.UnpackProperty(_cachedSceneFilePath, i);
                ResolveQuantization(i, prop.VariantType, prop.Quantize, prop.UnitVector);
                _propTypes[i] = prop.VariantType;
                _propSupportsDelta[i] = SupportsDelta(prop.VariantType);
                if (prop.IsObjectProperty || IsByteCoded(prop.VariantType)) _byteCodedMask |= 1L << i;
                _propIsNodeRef[i] = prop.IsObjectProperty && Protocol.IsNodeReferenceClass(prop.ClassIndex);
                _propIsObject[i] = prop.IsObjectProperty;
                _propClassIndex[i] = prop.ClassIndex;
                _propIsPerPeer[i] = prop.IsPerPeer;
                _propInterestMask[i] = prop.InterestMask;
                _propInterestRequired[i] = prop.InterestRequired;
                _propIntWidth[i] = ResolveIntWidth(prop.Metadata.TypeIdentifier);
                _propChunkBudget[i] = prop.ChunkBudget;
                if (!prop.IsObjectProperty)
                {
                    if (prop.IsPerPeer) _perPeerPrimMask |= 1L << i;
                    if (prop.VariantType == SerialVariantType.Object) _objectValuePrimMask |= 1L << i;
                }
                if (!prop.IsObjectProperty || _propIsNodeRef[i])
                {
                    _initSyncEligibleBytes ??= new byte[_byteCount];
                    _initSyncEligibleBytes[i / BitConstants.BitsInByte] |= (byte)(1 << (i % BitConstants.BitsInByte));
                }
                if (prop.IsObjectProperty && !_propIsNodeRef[i] && !Protocol.IsNetArrayClass(prop.ClassIndex))
                {
                    _pollRequiredPropsMask |= 1L << i;
                }
                if (prop.IsObjectProperty) _hasObjectProps = true;
            }

            _propertiesUpdated = new byte[_byteCount];
            _actualMask = new byte[_byteCount];
            _dirtyOnlyMask = new byte[_byteCount];
            _leftoverMask = new byte[_byteCount];
            _decodedMask = new byte[_byteCount];
            _decodedValues = new PropertyCache[_propertyCount];
            _incomingMask = new byte[_byteCount];
            _validPropsMask = _propertyCount >= 64 ? -1L : (1L << _propertyCount) - 1;
            _initSyncEligibleBytes ??= new byte[_byteCount];

            if (NetRunner.Instance.IsServer)
            {
                // Dirty tracking is now handled by NetworkController.MarkDirty() which sets DirtyMask
                // and populates CachedProperties. No more Godot signal subscription needed.

                _interestChangedHandler = (UUID peerId, long oldInterest, long newInterest) =>
                {
                    // ANY interest change can create sendable bytes for a clean node, so
                    // settledness dies first - unconditionally, BEFORE the early return
                    // below, which fires exactly for peers that have never been exported
                    // and must not stay skipped through their first grant.
                    ref var settledState = ref CollectionsMarshal.GetValueRefOrNullRef(_peerStates, peerId);
                    if (!Unsafe.IsNullRef(ref settledState) && settledState.IsInitialized)
                    {
                        settledState.Settled = false;
                    }

                    // Handle interest changes for peerInitialPropSync
                    if (!peerInitialPropSync.TryGetValue(peerId, out var syncMask))
                        return;

                    var remainingNonDefault = _nonDefaultMask;
                    while (remainingNonDefault != 0)
                    {
                        int propIndex = System.Numerics.BitOperations.TrailingZeroCount(remainingNonDefault);
                        remainingNonDefault &= remainingNonDefault - 1;

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

                    // Per-peer overrides re-ship on visibility gain the same way. They are
                    // not in _nonDefaultMask (per-peer writes bypass the shared dirty
                    // mask), so without this a peer regaining interest keeps a stale value.
                    if (network.PerPeerPropIndices != null && network.PerPeerValues != null)
                    {
                        foreach (var propIndex in network.PerPeerPropIndices)
                        {
                            if (propIndex >= _propertyCount) continue;
                            var overrides = network.PerPeerValues[propIndex];
                            if (overrides == null || !overrides.ContainsKey(peerId)) continue;

                            long interestMask = _propInterestMask[propIndex];
                            long interestRequired = _propInterestRequired[propIndex];

                            bool wasVisible = (interestMask & oldInterest) != 0
                                && (interestRequired & oldInterest) == interestRequired;
                            bool isNowVisible = (interestMask & newInterest) != 0
                                && (interestRequired & newInterest) == interestRequired;

                            if (!wasVisible && isNowVisible)
                            {
                                ClearBit(syncMask, propIndex);
                                ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(_peerStates, peerId);
                                if (!Unsafe.IsNullRef(ref state) && state.IsInitialized)
                                {
                                    ClearBit(state.AckedMask, propIndex);
                                }
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
        /// Fills the per-property quantization caches from the declared step. A step on a
        /// type NEBULA010 rejects is ignored here (the generator already refused to build),
        /// so the writer and reader can dispatch on <c>_propQuantStep &gt; 0</c> alone.
        /// </summary>
        private void ResolveQuantization(int propIndex, SerialVariantType type, float step, bool unitVector)
        {
            if (step <= 0f || !QuantizedCodec.IsQuantizable(type)) return;
            _propQuantStep[propIndex] = step;
            _propUnitVector[propIndex] = unitVector && type == SerialVariantType.Vector3;
            _propQuantBits[propIndex] = type == SerialVariantType.Quaternion ? QuantizedCodec.ResolveQuatBits(step) : (byte)0;
            _propQuantComponents[propIndex] = (byte)QuantizedCodec.ComponentCount(type, _propUnitVector[propIndex]);
            _quantizedMask |= 1L << propIndex;
            _lastGridCodes ??= new int[_propertyCount * QuantizedCodec.MaxComponents];
        }

        /// <summary>
        /// Grid codes of a property's current value: the integers the wire carries. A
        /// quaternion contributes its packed word as a single code.
        /// </summary>
        private void GridCodes(int propIndex, in PropertyCache value, Span<int> codes)
        {
            var type = _propTypes[propIndex];
            if (type == SerialVariantType.Quaternion)
            {
                codes[0] = (int)QuantizedCodec.PackQuat(value.QuatValue, _propQuantBits[propIndex]);
                return;
            }
            QuantizedCodec.Encode(in value, type, _propUnitVector[propIndex], _propQuantStep[propIndex], codes);
        }

        /// <summary>
        /// The quantized dead-band: true when the property's current grid codes equal the
        /// last codes that passed this filter, i.e. the change is invisible on the wire.
        /// Records the codes when they differ (or on the first call), so the comparison is
        /// always against the last SHIPPED cell.
        /// </summary>
        private bool GridUnchanged(int propIndex)
        {
            int count = _propTypes[propIndex] == SerialVariantType.Quaternion ? 1 : _propQuantComponents[propIndex];
            Span<int> codes = stackalloc int[QuantizedCodec.MaxComponents];
            GridCodes(propIndex, in network.CachedProperties[propIndex], codes);
            int baseSlot = propIndex * QuantizedCodec.MaxComponents;
            long bit = 1L << propIndex;
            bool same = (_gridSeededMask & bit) != 0;
            for (int k = 0; same && k < count; k++)
            {
                if (_lastGridCodes[baseSlot + k] != codes[k]) same = false;
            }
            if (same) return true;
            for (int k = 0; k < count; k++) _lastGridCodes[baseSlot + k] = codes[k];
            _gridSeededMask |= bit;
            return false;
        }

        /// <summary>Value types written with a byte codec rather than bit fields (see _byteCodedMask).</summary>
        private static bool IsByteCoded(SerialVariantType type)
            => type is SerialVariantType.Object or SerialVariantType.String
                or SerialVariantType.PackedByteArray or SerialVariantType.PackedInt32Array or SerialVariantType.PackedInt64Array;

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
        /// <summary>
        /// Returns a peer state to "never sent anything, never heard an ack" - the arrays are
        /// kept (they are peer-size-independent) but every mask, the sent history, and the
        /// delta baseline are wiped. Shared by pool reuse and <see cref="ResetPeerBaseline"/>.
        /// </summary>
        private static void ClearPeerState(ref PeerPropertyState state)
        {
            // A pooled state handed to a NEW peer must never inherit settledness - the new
            // peer is owed the full initial sync, and a stale true here would skip it
            // forever after one lost first packet.
            state.Settled = false;
            Array.Clear(state.AckedMask, 0, state.AckedMask.Length);
            Array.Clear(state.PendingDirtyMask, 0, state.PendingDirtyMask.Length);
            Array.Clear(state.SentHistory, 0, state.SentHistory.Length);
            for (int i = 0; i < state.SentHistory.Length; i++)
            {
                state.SentHistory[i].Tick = -1;
            }
            Array.Clear(state.DeltaChain, 0, state.DeltaChain.Length);
            Array.Clear(state.LossyMask, 0, state.LossyMask.Length);
            state.LatestAckedTick = -1;
            state.IsInitialized = true;
        }

        private PeerPropertyState CreateOrGetPooledState()
        {
            if (_statePool.Count > 0)
            {
                var state = _statePool.Pop();
                ClearPeerState(ref state);
                return state;
            }

            var fresh = new PeerPropertyState
            {
                AckedMask = new byte[_byteCount],
                PendingDirtyMask = new byte[_byteCount],
                SentHistory = new SentRecord[SNAPSHOT_RING_SIZE],
                LatestAckedTick = -1,
                DeltaChain = new byte[_propertyCount],
                LossyMask = new byte[_byteCount],
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
        /// typeIdentifier (from ProtocolNetProperty.Metadata) disambiguates the
        /// Object case: custom INetValue structs live in dedicated union fields,
        /// so RefValue alone cannot tell them apart.
        /// </summary>
        internal static bool PropertyCacheEquals(string typeIdentifier, ref PropertyCache a, ref PropertyCache b)
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
                SerialVariantType.Object => CustomValueEquals(typeIdentifier, ref a, ref b),
                _ => false
            };
        }

        /// <summary>
        /// Equality for VariantType.Object caches. Must mirror SetDeserializedValueToCache
        /// and NetworkController.SetCachedValue: UUID and NetId are stored in their union
        /// fields (RefValue stays null, so comparing it would report all values equal),
        /// while other custom types are boxed in RefValue.
        /// </summary>
        private static bool CustomValueEquals(string typeIdentifier, ref PropertyCache a, ref PropertyCache b)
        {
            switch (typeIdentifier)
            {
                case "Nebula.UUID":
                    return a.UUIDValue.Equals(b.UUIDValue);
                case "Nebula.NetId":
                    return a.NetIdValue.Equals(b.NetIdValue);
                default:
                    return ReferenceEquals(a.RefValue, b.RefValue) || object.Equals(a.RefValue, b.RefValue);
            }
        }

        /// <summary>
        /// Gets a node by path with caching to avoid GetNode() allocations.
        ///
        /// Three things the naive cache got wrong:
        /// - A freed or reparented node left a dangling entry that was handed out forever.
        ///   Entries are revalidated with IsInstanceValid and re-resolved when stale.
        /// - A failed lookup was cached as null, so the property could never recover even
        ///   once the node existed - and the caller re-logged the failure every tick.
        ///   Misses are no longer cached.
        /// - The StringName -> NodePath conversion (which allocates a string and a NodePath)
        ///   ran on every miss. It is cached separately so a re-resolve reuses it.
        /// </summary>
        private Node GetCachedNode(StringName nodePath)
        {
            if (_nodePathCache.TryGetValue(nodePath, out var node))
            {
                if (GodotObject.IsInstanceValid(node))
                {
                    return node;
                }
                // Node was freed or replaced - drop the stale entry and resolve again.
                _nodePathCache.Remove(nodePath);
            }

            if (!_nodePathConversionCache.TryGetValue(nodePath, out var path))
            {
                path = new NodePath(nodePath.ToString());
                _nodePathConversionCache[nodePath] = path;
            }

            node = network.RawNode.GetNodeOrNull(path);
            if (node != null)
            {
                _nodePathCache[nodePath] = node;
            }
            // Misses are deliberately not cached: the node may be added later, and caching
            // null would pin the failure permanently.
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

            // For object properties (INetSerializable), always consider them changed when
            // received. These types (like NetArray) are deserialized in-place and track their
            // own changes internally. The fact that we received data means something changed.
            // INetValue types (UUID, NetId, ...) also have VariantType.Object but are plain
            // values: they get a real equality check, because the server re-sends unacked
            // properties every tick and duplicates must not re-fire NotifyOnChange handlers.
            bool valueChanged = prop.IsObjectProperty
                || !PropertyCacheEquals(prop.Metadata.TypeIdentifier, ref oldValue, ref newValue);

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

        // ============================================================
        // TEST SEAMS (unit tests only - same-assembly access)
        // ============================================================

        /// <summary>
        /// Test seam: builds a serializer with hand-supplied property metadata, bypassing the
        /// Protocol registry (which requires a registered net scene). Client-side Deserialize
        /// paths work fully; server-side Export/Acknowledge work for PRIMITIVE properties
        /// against a WorldRunner prepared with CreatePeerStateForTests (object properties
        /// need the Protocol serializer registry and must not be used here).
        /// </summary>
        /// <param name="intWidths">
        /// Optional per-property integer width (Int props only; null = Int64 for all). Encoding
        /// metadata the real ctor resolves from the protocol table is supplied here by the
        /// test, so the reader and writer under test agree the same way they do in production.
        /// </param>
        /// <param name="quantizeSteps">Optional per-property grid step (0 = unquantized), as NetProperty.Quantize.</param>
        /// <param name="unitVectors">Optional per-property UnitVector flag, as NetProperty.UnitVector.</param>
        internal NetPropertiesSerializer(NetworkController _network, SerialVariantType[] propTypes, IntWidth[] intWidths = null,
            float[] quantizeSteps = null, bool[] unitVectors = null)
        {
            network = _network;
            _cachedSceneFilePath = network.RawNode.SceneFilePath;

            _propertyCount = propTypes.Length;
            _byteCount = (_propertyCount + BitConstants.BitsInByte - 1) / BitConstants.BitsInByte;
            _worstCaseHeaderBits = MaskModeBits + AgeBits + PresenceMask.WorstCaseBits(_byteCount, _propertyCount) + BodyAlignPadBits;

            _propTypes = propTypes;
            // Mirrors the real ctor: without this, useDelta was false for every test and the
            // delta/lossy write paths had no unit coverage at all.
            _propSupportsDelta = new bool[_propertyCount];
            for (int i = 0; i < _propertyCount; i++)
            {
                _propSupportsDelta[i] = SupportsDelta(propTypes[i]);
                if (IsByteCoded(propTypes[i])) _byteCodedMask |= 1L << i;
            }
            _propIsNodeRef = new bool[_propertyCount];
            _propIsObject = new bool[_propertyCount];
            _propClassIndex = new int[_propertyCount];
            for (int i = 0; i < _propertyCount; i++) _propClassIndex[i] = -1;
            _propIsPerPeer = new bool[_propertyCount];
            _propInterestMask = new long[_propertyCount];
            // Visible on every interest layer, like a property with no [NetInterest].
            for (int i = 0; i < _propertyCount; i++) _propInterestMask[i] = -1L;
            _propInterestRequired = new long[_propertyCount];
            _propIntWidth = intWidths ?? new IntWidth[_propertyCount];
            if (_propIntWidth.Length != _propertyCount)
            {
                throw new ArgumentException($"intWidths length {_propIntWidth.Length} != property count {_propertyCount}", nameof(intWidths));
            }
            _propChunkBudget = new int[_propertyCount];
            _propQuantStep = new float[_propertyCount];
            _propQuantBits = new byte[_propertyCount];
            _propUnitVector = new bool[_propertyCount];
            _propQuantComponents = new byte[_propertyCount];
            for (int i = 0; i < _propertyCount; i++)
            {
                ResolveQuantization(i,
                    propTypes[i],
                    quantizeSteps != null ? quantizeSteps[i] : 0f,
                    unitVectors != null && unitVectors[i]);
            }

            _propertiesUpdated = new byte[_byteCount];
            _actualMask = new byte[_byteCount];
            _dirtyOnlyMask = new byte[_byteCount];
            _leftoverMask = new byte[_byteCount];
            _decodedMask = new byte[_byteCount];
            _decodedValues = new PropertyCache[_propertyCount];
            _incomingMask = new byte[_byteCount];
            _validPropsMask = _propertyCount >= 64 ? -1L : (1L << _propertyCount) - 1;
            _initSyncEligibleBytes ??= new byte[_byteCount];
            // Test-ctor props are primitives (or INetValue stand-ins) - all merge-eligible.
            for (int i = 0; i < _propertyCount; i++)
            {
                _initSyncEligibleBytes[i / BitConstants.BitsInByte] |= (byte)(1 << (i % BitConstants.BitsInByte));
            }

            // Memo precondition masks: everything in this ctor is a plain primitive, so
            // both stay zero — mirroring the real ctor's derivation. Tests that need P2
            // coverage pass SerialVariantType.Object entries, which land in the mask here.
            for (int i = 0; i < _propertyCount; i++)
            {
                if (propTypes[i] == SerialVariantType.Object) _objectValuePrimMask |= 1L << i;
            }

            // The real ctor registers this only under IsServer, which the unit runner never
            // is - but the tests ARE exercising server-side export logic, and the settled
            // flag's interest wake-up is part of it.
            _interestChangedHandler = (UUID peerId, long oldInterest, long newInterest) =>
            {
                ref var settledState = ref CollectionsMarshal.GetValueRefOrNullRef(_peerStates, peerId);
                if (!Unsafe.IsNullRef(ref settledState) && settledState.IsInitialized)
                {
                    settledState.Settled = false;
                }
            };
            network.InterestChanged += _interestChangedHandler;
        }

        /// <summary>Test seam: runs Deserialize and reports whether the payload was applied.</summary>
        internal bool DeserializeForTests(NetBuffer buffer, Tick currentTick)
        {
            Deserialize(buffer, currentTick, out bool discarded);
            return !discarded;
        }

        /// <summary>Test seam: whether the settled flag is currently set for this peer.</summary>
        internal bool SettledForTests(UUID peerId)
            => _peerStates.TryGetValue(peerId, out var state) && state.IsInitialized && state.Settled;

        /// <summary>Test seam: one byte of the peer's lossy-delta mask (settle-absolute driver).</summary>
        internal byte LossyByteForTests(UUID peerId, int byteIndex)
        {
            if (!_peerStates.TryGetValue(peerId, out var state) || !state.IsInitialized)
            {
                return 0;
            }
            return state.LossyMask[byteIndex];
        }

        /// <summary>
        /// Test seam: Begin() captures the tick value ring only on a server, so delta
        /// encoding is unreachable in the unit runner. Forcing capture lets memo tests
        /// exercise the delta-path signatures the soak otherwise covers alone.
        /// </summary>
        internal bool ForceRingCaptureForTests;

        /// <summary>Test seam: one byte of the peer's resend-until-acked pending mask.</summary>
        internal byte PendingDirtyByteForTests(UUID peerId, int byteIndex)
        {
            if (!_peerStates.TryGetValue(peerId, out var state) || !state.IsInitialized)
            {
                return 0;
            }
            return state.PendingDirtyMask[byteIndex];
        }

        /// <summary>Test seam: whether the applied-state ring holds an entry for this exact tick.</summary>
        internal bool HasAppliedEntryForTests(Tick tick)
        {
            if (_appliedRing == null) return false;
            ref var entry = ref _appliedRing[tick % SNAPSHOT_RING_SIZE];
            return entry.Values != null && entry.Tick == tick;
        }

        /// <summary>Test seam (client): the applied value recorded for a property at a tick.</summary>
        internal PropertyCache AppliedValueForTests(Tick tick, int propIndex)
        {
            ref var entry = ref _appliedRing[tick % SNAPSHOT_RING_SIZE];
            if (entry.Values == null || entry.Tick != tick) throw new InvalidOperationException($"no applied entry at tick {tick}");
            return entry.Values[propIndex];
        }

        /// <summary>Test seam (server): the delta-ring value (canonical for quantized props) at a tick.</summary>
        internal PropertyCache RingValueForTests(Tick tick, int propIndex)
        {
            ref var entry = ref _tickValueRing[tick % SNAPSHOT_RING_SIZE];
            if (entry.Values == null || entry.Tick != tick) throw new InvalidOperationException($"no ring entry at tick {tick}");
            return entry.Values[propIndex];
        }

        private Data Deserialize(NetBuffer buffer, Tick currentTick, out bool discarded)
        {
            int byteCount = _byteCount;

            // Decode into reusable scratch. _incomingMask is fully overwritten by the decode
            // below - a flat read writes every byte, and the two-level decode zeroes the
            // bytes its header skips (a stale bit left from the previous payload would read
            // as a present property and misparse everything after it). _decodedMask must be
            // cleared because it accumulates as we decode.
            byte[] propertiesUpdated = _incomingMask;
            Array.Clear(_decodedMask, 0, byteCount);

            // Header: [maskMode:1][age:5][mask unless maskMode]. The age is read first because
            // mask reuse ("same mask as the section applied at the baseline tick") needs the
            // baseline resolved before the mask can be known.
            bool maskReuse = buffer.ReadBool();
            int baselineAge = (int)buffer.ReadBits(AgeBits);
            int sectionStartBits = buffer.ReadBitPosition - MaskModeBits - AgeBits;

            // ============================================================
            // BASELINE RESOLUTION (snapshot-delta)
            // ============================================================
            // Deltas in this payload were computed by the server against its value
            // snapshot at baselineTick - a tick this client received, applied, and acked.
            // They must be applied against OUR recorded state at that same tick, never
            // against the running value (which may include newer in-flight updates).
            PropertyCache[] baselineValues = null;
            long baselineMask = 0;
            bool discardPayload = false;

            // Scratch baseline handed to ReadDeltaOrAbsolute when this payload has no
            // resolvable baseline. A local (not a shared static) so that a future edit which
            // writes through the ref cannot leak state across nodes or across ticks.
            PropertyCache noBaseline = default;
            if (baselineAge > 0)
            {
                Tick baselineTick = currentTick - baselineAge;

                // The age comes off the wire unvalidated. A server never writes more than
                // MAX_DELTA_AGE, so anything larger means a desynced/corrupt stream; and a
                // negative baselineTick (age exceeding a young world's tick count) would
                // index the ring with a negative value and throw, aborting the whole tick
                // import. Both are handled as a discard, same as a missing baseline.
                if (baselineAge > MAX_DELTA_AGE || baselineTick < 0)
                {
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                        $"[NetPropertiesSerializer.Deserialize] NetId={network.NetId} invalid baseline age {baselineAge} at tick {currentTick}. Discarding payload.");
                    discardPayload = true;
                }
                else
                {
                    if (_appliedRing != null)
                    {
                        ref var baseEntry = ref _appliedRing[baselineTick % SNAPSHOT_RING_SIZE];
                        if (baseEntry.Values != null && baseEntry.Tick == baselineTick)
                        {
                            baselineValues = baseEntry.Values;
                            baselineMask = baseEntry.Mask;
                        }
                    }

                    if (baselineValues == null)
                    {
                        // Reachable when this node was rebuilt client-side (respawn) while the
                        // server retained a baseline, or when a prior payload was discarded.
                        // Parse the payload to keep the stream aligned, but discard the values;
                        // reporting the discard (instead of acking) is what lets the server
                        // fall back to the last shared baseline or an absolute send.
                        Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                            $"[NetPropertiesSerializer.Deserialize] NetId={network.NetId} missing applied-state baseline for tick {baselineTick} (age {baselineAge}). Discarding payload.");
                        discardPayload = true;
                    }
                }
            }

            if (maskReuse)
            {
                // The mask is not on the wire; without the baseline entry the section cannot
                // be parsed at all, so this is an abort (ImportState's catch, tick un-acked),
                // not a per-node discard. It self-heals: the server keeps deltaing against
                // the stale acked tick until the age passes MAX_DELTA_AGE, then goes absolute
                // with age 0, which always carries the full mask.
                if (baselineValues == null)
                {
                    throw new InvalidOperationException(
                        $"NetId={network.NetId} section reuses the mask of baseline tick {currentTick - baselineAge}, which this client has not applied; the section cannot be parsed.");
                }
                LongToMask(baselineMask, propertiesUpdated, byteCount);
            }
            else
            {
                PresenceMask.Read(buffer, propertiesUpdated.AsSpan(0, byteCount), _propertyCount);
            }
            long wireMask = MaskToLong(propertiesUpdated, byteCount);
            // Mirror of the writer's body pad for byte-coded values (see _byteCodedMask).
            if ((wireMask & _byteCodedMask) != 0)
            {
                buffer.AlignRead();
            }
            if (TraceWire) Debugger.Instance.Log($"[Props.R] {_cachedSceneFilePath} byteCount={byteCount} mask={Convert.ToHexString(propertiesUpdated, 0, byteCount)} reuse={maskReuse} age={baselineAge} bit={buffer.ReadBitPosition}");

            // ============================================================
            // TWO-PASS DESERIALIZATION (must match server Export order)
            // Pass 1: Read PRIMITIVE properties first
            // Pass 2: Read OBJECT properties second
            // ============================================================

            // Pass 1: Read PRIMITIVE properties (non-IsObjectProperty)
            // Note: We use IsObjectProperty (INetSerializable vs INetValue) NOT VariantType
            // to match the server's Export order which uses _propIsObject[]
            for (int propertyByteIndex = 0; propertyByteIndex < byteCount; propertyByteIndex++)
            {
                var propertyByte = propertiesUpdated[propertyByteIndex];
                for (byte propertyBit = 0; propertyBit < BitConstants.BitsInByte; propertyBit++)
                {
                    if ((propertyByte & (1 << propertyBit)) == 0)
                    {
                        continue;
                    }

                    var propertyIndex = propertyByteIndex * BitConstants.BitsInByte + propertyBit;

                    // Bounded by _propertyCount, not CachedProperties.Length: the latter is a
                    // fixed 64 regardless of how many properties this scene declares, so it
                    // would admit indices that have no entry in the pre-cached metadata arrays.
                    if (propertyIndex >= _propertyCount)
                    {
                        Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"[NetPropertiesSerializer.Deserialize] propertyIndex {propertyIndex} >= property count {_propertyCount}! Skipping property.");
                        continue;
                    }

                    // Metadata comes from the constructor's pre-cached arrays, the same ones
                    // the writer dispatches on (and the only ones the Protocol-free test
                    // constructor fills), never from a per-property registry lookup here.
                    // Skip IsObjectProperty (INetSerializable) - handled in Pass 2
                    if (_propIsObject[propertyIndex])
                    {
                        continue;
                    }
                    var propType = _propTypes[propertyIndex];
                    ref var existingCache = ref network.CachedProperties[propertyIndex];

                    int propStartPos = buffer.ReadPosition;
                    var cache = new PropertyCache();

                    if (propType == SerialVariantType.Nil)
                    {
                        Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Property {PropertyNameForLog(propertyIndex)} has VariantType.Nil, cannot deserialize");
                        continue;
                    }

                    // INetValue types with Object VariantType (like UUID) need special handling
                    // They're written with delta encoding wrapper (Absolute flag byte first) but use custom deserializer
                    if (propType == SerialVariantType.Object)
                    {
                        // Read the delta encoding flag byte (will always be Absolute for Object types since they don't support delta)
                        var flags = (DeltaEncodingFlags)buffer.ReadBits(EncodingBits);
                        if (flags != DeltaEncodingFlags.Absolute)
                        {
                            Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Expected Absolute encoding for INetValue Object type {PropertyNameForLog(propertyIndex)}, got {flags}");
                        }

                        // Use the deserializer for the value type
                        var deserializer = Protocol.GetDeserializer(_propClassIndex[propertyIndex]);
                        if (deserializer == null)
                        {
                            Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"No deserializer found for INetValue {PropertyNameForLog(propertyIndex)}");
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
                        // payload.
                        if (baselineValues != null)
                        {
                            ReadDeltaOrAbsolute(buffer, propertyIndex, propType, _propIntWidth[propertyIndex], ref baselineValues[propertyIndex], ref cache);
                        }
                        else
                        {
                            ReadDeltaOrAbsolute(buffer, propertyIndex, propType, _propIntWidth[propertyIndex], ref noBaseline, ref cache);
                        }
                    }

                    if (TraceWire) Debugger.Instance.Log($"[Props.R] idx={propertyIndex} '{PropertyNameForLog(propertyIndex)}' type={propType} bytes={buffer.ReadPosition - propStartPos} end={buffer.ReadPosition}");

                    if (!discardPayload)
                    {
                        _decodedValues[propertyIndex] = cache;
                        _decodedMask[propertyByteIndex] |= (byte)(1 << propertyBit);
                    }
                }
            }

            // Pass 2: Read OBJECT properties (IsObjectProperty = INetSerializable types)
            for (int propertyByteIndex = 0; propertyByteIndex < byteCount; propertyByteIndex++)
            {
                var propertyByte = propertiesUpdated[propertyByteIndex];
                for (byte propertyBit = 0; propertyBit < BitConstants.BitsInByte; propertyBit++)
                {
                    if ((propertyByte & (1 << propertyBit)) == 0)
                    {
                        continue;
                    }

                    var propertyIndex = propertyByteIndex * BitConstants.BitsInByte + propertyBit;

                    // Only process IsObjectProperty (INetSerializable) in this pass; the
                    // registry lookup is deferred until the bit is known to be one (the
                    // Protocol-free test ctor has no registry entry to unpack).
                    if (propertyIndex >= _propertyCount || !_propIsObject[propertyIndex])
                    {
                        continue;
                    }
                    var prop = Protocol.UnpackProperty(_cachedSceneFilePath, propertyIndex);
                    if (string.IsNullOrEmpty(prop.Name))
                    {
                        continue;
                    }
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

                    int propStartPos = buffer.ReadPosition;
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

                    if (TraceWire) Debugger.Instance.Log($"[Props.R] obj idx={propertyIndex} '{prop.NodePath}.{prop.Name}' bytes={buffer.ReadPosition - propStartPos} end={buffer.ReadPosition}");

                    // Object properties are recorded even when discardPayload is set: they
                    // are deserialized in place and carry no delta baseline, so the decode
                    // has already mutated the live object regardless.
                    _decodedValues[propertyIndex] = cache;
                    _decodedMask[propertyByteIndex] |= (byte)(1 << propertyBit);
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

                for (int byteIdx = 0; byteIdx < byteCount; byteIdx++)
                {
                    var decodedByte = _decodedMask[byteIdx];
                    if (decodedByte == 0) continue;
                    for (int bit = 0; bit < BitConstants.BitsInByte; bit++)
                    {
                        if ((decodedByte & (1 << bit)) == 0) continue;
                        int propIndex = byteIdx * BitConstants.BitsInByte + bit;
                        if (propIndex >= _propertyCount) continue;
                        entry.Values[propIndex] = _decodedValues[propIndex];
                    }
                }

                entry.Tick = currentTick;
                entry.Mask = wireMask;
                _lastAppliedTick = currentTick;
            }

            if (TraceWire) Debugger.Instance.Log($"[Props.R] NetId={network.NetId} sectionBits={buffer.ReadBitPosition - sectionStartBits}");
            discarded = discardPayload;
            return new Data(_decodedMask, _decodedValues);
        }

        /// <summary>
        /// Reads a property value with delta decoding support. Deltas are applied against
        /// the baseline snapshot value (the client's recorded state at the packet's
        /// declared baseline tick), never against the running value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadDeltaOrAbsolute(NetBuffer buffer, int propIndex, SerialVariantType type, IntWidth intWidth, ref PropertyCache baseline, ref PropertyCache cache)
        {
            var flags = (DeltaEncodingFlags)buffer.ReadBits(EncodingBits);
            cache.Type = type;

            // Quantized properties have their own forms for every flag value, decided by
            // the protocol table.
            if (_propQuantStep[propIndex] > 0f)
            {
                ReadQuantized(buffer, flags, propIndex, type, ref baseline, ref cache);
                return;
            }

            // A quaternion is always a smallest-three absolute; no flag bit marks it.
            if (type == SerialVariantType.Quaternion)
            {
                cache.QuatValue = QuantizedCodec.ReadQuat(buffer, QuantizedCodec.UnquantizedQuatBits);
                return;
            }

            switch (flags)
            {
                case DeltaEncodingFlags.Absolute:
                    // Full absolute value
                    ReadAbsoluteValue(buffer, type, intWidth, ref cache);
                    break;

                case DeltaEncodingFlags.DeltaSmall:
                    // Small delta (half-float/short encoding)
                    ReadSmallDelta(buffer, type, intWidth, ref baseline, ref cache);
                    break;

                case DeltaEncodingFlags.DeltaFull:
                    // Full delta (same type as property)
                    ReadFullDelta(buffer, type, intWidth, ref baseline, ref cache);
                    break;

                default:
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Unknown delta encoding flag: {flags}");
                    ReadAbsoluteValue(buffer, type, intWidth, ref cache);
                    break;
            }
        }

        /// <summary>
        /// Mirror of the writer's quantized paths (WriteAbsolute / WriteDelta): a packed
        /// quaternion word, or N grid codes absolute / as a small packed delta / as varint
        /// deltas applied to Quantize(baseline). The stored value is Dequantize(codes),
        /// which is exactly what the server's canonical ring holds for this tick.
        /// </summary>
        private void ReadQuantized(NetBuffer buffer, DeltaEncodingFlags flags, int propIndex, SerialVariantType type, ref PropertyCache baseline, ref PropertyCache cache)
        {
            if (type == SerialVariantType.Quaternion)
            {
                cache.QuatValue = QuantizedCodec.ReadQuat(buffer, _propQuantBits[propIndex]);
                return;
            }

            int count = _propQuantComponents[propIndex];
            bool unit = _propUnitVector[propIndex];
            float step = _propQuantStep[propIndex];
            Span<int> codes = stackalloc int[QuantizedCodec.MaxComponents];
            switch (flags)
            {
                case DeltaEncodingFlags.Absolute:
                    QuantizedCodec.ReadCodes(buffer, codes, count);
                    break;
                case DeltaEncodingFlags.DeltaSmall:
                case DeltaEncodingFlags.DeltaFull:
                {
                    Span<int> deltas = stackalloc int[QuantizedCodec.MaxComponents];
                    if (flags == DeltaEncodingFlags.DeltaSmall)
                        QuantizedCodec.ReadSmallDelta(buffer, deltas, count);
                    else
                        QuantizedCodec.ReadCodes(buffer, deltas, count);
                    QuantizedCodec.Encode(in baseline, type, unit, step, codes);
                    for (int k = 0; k < count; k++) codes[k] += deltas[k];
                    break;
                }
                default:
                    throw new InvalidOperationException($"unknown delta encoding flag {flags} on quantized property {propIndex}");
            }
            QuantizedCodec.Decode(codes, type, unit, step, ref cache);
        }

        /// <summary>Property name for a log line; the registry may not know a test scene.</summary>
        private string PropertyNameForLog(int propIndex)
            => Protocol.TryUnpackProperty(_cachedSceneFilePath, propIndex, out var prop) ? $"{prop.NodePath}.{prop.Name}" : $"#{propIndex}";

        /// <summary>
        /// Reads an absolute property value (no delta). Int properties read at the width the
        /// constructor resolved (the same <see cref="IntWidth"/> WriteAbsoluteValue writes at),
        /// so reader and writer share one source of truth; everything else delegates to
        /// NetReader.ReadAbsoluteValue.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadAbsoluteValue(NetBuffer buffer, SerialVariantType type, IntWidth intWidth, ref PropertyCache cache)
        {
            switch (type)
            {
                case SerialVariantType.Bool:
                    cache.BoolValue = buffer.ReadBool();
                    break;
                case SerialVariantType.Int:
                    cache.LongValue = 0;
                    switch (intWidth)
                    {
                        case IntWidth.Byte:
                            cache.ByteValue = (byte)buffer.ReadBits(BitConstants.BitsInByte);
                            break;
                        case IntWidth.Int16:
                            cache.IntValue = (short)(ushort)buffer.ReadBits(BitConstants.BitsInShort);
                            break;
                        case IntWidth.UInt16:
                            cache.IntValue = (ushort)buffer.ReadBits(BitConstants.BitsInShort);
                            break;
                        case IntWidth.Int32:
                        case IntWidth.UInt32:
                            cache.IntValue = (int)(uint)buffer.ReadBits(BitConstants.BitsInInt);
                            break;
                        default:
                            cache.LongValue = (long)buffer.ReadBits(BitConstants.BitsInLong);
                            break;
                    }
                    break;
                case SerialVariantType.Float:
                    cache.FloatValue = GetFloat(buffer);
                    break;
                case SerialVariantType.Vector2:
                    cache.Vec2Value = GetVector2(buffer);
                    break;
                case SerialVariantType.Vector3:
                    cache.Vec3Value = GetVector3(buffer);
                    break;
                case SerialVariantType.Quaternion:
                    cache.QuatValue = QuantizedCodec.ReadQuat(buffer, QuantizedCodec.UnquantizedQuatBits);
                    break;
                // Byte-granular codecs shared with NetFunction arguments: the buffer
                // auto-aligns on the first byte read.
                case SerialVariantType.String:
                case SerialVariantType.PackedByteArray:
                case SerialVariantType.PackedInt32Array:
                case SerialVariantType.PackedInt64Array:
                    NetReader.ReadAbsoluteValue(buffer, type, null, ref cache);
                    break;
                default:
                    throw new NotSupportedException($"ReadAbsoluteValue: unsupported type {type}");
            }
        }

        /// <summary>
        /// Reads a small delta (half-float/short) and applies it to the baseline value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadSmallDelta(NetBuffer buffer, SerialVariantType type, IntWidth intWidth, ref PropertyCache baseline, ref PropertyCache cache)
        {
            switch (type)
            {
                case SerialVariantType.Float:
                    float deltaF = GetHalf(buffer);
                    cache.FloatValue = baseline.FloatValue + deltaF;
                    break;

                case SerialVariantType.Int:
                    // Small delta uses Int16 for all integer types
                    short deltaS = (short)(ushort)buffer.ReadBits(BitConstants.BitsInShort);
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
                    float dx2 = GetHalf(buffer);
                    float dy2 = GetHalf(buffer);
                    cache.Vec2Value = new Vector2(baseline.Vec2Value.X + dx2, baseline.Vec2Value.Y + dy2);
                    break;

                case SerialVariantType.Vector3:
                    float dx3 = GetHalf(buffer);
                    float dy3 = GetHalf(buffer);
                    float dz3 = GetHalf(buffer);
                    cache.Vec3Value = new Vector3(
                        baseline.Vec3Value.X + dx3,
                        baseline.Vec3Value.Y + dy3,
                        baseline.Vec3Value.Z + dz3);
                    break;

                default:
                    // Fallback to absolute for unsupported small delta types
                    Debugger.Instance.Log(Debugger.DebugLevel.WARN, $"Small delta not supported for type {type}, reading absolute");
                    ReadAbsoluteValue(buffer, type, intWidth, ref cache);
                    break;
            }
        }

        /// <summary>
        /// Reads a full delta and applies it to the baseline value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadFullDelta(NetBuffer buffer, SerialVariantType type, IntWidth intWidth, ref PropertyCache baseline, ref PropertyCache cache)
        {
            switch (type)
            {
                case SerialVariantType.Float:
                    float deltaF = GetFloat(buffer);
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
                            short deltaB = (short)(ushort)buffer.ReadBits(BitConstants.BitsInShort);
                            cache.ByteValue = (byte)(baseline.ByteValue + deltaB);
                            break;
                        case IntWidth.Int16:
                        case IntWidth.UInt16:
                            short deltaS = (short)(ushort)buffer.ReadBits(BitConstants.BitsInShort);
                            cache.IntValue = baseline.IntValue + deltaS;
                            break;
                        case IntWidth.Int32:
                        case IntWidth.UInt32:
                            int deltaI = (int)(uint)buffer.ReadBits(BitConstants.BitsInInt);
                            cache.IntValue = baseline.IntValue + deltaI;
                            break;
                        default:
                            // Int64: long, ulong, or an unrecognised subtype
                            long deltaL = (long)buffer.ReadBits(BitConstants.BitsInLong);
                            cache.LongValue = baseline.LongValue + deltaL;
                            break;
                    }
                    break;

                case SerialVariantType.Vector2:
                    Vector2 deltaV2 = GetVector2(buffer);
                    cache.Vec2Value = baseline.Vec2Value + deltaV2;
                    break;

                case SerialVariantType.Vector3:
                    Vector3 deltaV3 = GetVector3(buffer);
                    cache.Vec3Value = baseline.Vec3Value + deltaV3;
                    break;

                default:
                    // Fallback to absolute for unsupported delta types
                    Debugger.Instance.Log(Debugger.DebugLevel.WARN, $"Full delta not supported for type {type}, reading absolute");
                    ReadAbsoluteValue(buffer, type, intWidth, ref cache);
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
                Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"No serializer found for {PropertyNameForLog(propIndex)}");
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

            // New tick, new memo: the cached blobs describe THIS tick's values only.
            _memoCount = 0;

            // Track which properties have ever been set (for initial sync to new peers).
            // Bounded by _propertyCount, not the mask width: indices at or above it have no
            // metadata and would fault the pre-cached arrays if a stray bit ever set one.
            // _validPropsMask keeps the "stray bit at/above the count" defense the old
            // per-index loop provided.
            _nonDefaultMask |= processingDirtyMask & _validPropsMask;

            // Capture this tick's property values for delta baselines. Runs once per node
            // per tick, before any per-peer Export. All peers' deltas for this tick are
            // computed against entries of this ring at their respective acked ticks.
            if (_propertyCount > 0 && (NetRunner.Instance.IsServer || ForceRingCaptureForTests) && network.CurrentWorld != null)
            {
                // Quantized dead-band: a dirty property whose value still encodes to the
                // grid cell last shipped has nothing to say on the wire. Dropped from the
                // tick's dirty set here, before any peer is visited, so it is memo-safe and
                // lets a jittering-but-still node reach Settled. _nonDefaultMask above has
                // already recorded the write for initial sync. Per-peer props never get
                // here (NEBULA010 refuses Quantize with PerPeerState).
                long quantizedDirty = processingDirtyMask & _quantizedMask;
                while (quantizedDirty != 0)
                {
                    int propIndex = System.Numerics.BitOperations.TrailingZeroCount(quantizedDirty);
                    quantizedDirty &= quantizedDirty - 1;
                    if (!_propIsPerPeer[propIndex] && GridUnchanged(propIndex))
                    {
                        processingDirtyMask &= ~(1L << propIndex);
                    }
                }

                _tickValueRing ??= new TickSnapshot[SNAPSHOT_RING_SIZE];
                Tick tick = network.CurrentWorld.CurrentTick;
                ref var entry = ref _tickValueRing[tick % SNAPSHOT_RING_SIZE];
                entry.Values ??= new PropertyCache[_propertyCount];
                Array.Copy(network.CachedProperties, entry.Values, _propertyCount);
                entry.Tick = tick;

                // Baseline canonicalisation: a quantized grid property's ring slot holds the
                // value the CLIENT holds after decoding it, so both sides compute the
                // delta's Quantize(baseline) from bit-identical floats. Gameplay keeps
                // reading full precision from CachedProperties; only the delta ring is
                // canonical. Quaternions are skipped: they never delta (SupportsDelta).
                long canonical = _quantizedMask;
                while (canonical != 0)
                {
                    int propIndex = System.Numerics.BitOperations.TrailingZeroCount(canonical);
                    canonical &= canonical - 1;
                    if (_propQuantComponents[propIndex] == 0) continue;
                    QuantizedCodec.Canonicalize(ref entry.Values[propIndex], _propTypes[propIndex], _propUnitVector[propIndex], _propQuantStep[propIndex]);
                }
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

        public bool Import(WorldRunner currentWorld, NetBuffer buffer, out NetworkController nodeOut)
        {
            nodeOut = network;

            var data = Deserialize(buffer, currentWorld.CurrentTick, out bool discarded);

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

            // Apply primitives first, then object properties. This mirrors the order the
            // old Dictionary happened to yield (insertion order: decode pass 1, then pass 2),
            // so any OnNetworkChange handler that observes a sibling property still sees the
            // same ordering it did before.
            //
            // In discard mode the decoded mask only carries object-property bits (primitives
            // are suppressed at decode; objects still apply - their chunk streams are
            // self-contained and resend-tolerant), so these calls stay unconditional.
            ApplyDecoded(data, currentWorld.CurrentTick, isReady, objectPass: false);
            ApplyDecoded(data, currentWorld.CurrentTick, isReady, objectPass: true);

            // A discarded payload must not be acked: the applied ring has no entry for this
            // tick, so an ack would invite the server to delta against it forever.
            return !discarded;
        }

        /// <summary>
        /// Applies one class of decoded properties (primitives or objects) in index order.
        /// Values are read by ref straight out of the scratch array - no copy, no hashing.
        /// </summary>
        private void ApplyDecoded(Data data, Tick tick, bool isReady, bool objectPass)
        {
            for (int byteIdx = 0; byteIdx < _byteCount; byteIdx++)
            {
                var decodedByte = data.DecodedMask[byteIdx];
                if (decodedByte == 0) continue;

                for (int bit = 0; bit < BitConstants.BitsInByte; bit++)
                {
                    if ((decodedByte & (1 << bit)) == 0) continue;

                    int propIndex = byteIdx * BitConstants.BitsInByte + bit;
                    if (propIndex >= _propertyCount) continue;
                    if (_propIsObject[propIndex] != objectPass) continue;

                    var prop = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                    ref var propValue = ref data.Values[propIndex];

                    if (isReady)
                    {
                        ImportProperty(prop, tick, ref propValue);
                    }
                    else
                    {
                        cachedPropertyChanges[propIndex] = propValue;
                    }
                }
            }
        }

        /// <summary>
        /// Props that have EVER been dirty, as a bitmask (was a HashSet, enumerated per
        /// peer per node per tick in the initial-sync merge — a permanent per-visit heap
        /// walk that grew with the scene). Bit math only from here on.
        /// </summary>
        private long _nonDefaultMask;
        /// <summary>1-bits for every valid prop index; masks stray dirty bits at/above the count.</summary>
        private long _validPropsMask;
        /// <summary>Per byte: props eligible for the initial-sync merge (primitives + node refs).</summary>
        private byte[] _initSyncEligibleBytes;

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

        /// <summary>Whether this peer's interest reaches any poll-required object prop
        /// (interest-required semantics included - a single OR'd mask would get the
        /// all-bits-required case wrong).</summary>
        private bool PeerTouchesPollRequiredProps(long peerInterestLayers)
        {
            long remaining = _pollRequiredPropsMask;
            while (remaining != 0)
            {
                int propIndex = System.Numerics.BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
                if (PeerHasInterestInProperty(propIndex, peerInterestLayers)) return true;
            }
            return false;
        }

        /// <summary>
        /// Rotation pre-gate: true when this node provably owes this peer nothing this
        /// tick, letting WorldRunner skip PropsMayRidePacket and the Export call outright.
        /// Same conditions as the in-Export settled guard.
        /// </summary>
        // PUBLIC, not internal: this implements IStateSerializer.NothingForPeer, whose
        // DEFAULT interface method returns false. An internal method here does not
        // implement the interface member - calls through the IStateSerializer-typed
        // rotation variable silently bind to the default, and the pre-gate never fires.
        // (Found by measurement: memo_slow collapsed but rotation cost did not.)
        public bool NothingForPeer(UUID peerId)
        {
            // Under verify the skip must not happen - the full run is the check.
            if (VerifySettledEnabled) return false;
            return NothingForPeerUnchecked(peerId);
        }

        private bool NothingForPeerUnchecked(UUID peerId)
        {
            if (_settledDisabled || processingDirtyMask != 0) return false;
            if (!_peerStates.TryGetValue(peerId, out var probe)
                || !probe.IsInitialized || !probe.Settled)
            {
                return false;
            }
            long perPeerDirty = 0;
            network.PerPeerDirtyMask?.TryGetValue(peerId, out perPeerDirty);
            return perPeerDirty == 0;
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

        /// <summary>
        /// Scratch mask of properties actually written by the current Export. Instance
        /// scratch rather than a per-call allocation; safe because Export is driven
        /// serially by WorldRunner.ExportState (one peer, one node at a time).
        /// </summary>
        private byte[] _actualMask;

        /// <summary>
        /// Scratch mask of properties that are genuinely dirty THIS tick for the peer being
        /// exported — captured before the non-default/pending/settle merges widen the send
        /// set. Deltas are only valid for freshly-changed values: resends and settle
        /// absolutes must go absolute so the value is exact on arrival. Same serial-access
        /// assumption as _actualMask.
        /// </summary>
        private byte[] _dirtyOnlyMask;

        /// <summary>
        /// Scratch mask of primitive properties that were eligible to ship in the current
        /// Export but were rewound (or never written) for budget. Merged into
        /// PendingDirtyMask before Export returns, so they retry as absolutes on a later
        /// tick. Same serial-access assumption as _actualMask.
        /// </summary>
        private byte[] _leftoverMask;

        // ─── Section memo ─────────────────────────────────────────────────
        //
        // Encode-once-per-signature cache for the PRIMITIVE segment of a props section.
        // The signature (MaskSig, UseDeltaSig, Age) is a COMPLETE description of those
        // bytes for an eligible peer — see the DECIDE PASS comment in ExportCore for the
        // inventory of every per-peer input and why the gate bits subsume them. Peers
        // whose section could contain per-peer bytes (per-peer-valued primitives,
        // INetValue primitives whose serializer receives the peer) are excluded by the
        // precondition masks below and always take the full writer.
        //
        // Lifetime: reset in Begin(), i.e. once per node per tick, before the peer loop.
        // Relies on CachedProperties not being mutated between Begin() and the last
        // peer's Export — nothing in ExportState does; NEBULA_VERIFY_MEMO polices it.
        private struct MemoEntry
        {
            public long MaskSig;
            public long UseDeltaSig;
            public byte Age;
            public byte[] Blob;          // primitive body bits at phase 0, buffer reused across ticks
            public int BlobBits;
            public long LossyResultMask; // WriteDelta lossy returns, for the stamp replay
        }
        private const int MemoCapacity = 4;
        private MemoEntry[] _memo;
        private int _memoCount;
        /// <summary>Primitive props whose VALUE is per-peer (P1). Signature-stable: any
        /// written bit here makes every peer ineligible for that mask.</summary>
        private long _perPeerPrimMask;
        /// <summary>Primitive props of an INetValue type (P2): their generated serializer
        /// receives the peer and may write per-peer bytes (node references do).</summary>
        private long _objectValuePrimMask;
        /// <summary>
        /// Object props that can change with NO dirty signal (in-place-mutated snapshots
        /// like CargoState, whose only send-gate is being asked every tick). A peer whose
        /// interest touches any of these can never be marked Settled. Node references are
        /// excluded (dirty-gated) and so is NetArray (its indexer marks dirty).
        /// </summary>
        private long _pollRequiredPropsMask;
        /// <summary>Latched off for the whole run by a NEBULA_VERIFY_SETTLED divergence.</summary>
        private static bool _settledDisabled;
        private static bool? _verifySettled;
        private static bool VerifySettledEnabled
        {
            get
            {
                if (_verifySettled == null)
                {
                    Nebula.Utility.Tools.Env.TryGetFlag("NEBULA_VERIFY_SETTLED", out var on);
                    _verifySettled = on;
                }
                return _verifySettled.Value;
            }
        }
        internal static void DisableSettledForRun() => _settledDisabled = true;
        internal int MemoHitsForTests;
        /// <summary>
        /// Test-only off switch. The memo is UNCONDITIONAL in production — it ships
        /// byte-identical output (soak-verified via NEBULA_VERIFY_MEMO) and exists only
        /// as how the serializer encodes, not as a mode. Tests set false to build the
        /// slow-path baseline the equivalence matrix compares against.
        /// </summary>
        internal bool? MemoOverrideForTests;
        /// <summary>Latched off for the whole run by a verify-mode divergence.</summary>
        private static bool _memoDisabled;
        private static bool? _verifyMemo;
        private static bool VerifyMemoEnabled
        {
            get
            {
                if (_verifyMemo == null)
                {
                    Nebula.Utility.Tools.Env.TryGetFlag("NEBULA_VERIFY_MEMO", out var on);
                    _verifyMemo = on;
                }
                return _verifyMemo.Value;
            }
        }
        private bool MemoEnabled => MemoOverrideForTests ?? true;

        /// <summary>
        /// Scratch for the payload currently being imported: which property indices decoded
        /// a value, and the values themselves.
        ///
        /// Same serial-access assumption as _actualMask, and one step stronger: the values
        /// are still being read while ImportProperty fires OnNetworkChange handlers, so a
        /// handler that synchronously drove another Import of THIS node would overwrite the
        /// buffer mid-apply. WorldRunner applies packets one node at a time off the network
        /// tick and nothing re-enters it, so this holds - but it is the reason this scratch
        /// is per-serializer rather than shared across serializers.
        ///
        /// _decodedValues is intentionally not cleared between packets; entries whose
        /// _decodedMask bit is unset are never read.
        /// </summary>
        private byte[] _decodedMask;
        private PropertyCache[] _decodedValues;

        /// <summary>Scratch for the incoming presence mask read off the wire.</summary>
        private byte[] _incomingMask;

        public ExportResult Export(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, int maxBits)
        {
            // Self-limiting serializer: never writes more than maxBits, so the host
            // always commits what was written (in-write stamps like DeltaChain, LossyMask
            // and object-prop chunk frontiers stay valid). Packet-coupled stamps
            // (SentHistory, PendingDirtyMask for shipped bits, initial-sync, per-peer
            // dirty clears) apply in CommitExport.

            // NEBULA_VERIFY_SETTLED: the settled machinery claims this peer is owed
            // nothing; with verify on, no skip actually happens (NothingForPeer and the
            // in-Export guard both stand down), the full run executes, and a claim the
            // run contradicts latches the skip off for the whole process. Same
            // one-unaffordable-failure-mode policing the section memo uses.
            if (VerifySettledEnabled && !_settledDisabled)
            {
                bool claimedNothing = NothingForPeerUnchecked(NetRunner.Instance.GetPeerId(peer));
                int before = buffer.WriteBitPosition;
                var verified = ExportCore(currentWorld, peer, buffer, maxBits);
                if (claimedNothing && (verified != ExportResult.None || buffer.WriteBitPosition != before))
                {
                    _settledDisabled = true;
                    Debugger.Instance.Log(
                        $"[SettledVerify] DIVERGENCE on {_cachedSceneFilePath}: the settled flag "
                        + $"claimed nothing to send but the full run produced {verified} with "
                        + $"{buffer.WriteBitPosition - before} bit(s). Settled skipping disabled for "
                        + "this run; a wake-up source is missing.",
                        Debugger.DebugLevel.ERROR);
                }
                return verified;
            }

            return ExportCore(currentWorld, peer, buffer, maxBits);
        }

        private ExportResult ExportCore(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, int maxBits)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);

            // SETTLED GUARD: this node owed this peer nothing at the end of its last full
            // run, nothing has changed that could create bytes (no broadcast dirt this
            // tick, no per-peer dirt), so the entire prologue - spawn-state lookups, mask
            // merges, the object loop - is skipped. See PeerPropertyState.Settled for the
            // discipline that makes this sound. Disabled under NEBULA_VERIFY_SETTLED so
            // the full run can police the skip decision, and latched off for the run by a
            // verify divergence.
            if (processingDirtyMask == 0 && !_settledDisabled && !VerifySettledEnabled
                && _peerStates.TryGetValue(peerId, out var settledProbe)
                && settledProbe.IsInitialized && settledProbe.Settled)
            {
                long guardPerPeerDirty = 0;
                network.PerPeerDirtyMask?.TryGetValue(peerId, out guardPerPeerDirty);
                if (guardPerPeerDirty == 0)
                {
                    return ExportResult.None;
                }
            }

            // Only export if spawn data has been sent AND not despawning/despawned
            // NotSpawned: SpawnSerializer hasn't written spawn data yet
            // Despawning/Despawned: Node is being removed, no point sending property updates
            var spawnState = currentWorld.GetClientSpawnState(network.NetId, peer);
            if (spawnState == WorldRunner.ClientSpawnState.NotSpawned ||
                spawnState == WorldRunner.ClientSpawnState.Despawning ||
                spawnState == WorldRunner.ClientSpawnState.Despawned)
            {
                return ExportResult.None;
            }

            // For nested scenes, don't export until parent spawn is at least being sent
            if (network.NetParent != null)
            {
                var parentSpawnState = currentWorld.GetClientSpawnState(network.NetParent.NetId, peer);
                if (parentSpawnState == WorldRunner.ClientSpawnState.NotSpawned)
                {
                    return ExportResult.None;
                }
            }

            int byteCount = _byteCount;

            Array.Clear(_propertiesUpdated, 0, byteCount);
            Array.Clear(_leftoverMask, 0, byteCount);

            if (!peerInitialPropSync.TryGetValue(peerId, out var initialSync))
            {
                initialSync = new byte[byteCount];
                peerInitialPropSync[peerId] = initialSync;
            }

            // Zero-alloc dictionary access via ref for delta state
            // NOTE: GetValueRefOrAddDefault's out parameter is `exists` - true when the key was
            // ALREADY in the dictionary - not `isNew`. Reading it the other way round meant this
            // block recreated the peer's state on every export after the first, wiping AckedMask,
            // PendingDirtyMask, SentHistory and LatestAckedTick every single tick. Consequences:
            // delta encoding could never engage (no baseline ever survived to be used), acks could
            // never commit (SentHistory was blank by the time the ack arrived), and every export
            // allocated a fresh set of state arrays.
            ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(_peerStates, peerId, out bool exists);
            if (!exists || !state.IsInitialized)
            {
                state = CreateOrGetPooledState();
            }

            // Every full run re-derives settledness from scratch: whatever this run does
            // (ship, bank, defer, throw) the flag only comes back at the empty-section end
            // under the raw-mask conditions. A flag that survived a run could be stale the
            // moment that run banked pending bits and the packet was lost.
            state.Settled = false;

            // Filter against interest layers first
            if (!TryGetInterestLayers(peerId, out var peerInterestLayers))
            {
                return ExportResult.None;
            }

            // Snapshot the per-peer dirty mask AFTER the interest early-return, and do NOT
            // clear it here: bits are cleared at the bottom of Export only for properties
            // that actually shipped, so interest-filtered (or not-yet-visible) changes stay
            // armed and retry next tick instead of being lost permanently.
            long perPeerDirty = 0;
            network.PerPeerDirtyMask?.TryGetValue(peerId, out perPeerDirty);

            // Build the dirty mask from processingDirtyMask for PRIMITIVES and NODE REFERENCES.
            // Other object properties (INetSerializable) are handled separately - they
            // self-filter. A node reference must be here: it is dirty-gated in the object
            // loop, and this is the ONLY path by which a CHANGED reference enters the send
            // mask - initial sync covers the first value only, resend covers loss only. Left
            // out, the first value shipped and every later assignment (a planet lock released
            // to null, or moved to another body) was silently never sent.
            // Per-peer properties use per-peer dirty mask instead of global
            for (int propIndex = 0; propIndex < 64 && propIndex < _propertyCount; propIndex++)
            {
                if (_propIsObject[propIndex] && !_propIsNodeRef[propIndex]) continue;

                // For per-peer properties, use per-peer dirty mask
                // For broadcast properties, use global processingDirtyMask
                bool isDirty;
                if (_propIsPerPeer[propIndex])
                {
                    // A base (unscoped) write broadcasts to exactly the peers WITHOUT an
                    // override; peers with an override keep their per-peer value.
                    var overrides = network.PerPeerValues?[propIndex];
                    isDirty = (perPeerDirty & (1L << propIndex)) != 0
                        || ((processingDirtyMask & (1L << propIndex)) != 0
                            && (overrides == null || !overrides.ContainsKey(peerId)));
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

            // Snapshot the freshly-dirty bits before the merges below widen the send set.
            // Only these props may be delta-encoded; everything merged in later (initial
            // sync, pending resends, settle absolutes) carries an unchanged value and must
            // be written absolute so it is exact on arrival.
            Array.Copy(_propertiesUpdated, _dirtyOnlyMask, byteCount);

            // Include non-default properties that haven't been synced yet. Node references
            // join the primitives here: their dirty bit is real (MarkDirtyRef sets it), so a
            // late joiner needs the same initial-sync coverage a primitive gets.
            // Byte-wise: ever-dirty ∩ merge-eligible (primitives + node refs; other object
            // props' dirty bits mean nothing) ∩ not-yet-synced. Replaces a per-visit
            // HashSet enumeration with eight AND/OR ops.
            for (var i = 0; i < byteCount; i++)
            {
                var pendingInitial = (byte)((_nonDefaultMask >> (i * BitConstants.BitsInByte))
                    & _initSyncEligibleBytes[i]
                    & ~initialSync[i]);
                _propertiesUpdated[i] |= pendingInitial;
            }

            // Per-peer overrides join initial sync directly: per-peer writes never enter the
            // shared dirty mask, so _nonDefaultMask can't cover them. Without this a
            // peer that joins (or respawns) after its override was written receives nothing.
            if (network.PerPeerPropIndices != null && network.PerPeerValues != null)
            {
                foreach (var propIndex in network.PerPeerPropIndices)
                {
                    if (propIndex >= _propertyCount || _propIsObject[propIndex]) continue;

                    var overrides = network.PerPeerValues[propIndex];
                    if (overrides == null || !overrides.ContainsKey(peerId)) continue;

                    var byteIndex = propIndex / BitConstants.BitsInByte;
                    var propSlot = (byte)(1 << (propIndex % BitConstants.BitsInByte));
                    if ((initialSync[byteIndex] & propSlot) == 0)
                    {
                        _propertiesUpdated[byteIndex] |= propSlot;
                    }
                }
            }

            // Include properties that were sent but not yet acknowledged (for re-sending).
            // Node references ride this too: once they are only sent on change, this is the
            // ONLY thing that recovers a reference lost in flight.
            for (var i = 0; i < state.PendingDirtyMask.Length && i < _propertiesUpdated.Length; i++)
            {
                var pendingByte = state.PendingDirtyMask[i];
                for (int j = 0; j < 8; j++)
                {
                    int propIndex = i * 8 + j;
                    if (propIndex >= _propertyCount) break;
                    if (_propIsObject[propIndex] && !_propIsNodeRef[propIndex]) continue;
                    if ((pendingByte & (1 << j)) != 0)
                    {
                        _propertiesUpdated[i] |= (byte)(1 << j);
                    }
                }
            }

            // SETTLE ABSOLUTE: a property whose last landed encoding included a lossy delta
            // holds a slightly-wrong value on the peer (half-precision rounding). While it
            // keeps changing the stream corrects itself; once it goes quiet nothing would
            // ever fix the residue. Schedule it once more — it is not in _dirtyOnlyMask, so
            // the write loop sends it absolute, and WriteAbsolute clears its LossyMask bit.
            for (var i = 0; i < byteCount; i++)
            {
                var lossyByte = state.LossyMask[i];
                if (lossyByte == 0) continue;
                _propertiesUpdated[i] |= lossyByte;
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
            // DEFER (budget)
            // ============================================================
            // The section can't fit its fixed overhead (header + worst-case mask) plus
            // even the smallest property write. Preserve the would-be-shipped primitive
            // bits in PendingDirtyMask: processingDirtyMask dies at the end of this tick,
            // and a budget-skipped peer would otherwise silently lose those changes
            // forever. They ship absolute on a later tick and clear on its ack, exactly
            // like a loss-recovery resend. Stateful sources merged into the mask above
            // (initial sync, per-peer overrides, lossy settles, existing pending bits)
            // re-derive next tick, so over-merging them here is harmless. Object props
            // keep their own resumable per-peer state and need nothing.
            //
            // Every budget check in this method measures against the WORST-CASE header
            // (mask reuse off, every mask byte nonzero): the mask is only final after the
            // object loop, so any narrower estimate could still grow. The pessimism is
            // bounded (worst minus actual, under 9 bytes for a 64-prop scene) and only
            // ever defers a tail node a tick early; it can never make a section
            // unshippable.
            int worstHeaderBits = _worstCaseHeaderBits;
            if (maxBits < worstHeaderBits + MinPropertyWriteBits)
            {
                for (var i = 0; i < byteCount; i++)
                {
                    var b = _propertiesUpdated[i];
                    if (b == 0) continue;
                    for (var j = 0; j < 8; j++)
                    {
                        if ((b & (1 << j)) == 0) continue;
                        var propIndex = i * 8 + j;
                        if (propIndex >= _propertyCount || _propIsObject[propIndex]) continue;
                        state.PendingDirtyMask[i] |= (byte)(1 << j);
                    }
                }
                return ExportResult.None;
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
            // DECIDE PASS
            // ============================================================
            // Every per-prop encoding decision, computed BEFORE any byte is written.
            //
            // This is the seam the section memo stands on: the per-peer state that can
            // influence the primitive bytes (AckedMask, SentHistory, DeltaChain,
            // _dirtyOnlyMask, the per-peer flag) reaches the writer EXCLUSIVELY through
            // the per-prop useDelta boolean computed here. Everything else the writer
            // reads is shared node-level state (CachedProperties, the baseline ring, the
            // property metadata). So (writtenPrimMask, baselineAge, useDeltaMask) is a
            // complete signature of the primitive segment's bytes for any peer that
            // writes no per-peer-valued and no INetValue primitive.
            //
            // Behavior-neutral versus the old inline computation: the gate inputs are
            // loop-invariant (DeltaChain is per-prop, stamped only by its own write), so
            // deciding everything up front reads the same values the inline gate did.
            long writtenPrimMask = 0;
            long useDeltaMask = 0;
            for (var i = 0; i < byteCount; i++)
            {
                var propSegment = _propertiesUpdated[i];
                if (propSegment == 0) continue;
                for (var j = 0; j < BitConstants.BitsInByte; j++)
                {
                    if ((propSegment & (byte)(1 << j)) == 0) continue;
                    var propIndex = i * BitConstants.BitsInByte + j;
                    if (_propIsObject[propIndex]) continue;
                    writtenPrimMask |= 1L << propIndex;

                    bool gateHasAcked = (state.AckedMask[i] & (1 << j)) != 0;
                    bool gateDirtyThisTick = (_dirtyOnlyMask[i] & (1 << j)) != 0;
                    bool gateSentLastTick = false;
                    if (currentTick >= 1)
                    {
                        ref var prevRecord = ref state.SentHistory[(currentTick - 1) % SNAPSHOT_RING_SIZE];
                        gateSentLastTick = prevRecord.Tick == currentTick - 1
                            && (prevRecord.SentMask & (1L << propIndex)) != 0;
                    }

                    // Delta requires: a resolvable baseline, a confirmed-received prop,
                    // a freshly-changed value mid-streak, and not a per-peer prop (their
                    // values have no shared snapshot). DeltaChain forces a periodic
                    // absolute refresh to bound drift.
                    if (baselineValues != null
                        && gateHasAcked
                        && gateDirtyThisTick
                        && gateSentLastTick
                        && !_propIsPerPeer[propIndex]
                        && _propSupportsDelta[propIndex]
                        && state.DeltaChain[propIndex] < REFRESH_CHAIN)
                    {
                        useDeltaMask |= 1L << propIndex;
                    }
                }
            }

            // ============================================================
            // BODY-FIRST WRITE
            // ============================================================
            // The section is [maskMode:1][age:5][mask unless reused][primitives][objects],
            // padded to a byte. The mask is only final after the object loop, so the body
            // is written into a thread-local scratch at phase 0 and appended after the
            // header once the mask is known - no reserve, no backfill, no memmove. Memo
            // blobs are captured from and served into that scratch, so a hit is a byte
            // memcpy at phase 0 and only the final AppendBits ever shifts.
            var body = BodyScratch;
            body.Reset();

            // Track which properties actually got written (for the wire mask).
            // Reused scratch, not a fresh array: Export runs once per peer per node per
            // tick, so allocating here was one of the largest per-tick GC sources in the
            // netcode. Fully overwritten by the copy below, so no clear is needed.
            byte[] actualMask = _actualMask;
            Array.Copy(_propertiesUpdated, actualMask, byteCount);

            // ============================================================
            // SECTION MEMO (encode once per signature per node per tick)
            // ============================================================
            // Eligibility is signature-determined: a written per-peer-valued primitive
            // (P1) or INetValue primitive (P2) makes the BYTES per-peer, so any such bit
            // in the mask disqualifies every peer with that mask. P3 (whole segment fits
            // this peer's budget) is checked against the candidate entry; P4 (clean
            // leader encode) is enforced at capture. The object properties are untouched
            // by the memo - they run per peer below either way. The header (mask mode,
            // age, mask) is outside the blob and always written per peer.
            bool memoEligible = MemoEnabled && !_memoDisabled
                && writtenPrimMask != 0
                && (writtenPrimMask & (_perPeerPrimMask | _objectValuePrimMask)) == 0;
            int memoHit = -1;
            bool encodeThrew = false;
            if (memoEligible)
            {
                for (int m = 0; m < _memoCount; m++)
                {
                    ref var candidate = ref _memo[m];
                    if (candidate.MaskSig == writtenPrimMask
                        && candidate.UseDeltaSig == useDeltaMask
                        && candidate.Age == (byte)baselineAge
                        && worstHeaderBits + candidate.BlobBits <= maxBits)
                    {
                        memoHit = m;
                        break;
                    }
                }
            }

            // Counted at the LOOKUP, not the serve, so verify mode (which routes hits
            // down the slow path to compare bytes) still reports them as hits — otherwise
            // a verify soak cannot distinguish "zero divergences across N comparisons"
            // from "zero comparisons ever ran".
            var memoCounter = memoHit >= 0
                ? Diagnostics.TickProfiler.Counter.PropsMemoHit
                : !memoEligible
                    ? Diagnostics.TickProfiler.Counter.PropsMemoSlow
                    : _memoCount >= MemoCapacity
                        ? Diagnostics.TickProfiler.Counter.PropsMemoOverflow
                        : Diagnostics.TickProfiler.Counter.PropsMemoMiss;
            Diagnostics.TickProfiler.Current?.Add(memoCounter, 1);

            if (memoHit >= 0 && !VerifyMemoEnabled)
            {
                // FAST PATH: the blob is the primitive body captured from a clean encode
                // with this exact signature this tick, at phase 0. Copy it, then apply the
                // per-peer stamps the writer would have applied. No rewind is possible (P3
                // held), so the rewind-restoration bookkeeping has nothing to do.
                ref var hitEntry = ref _memo[memoHit];
                hitEntry.Blob.AsSpan(0, BytesFor(hitEntry.BlobBits)).CopyTo(body.RawBuffer);
                body.WriteBitPosition = hitEntry.BlobBits;
                ReplayMemoStamps(ref state, writtenPrimMask, useDeltaMask, hitEntry.LossyResultMask);
                MemoHitsForTests++;
            }
            else
            {
                long lossyResultBits = 0;

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

                        int propStartBits = body.WriteBitPosition;
                        // Snapshot the in-write stamps so a budget rewind can restore them -
                        // a rewound property must leave no trace of the aborted encoding.
                        var deltaChainBefore = state.DeltaChain[propIndex];
                        var lossyByteBefore = state.LossyMask[i];
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

                            // Decided in the DECIDE PASS above; see the seam comment there.
                            // Deltas are a stream optimization: only worth it mid-streak, where
                            // the byte savings compound and the settle absolute at the end is
                            // amortized. A one-shot change or a resend of an unchanged value
                            // goes absolute — exact on arrival, no settle follow-up needed.
                            bool useDelta = (useDeltaMask & (1L << propIndex)) != 0;

                            if (useDelta)
                            {
                                // A lossy delta means the peer's reconstruction is now inexact.
                                // A later lossless delta does NOT clear the flag: applied to an
                                // already-drifted base, the result is still drifted. Only an
                                // absolute restores exactness.
                                if (WriteDelta(body, propIndex, ref current, ref baselineValues[propIndex]))
                                {
                                    state.LossyMask[i] |= (byte)(1 << j);
                                    lossyResultBits |= 1L << propIndex;
                                }
                                state.DeltaChain[propIndex]++;
                            }
                            else
                            {
                                WriteAbsolute(currentWorld, peer, body, propIndex, ref current);
                                state.DeltaChain[propIndex] = 0;
                                state.LossyMask[i] &= (byte)~(1 << j);
                            }

                            // Budget: the write pushed the section past maxBits. Rewind it,
                            // restore its stamps, and defer the bit - smaller later
                            // properties may still fit (the mask is written after the body,
                            // so the wire stays consistent).
                            if (worstHeaderBits + body.WriteBitPosition > maxBits)
                            {
                                body.WriteBitPosition = propStartBits;
                                state.DeltaChain[propIndex] = deltaChainBefore;
                                state.LossyMask[i] = lossyByteBefore;
                                actualMask[i] &= (byte)~(1 << j);
                                _leftoverMask[i] |= (byte)(1 << j);
                                lossyResultBits &= ~(1L << propIndex);
                            }
                            else if (Diagnostics.PayloadCensus.Enabled)
                            {
                                var censusProp = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                                Diagnostics.PayloadCensus.Record(
                                    $"{censusProp.NodePath}.{censusProp.Name}",
                                    body.WriteBitPosition - propStartBits, useDelta);
                            }
                            if (TraceWire)
                            {
                                var tp = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                                Debugger.Instance.Log($"[Props.W] idx={propIndex} '{tp.NodePath}.{tp.Name}' type={tp.VariantType} delta={useDelta} bits={body.WriteBitPosition - propStartBits} bodyEnd={body.WriteBitPosition}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                                $"Error serializing property {PropertyNameForLog(propIndex)}: {ex.InnerException?.Message ?? ex.Message}");
                            // Clear the bit AND rewind - stray partial bits would desync the
                            // whole stream for every property and node after this one
                            actualMask[i] &= (byte)~(1 << j);
                            body.WriteBitPosition = propStartBits;
                            encodeThrew = true;
                        }
                    }
                }

                if (memoHit >= 0)
                {
                    // NEBULA_VERIFY_MEMO: this peer matched an entry, and the real writer
                    // just ran anyway. Compare the bits; a divergence means the signature
                    // missed an input, which is the one failure mode this cache cannot
                    // afford - latch the memo off for the whole run and say so loudly.
                    // Both sides are phase-0 with zero pad bits, so byte equality is exact.
                    ref var verifyEntry = ref _memo[memoHit];
                    int writtenBits = body.WrittenBits;
                    bool identical = writtenBits == verifyEntry.BlobBits
                        && body.RawBuffer.AsSpan(0, BytesFor(writtenBits))
                            .SequenceEqual(verifyEntry.Blob.AsSpan(0, BytesFor(verifyEntry.BlobBits)));
                    if (!identical)
                    {
                        _memoDisabled = true;
                        Debugger.Instance.Log(
                            $"[MemoVerify] DIVERGENCE on {_cachedSceneFilePath}: sig=(mask={writtenPrimMask:X}, delta={useDeltaMask:X}, age={baselineAge}) "
                            + $"slow={Convert.ToHexString(body.RawBuffer.AsSpan(0, BytesFor(writtenBits)))}/{writtenBits}b "
                            + $"memo={Convert.ToHexString(verifyEntry.Blob.AsSpan(0, BytesFor(verifyEntry.BlobBits)))}/{verifyEntry.BlobBits}b. "
                            + "Section memo disabled for this run; the signature is missing an input.",
                            Debugger.DebugLevel.ERROR);
                    }
                }
                else if (memoEligible && !encodeThrew && _memoCount < MemoCapacity)
                {
                    // CAPTURE (P4): only a clean encode may seed a shareable entry - a
                    // budget rewind or an exception produced bits that do not match the
                    // signature's promise.
                    bool leftoverClean = true;
                    for (var i = 0; i < byteCount; i++)
                    {
                        if (_leftoverMask[i] != 0) { leftoverClean = false; break; }
                    }
                    if (leftoverClean)
                    {
                        _memo ??= new MemoEntry[MemoCapacity];
                        ref var slot = ref _memo[_memoCount++];
                        slot.MaskSig = writtenPrimMask;
                        slot.UseDeltaSig = useDeltaMask;
                        slot.Age = (byte)baselineAge;
                        int blobBits = body.WrittenBits;
                        int blobBytes = BytesFor(blobBits);
                        if (slot.Blob == null || slot.Blob.Length < blobBytes)
                        {
                            slot.Blob = new byte[Math.Max(blobBytes, 256)];
                        }
                        body.RawBuffer.AsSpan(0, blobBytes).CopyTo(slot.Blob);
                        slot.BlobBits = blobBits;
                        slot.LossyResultMask = lossyResultBits;
                    }
                }
            }

            // Write OBJECT properties (INetSerializable) - always call, they self-filter
            // These return true if they wrote data, false if nothing to send. Their codecs
            // are byte-granular: the scratch auto-aligns on the delegate's first byte
            // write and the reader mirrors it, so a delegate never aligns by hand.
            bool objectDeferred = false;
            for (int propIndex = 0; propIndex < _propertyCount; propIndex++)
            {
                if (!_propIsObject[propIndex]) continue;

                // Check interest for this property
                if (!PeerHasInterestInProperty(propIndex, peerInterestLayers)) continue;

                // A node reference is only ever ASSIGNED, so MarkDirtyRef has already told us
                // whether it changed — unlike an in-place-mutated object, whose dirty bit means
                // nothing and which therefore has to be asked every tick. Gate it on the same
                // merged mask the primitives use, so it inherits initial sync for late joiners
                // and resend-until-acked for loss, and costs nothing while it sits unchanged.
                if (_propIsNodeRef[propIndex]
                    && (_propertiesUpdated[propIndex / BitConstants.BitsInByte]
                        & (1 << (propIndex % BitConstants.BitsInByte))) == 0)
                {
                    continue;
                }

                var classIndex = _propClassIndex[propIndex];
                if (classIndex < 0) continue;

                var serializer = Protocol.GetSerializer(classIndex);
                if (serializer == null) continue;

                // Budget: object serializers cannot be rewound (chunk streams stamp their
                // per-peer frontier state during the write), so one is only invoked while
                // the section still has room for its full chunk budget - the size the
                // serializer is designed to respect - plus the align pad in front of it.
                // A skipped object keeps its own resumable per-peer state and simply
                // resumes on a later tick.
                if (maxBits - (worstHeaderBits + body.WriteBitPosition)
                    < _propChunkBudget[propIndex] * BitConstants.BitsInByte + (BitConstants.BitsInByte - 1))
                {
                    // A dirty NODE-REF skipped here must bank its bit or the change is
                    // lost outright: its dirty mask died in Begin(), CommitExport banks
                    // only SHIPPED bits, and the initial-sync merge is blocked once the
                    // previous value shipped. (Self-filtering objects are fine - their
                    // resumable per-peer state is their own recovery.) The leftover mask
                    // flows into PendingDirtyMask below, and the pending merge re-arms
                    // node-refs next tick.
                    BankNodeRefIfMasked(propIndex);
                    ClearActualMaskBit(actualMask, propIndex);
                    objectDeferred = true;
                    continue;
                }

                // Per-peer array properties serialize this peer's forked instance; everything else
                // (and any peer that has not diverged) serializes the shared base.
                ref var cache = ref ResolveObjectCache(propIndex, peerId);

                // Remember position in case we need to rewind
                int startBits = body.WriteBitPosition;

                try
                {
                    // Object serializers return true if they wrote data
                    bool wroteData = serializer(currentWorld, peer, ref cache, body, _propChunkBudget[propIndex]);

                    if (wroteData)
                    {
                        // Set the bit in the actual mask
                        int byteIdx = propIndex / 8;
                        int bitIdx = propIndex % 8;
                        actualMask[byteIdx] |= (byte)(1 << bitIdx);
                        if (TraceWire)
                        {
                            var tp = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                            Debugger.Instance.Log($"[Props.W] obj idx={propIndex} '{tp.NodePath}.{tp.Name}' bits={body.WriteBitPosition - startBits} bodyEnd={body.WriteBitPosition}");
                        }

                        if (Diagnostics.PayloadCensus.Enabled)
                        {
                            var censusProp = Protocol.UnpackProperty(_cachedSceneFilePath, propIndex);
                            Diagnostics.PayloadCensus.Record(
                                $"{censusProp.NodePath}.{censusProp.Name} (obj)",
                                body.WriteBitPosition - startBits, false);
                        }
                    }
                    else
                    {
                        // Rewind - nothing was written (a delegate that rewound itself with
                        // the byte setter lands here too; both leave no stale bits).
                        body.WriteBitPosition = startBits;
                        // A refused node-ref write (target spawn not yet acked) banks for
                        // the same reason as the budget skip above - the dirty bit is
                        // already consumed and nothing else re-arms a changed value.
                        BankNodeRefIfMasked(propIndex);
                        ClearActualMaskBit(actualMask, propIndex);
                    }
                }
                catch (Exception ex)
                {
                    Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                        $"Error serializing object property {PropertyNameForLog(propIndex)}: {ex.InnerException?.Message ?? ex.Message}");
                    // Rewind on error
                    body.WriteBitPosition = startBits;
                    BankNodeRefIfMasked(propIndex);
                    ClearActualMaskBit(actualMask, propIndex);
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

            // Budget leftovers are delivery-independent: a bit that didn't fit must
            // survive whether or not this section ships, so it merges into the pending
            // machinery here rather than in CommitExport. It re-sends absolute on a
            // later tick and clears on that tick's ack.
            bool hasLeftover = false;
            for (var i = 0; i < byteCount; i++)
            {
                if (_leftoverMask[i] == 0) continue;
                hasLeftover = true;
                state.PendingDirtyMask[i] |= _leftoverMask[i];
            }

            if (!hasAnyData)
            {
                // Nothing to send; nothing has touched the section buffer.

                // SETTLE: an empty section, produced by a clean run, with no latent
                // obligations. Raw masks, not inference - the interest filter can strip
                // merged pending/lossy bits out of the SECTION while the raw masks still
                // hold them, so "the section was empty" alone proves nothing.
                if (!encodeThrew && !objectDeferred && perPeerDirty == 0)
                {
                    bool owes = false;
                    for (var i = 0; i < byteCount; i++)
                    {
                        if ((state.PendingDirtyMask[i] | state.LossyMask[i]) != 0) { owes = true; break; }
                    }
                    if (!owes && !PeerTouchesPollRequiredProps(peerInterestLayers))
                    {
                        state.Settled = true;
                    }
                }
                return ExportResult.None;
            }

            // HEADER, then the body. Mask reuse: when the wire mask equals the one this
            // peer applied at the baseline tick, a single bit replaces the mask. The
            // reference is the WIRE mask stamped in CommitExport (object bits included);
            // the client recorded the same mask when it applied that tick, and a delta
            // already requires that tick to be applied, so the reference is loss-safe.
            long wireMask = MaskToLong(actualMask, byteCount);
            bool maskReuse = false;
            if (baselineAge > 0)
            {
                ref var baseRecord = ref state.SentHistory[state.LatestAckedTick % SNAPSHOT_RING_SIZE];
                maskReuse = baseRecord.Tick == state.LatestAckedTick && baseRecord.WireMask == wireMask;
            }
            int sectionStartBits = buffer.WriteBitPosition;
            buffer.WriteBool(maskReuse);
            buffer.WriteBits((ulong)baselineAge, AgeBits);
            if (!maskReuse)
            {
                PresenceMask.Write(buffer, actualMask.AsSpan(0, byteCount), _propertyCount);
            }
            // Byte-coded values inside the body were aligned to the scratch's phase 0; the body
            // must start on a byte boundary in the FINAL stream too. Only the packet assembler
            // knows that phase, so mark the spot and let it pad there (NetBuffer.MarkAlign);
            // the reader aligns at the same logical point. See _byteCodedMask.
            if ((wireMask & _byteCodedMask) != 0)
            {
                buffer.MarkAlign();
            }
            buffer.AppendBits(body);
            if (TraceWire)
            {
                Debugger.Instance.Log($"[Props.W] mask={Convert.ToHexString(actualMask, 0, byteCount)} reuse={maskReuse} age={baselineAge} bodyBits={body.WrittenBits} sectionBits={buffer.WriteBitPosition - sectionStartBits}");
            }

            // Packet-coupled stamps (SentHistory, shipped-bit pending, initial sync,
            // per-peer dirty clears) apply in CommitExport once the host commits these
            // bytes to the packet.
            return hasLeftover || objectDeferred ? ExportResult.Partial : ExportResult.Written;
        }

        /// <summary>
        /// The primitive body of the section under construction, at phase 0. One per thread
        /// (per-world thread groups export concurrently; a node lives in exactly one world),
        /// reached only through this property so it is created on first use on that thread -
        /// the SpawnSerializer idiom. A per-instance buffer would rent 2 KB per net node.
        /// </summary>
        private const int BodyScratchCapacity = 4096;
        [ThreadStatic] private static NetBuffer _bodyScratch;
        private static NetBuffer BodyScratch => _bodyScratch ??= new NetBuffer(BodyScratchCapacity, usePool: false);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int BytesFor(int bits) => (bits + BitConstants.BitsInByte - 1) / BitConstants.BitsInByte;

        /// <summary>The mask bytes as one 64-bit word, bit i = property i.</summary>
        private static long MaskToLong(byte[] mask, int byteCount)
        {
            long result = 0;
            for (int i = 0; i < byteCount; i++) result |= (long)mask[i] << (i * BitConstants.BitsInByte);
            return result;
        }

        private static void LongToMask(long value, byte[] mask, int byteCount)
        {
            for (int i = 0; i < byteCount; i++) mask[i] = (byte)(value >> (i * BitConstants.BitsInByte));
        }

        // Unaligned numeric forms for the bit stream. The reader mirrors each one.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void PutFloat(NetBuffer buffer, float value) => buffer.WriteBits(BitConverter.SingleToUInt32Bits(value), BitConstants.BitsInInt);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetFloat(NetBuffer buffer) => BitConverter.UInt32BitsToSingle((uint)buffer.ReadBits(BitConstants.BitsInInt));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void PutHalf(NetBuffer buffer, float value) => buffer.WriteBits(BitConverter.HalfToUInt16Bits((Half)value), BitConstants.BitsInShort);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetHalf(NetBuffer buffer) => (float)BitConverter.UInt16BitsToHalf((ushort)buffer.ReadBits(BitConstants.BitsInShort));
        /// <summary>Two halves, matching NetWriter.WriteVector2's precision.</summary>
        private static void PutVector2(NetBuffer buffer, Vector2 v) { PutHalf(buffer, v.X); PutHalf(buffer, v.Y); }
        private static Vector2 GetVector2(NetBuffer buffer) => new(GetHalf(buffer), GetHalf(buffer));
        /// <summary>Three floats, matching NetWriter.WriteVector3's precision.</summary>
        private static void PutVector3(NetBuffer buffer, Vector3 v) { PutFloat(buffer, v.X); PutFloat(buffer, v.Y); PutFloat(buffer, v.Z); }
        private static Vector3 GetVector3(NetBuffer buffer) => new(GetFloat(buffer), GetFloat(buffer), GetFloat(buffer));

        /// <summary>
        /// Banks a node-reference property whose write was skipped or refused while its
        /// merged-mask bit was set. Into the leftover mask, so it rides the existing
        /// leftover-to-pending merge and resends until acked like any primitive.
        /// </summary>
        /// <summary>
        /// The section mask must describe exactly the bytes that follow it. actualMask
        /// starts as a copy of the MERGED mask, which since node references became
        /// mask-gated carries their bits (initial sync, resend, MarkDirtyRef) - so every
        /// object-loop outcome that writes nothing (budget skip, refused write, exception)
        /// has to take the bit back out, or the client reads a value that was never sent
        /// and every property and node after it in the packet is misparsed. This was the
        /// intro-expedition join failure: the player's CurrentPlanet reference is refused
        /// until the planet's spawn is acked, and the stale bit garbled the whole tick.
        /// </summary>
        private static void ClearActualMaskBit(byte[] actualMask, int propIndex)
        {
            actualMask[propIndex / BitConstants.BitsInByte] &= (byte)~(1 << (propIndex % BitConstants.BitsInByte));
        }

        private void BankNodeRefIfMasked(int propIndex)
        {
            if (!_propIsNodeRef[propIndex]) return;
            int byteIdx = propIndex / BitConstants.BitsInByte;
            byte bit = (byte)(1 << (propIndex % BitConstants.BitsInByte));
            if ((_propertiesUpdated[byteIdx] & bit) == 0) return;
            _leftoverMask[byteIdx] |= bit;
        }

        /// <summary>
        /// Applies the per-peer stamps the primitive writer would have applied for a
        /// memo-served section, without running the writer: exactly the DeltaChain and
        /// LossyMask updates at the two write sites, driven by the memoized per-prop
        /// outcomes. Note the asymmetry is deliberate and mirrors the writer - a
        /// non-lossy delta does NOT clear the lossy bit (applied to a drifted base the
        /// result is still drifted); only an absolute clears it.
        /// </summary>
        private static void ReplayMemoStamps(
            ref PeerPropertyState state, long writtenPrimMask, long useDeltaMask, long lossyResultMask)
        {
            long remaining = writtenPrimMask;
            while (remaining != 0)
            {
                int propIndex = System.Numerics.BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
                int byteIdx = propIndex / BitConstants.BitsInByte;
                int bit = propIndex % BitConstants.BitsInByte;
                if ((useDeltaMask & (1L << propIndex)) != 0)
                {
                    if ((lossyResultMask & (1L << propIndex)) != 0)
                    {
                        state.LossyMask[byteIdx] |= (byte)(1 << bit);
                    }
                    state.DeltaChain[propIndex]++;
                }
                else
                {
                    state.DeltaChain[propIndex] = 0;
                    state.LossyMask[byteIdx] &= (byte)~(1 << bit);
                }
            }
        }

        /// <summary>
        /// The section written by the immediately preceding Export was committed to the
        /// tick packet: stamp everything that asserts "these bytes rode tick
        /// <paramref name="tick"/>". PendingDirtyMask and SentHistory only track
        /// PRIMITIVE properties - object properties (INetSerializable) manage their own
        /// per-peer pending/resend state and are acked through their own tick-gated
        /// callbacks. The host always commits a self-limited section, which is what keeps
        /// the in-write stamps (DeltaChain, LossyMask, chunk frontiers) valid in Export.
        /// </summary>
        public void CommitExport(WorldRunner currentWorld, NetPeer peer, Tick tick)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);
            ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(_peerStates, peerId, out bool exists);
            if (!exists || !state.IsInitialized)
            {
                return;
            }
            if (!peerInitialPropSync.TryGetValue(peerId, out var initialSync))
            {
                return;
            }

            long primitiveSentMask = 0;
            long perPeerSentMask = 0;
            long dirtySentMask = 0;
            for (var byteIdx = 0; byteIdx < _byteCount; byteIdx++)
            {
                var b = _actualMask[byteIdx];
                if (b == 0) continue;
                initialSync[byteIdx] |= b;
                // _dirtyOnlyMask is this export's snapshot of freshly-dirty bits; valid here
                // because CommitExport is contractually adjacent to the Export it commits.
                if (byteIdx * 8 < 64)
                    dirtySentMask |= (long)(b & _dirtyOnlyMask[byteIdx]) << (byteIdx * 8);

                for (int bit = 0; bit < 8; bit++)
                {
                    if ((b & (1 << bit)) == 0) continue;
                    int propIdx = byteIdx * 8 + bit;
                    if (propIdx >= _propertyCount) continue;
                    // Node references are banked like primitives; other object properties own
                    // their per-peer resend state inside their own serializer.
                    if (_propIsObject[propIdx] && !_propIsNodeRef[propIdx]) continue;
                    state.PendingDirtyMask[byteIdx] |= (byte)(1 << bit);
                    if (propIdx < 64)
                    {
                        primitiveSentMask |= 1L << propIdx;
                        if (_propIsPerPeer[propIdx])
                        {
                            perPeerSentMask |= 1L << propIdx;
                        }
                    }
                }
            }

            // Clear only the per-peer dirty bits that actually shipped this export.
            // Bits filtered out (interest, budget) stay armed and retry next tick;
            // loss recovery from here on is PendingDirtyMask's job.
            if (perPeerSentMask != 0 && network.PerPeerDirtyMask != null &&
                network.PerPeerDirtyMask.TryGetValue(peerId, out var remainingPerPeerDirty))
            {
                network.PerPeerDirtyMask[peerId] = remainingPerPeerDirty & ~perPeerSentMask;
            }

            // Record what was sent at this tick so a future ack can be matched to exactly
            // the values the peer received (the heart of tick-correlated acknowledgment)
            ref var sentRecord = ref state.SentHistory[tick % SNAPSHOT_RING_SIZE];
            sentRecord.Tick = tick;
            sentRecord.SentMask = primitiveSentMask;
            sentRecord.DirtySentMask = dirtySentMask & primitiveSentMask;
            sentRecord.WireMask = MaskToLong(_actualMask, _byteCount);
        }

        /// <summary>
        /// Writes an absolute property value with its encoding flag byte.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteAbsolute(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, int propIndex, ref PropertyCache current)
        {
            buffer.WriteBits((ulong)DeltaEncodingFlags.Absolute, EncodingBits);
            if (_propQuantStep[propIndex] > 0f)
            {
                if (_propTypes[propIndex] == SerialVariantType.Quaternion)
                {
                    QuantizedCodec.WriteQuat(buffer, current.QuatValue, _propQuantBits[propIndex]);
                }
                else
                {
                    Span<int> codes = stackalloc int[QuantizedCodec.MaxComponents];
                    GridCodes(propIndex, in current, codes);
                    QuantizedCodec.WriteCodes(buffer, codes, _propQuantComponents[propIndex]);
                }
                return;
            }

            // Quaternion: always smallest-three; the reader knows the type, so no flag.
            if (_propTypes[propIndex] == SerialVariantType.Quaternion)
            {
                QuantizedCodec.WriteQuat(buffer, current.QuatValue, QuantizedCodec.UnquantizedQuatBits);
                return;
            }
            WriteAbsoluteValue(currentWorld, peer, buffer, propIndex, ref current);
        }

        /// <summary>
        /// Mirrors the client's ReadSmallDelta reconstruction for one component: the wire
        /// carries (Half)delta and the client adds (float)(Half)delta to its recorded
        /// baseline. True when that reconstruction lands exactly on the current value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool HalfDeltaIsLossless(float baseline, float delta, float current)
            => baseline + (float)(Half)delta == current;

        /// <summary>
        /// Mirrors the client's ReadFullDelta reconstruction for one component. Full deltas
        /// carry float32, but baseline + delta can still round away from the current value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool FullDeltaIsLossless(float baseline, float delta, float current)
            => baseline + delta == current;

        /// <summary>
        /// Writes a property value as a delta against the snapshot value at the peer's
        /// acked baseline tick. Caller guarantees the type supports delta encoding.
        /// Returns true when the encoding was LOSSY — the peer's reconstruction will not
        /// exactly equal the current value — so the caller can flag the property for a
        /// settle absolute once it stops changing. Integer deltas are always exact (the
        /// client mirrors the same wrapping arithmetic), so only float-family types can
        /// report lossy.
        /// </summary>
        private bool WriteDelta(
            NetBuffer buffer,
            int propIndex,
            ref PropertyCache current,
            ref PropertyCache baseline)
        {
            var type = _propTypes[propIndex];

            if (_propQuantStep[propIndex] > 0f)
            {
                // Integer step-count delta against the canonical ring value: exact by
                // construction (the client adds it to Quantize(its identical baseline)),
                // so never lossy. Small packed word when every component fits, else the
                // magnitude form - TryWriteSmallDelta writes nothing when it declines.
                int count = _propQuantComponents[propIndex];
                Span<int> codes = stackalloc int[QuantizedCodec.MaxComponents];
                Span<int> baseCodes = stackalloc int[QuantizedCodec.MaxComponents];
                GridCodes(propIndex, in current, codes);
                GridCodes(propIndex, in baseline, baseCodes);
                for (int k = 0; k < count; k++) codes[k] -= baseCodes[k];

                int flagBits = buffer.WriteBitPosition;
                buffer.WriteBits((ulong)DeltaEncodingFlags.DeltaSmall, EncodingBits);
                if (!QuantizedCodec.TryWriteSmallDelta(buffer, codes, count))
                {
                    buffer.WriteBitPosition = flagBits;
                    buffer.WriteBits((ulong)DeltaEncodingFlags.DeltaFull, EncodingBits);
                    QuantizedCodec.WriteCodes(buffer, codes, count);
                }
                return false;
            }

            switch (type)
            {
                case SerialVariantType.Float:
                    float deltaF = current.FloatValue - baseline.FloatValue;
                    if (MathF.Abs(deltaF) < SmallDeltaThreshold)
                    {
                        buffer.WriteBits((ulong)DeltaEncodingFlags.DeltaSmall, EncodingBits);
                        PutHalf(buffer, deltaF);
                        return !HalfDeltaIsLossless(baseline.FloatValue, deltaF, current.FloatValue);
                    }
                    else
                    {
                        buffer.WriteBits((ulong)DeltaEncodingFlags.DeltaFull, EncodingBits);
                        PutFloat(buffer, deltaF);
                        return !FullDeltaIsLossless(baseline.FloatValue, deltaF, current.FloatValue);
                    }

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
                        buffer.WriteBits((ulong)DeltaEncodingFlags.DeltaSmall, EncodingBits);
                        buffer.WriteBits((ushort)(short)deltaL, BitConstants.BitsInShort);
                    }
                    else
                    {
                        // Full delta - write appropriate size based on width
                        buffer.WriteBits((ulong)DeltaEncodingFlags.DeltaFull, EncodingBits);
                        switch (intWidth)
                        {
                            case IntWidth.Byte:
                            case IntWidth.Int16:
                            case IntWidth.UInt16:
                                buffer.WriteBits((ushort)(short)deltaL, BitConstants.BitsInShort);
                                break;
                            case IntWidth.Int32:
                            case IntWidth.UInt32:
                                buffer.WriteBits((uint)(int)deltaL, BitConstants.BitsInInt);
                                break;
                            default:
                                buffer.WriteBits((ulong)deltaL, BitConstants.BitsInLong);
                                break;
                        }
                    }
                    return false;

                case SerialVariantType.Vector2:
                    Vector2 deltaV2 = current.Vec2Value - baseline.Vec2Value;
                    float mag2 = deltaV2.LengthSquared();
                    if (mag2 < SmallDeltaThresholdSq)
                    {
                        buffer.WriteBits((ulong)DeltaEncodingFlags.DeltaSmall, EncodingBits);
                        PutHalf(buffer, deltaV2.X);
                        PutHalf(buffer, deltaV2.Y);
                        return !(HalfDeltaIsLossless(baseline.Vec2Value.X, deltaV2.X, current.Vec2Value.X)
                            && HalfDeltaIsLossless(baseline.Vec2Value.Y, deltaV2.Y, current.Vec2Value.Y));
                    }
                    else
                    {
                        buffer.WriteBits((ulong)DeltaEncodingFlags.DeltaFull, EncodingBits);
                        PutVector2(buffer, deltaV2);
                        return !(FullDeltaIsLossless(baseline.Vec2Value.X, deltaV2.X, current.Vec2Value.X)
                            && FullDeltaIsLossless(baseline.Vec2Value.Y, deltaV2.Y, current.Vec2Value.Y));
                    }

                case SerialVariantType.Vector3:
                    Vector3 deltaV3 = current.Vec3Value - baseline.Vec3Value;
                    float mag3 = deltaV3.LengthSquared();
                    if (mag3 < SmallDeltaThresholdSq)
                    {
                        buffer.WriteBits((ulong)DeltaEncodingFlags.DeltaSmall, EncodingBits);
                        PutHalf(buffer, deltaV3.X);
                        PutHalf(buffer, deltaV3.Y);
                        PutHalf(buffer, deltaV3.Z);
                        return !(HalfDeltaIsLossless(baseline.Vec3Value.X, deltaV3.X, current.Vec3Value.X)
                            && HalfDeltaIsLossless(baseline.Vec3Value.Y, deltaV3.Y, current.Vec3Value.Y)
                            && HalfDeltaIsLossless(baseline.Vec3Value.Z, deltaV3.Z, current.Vec3Value.Z));
                    }
                    else
                    {
                        buffer.WriteBits((ulong)DeltaEncodingFlags.DeltaFull, EncodingBits);
                        PutVector3(buffer, deltaV3);
                        return !(FullDeltaIsLossless(baseline.Vec3Value.X, deltaV3.X, current.Vec3Value.X)
                            && FullDeltaIsLossless(baseline.Vec3Value.Y, deltaV3.Y, current.Vec3Value.Y)
                            && FullDeltaIsLossless(baseline.Vec3Value.Z, deltaV3.Z, current.Vec3Value.Z));
                    }

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
                    buffer.WriteBool(cache.BoolValue);
                    break;
                case SerialVariantType.Int:
                    // Sized integer types (enums, byte, short, int, long). Must mirror
                    // ReadAbsoluteValue's Int case exactly - a width mismatch misaligns
                    // every value after this one in the packet.
                    switch (_propIntWidth[propIndex])
                    {
                        case IntWidth.Byte:
                            buffer.WriteBits(cache.ByteValue, BitConstants.BitsInByte);
                            break;
                        case IntWidth.Int16:
                            buffer.WriteBits((ushort)(short)cache.IntValue, BitConstants.BitsInShort);
                            break;
                        case IntWidth.UInt16:
                            buffer.WriteBits((ushort)cache.IntValue, BitConstants.BitsInShort);
                            break;
                        case IntWidth.Int32:
                            buffer.WriteBits((uint)cache.IntValue, BitConstants.BitsInInt);
                            break;
                        case IntWidth.UInt32:
                            buffer.WriteBits((uint)cache.IntValue, BitConstants.BitsInInt);
                            break;
                        default:
                            // Int64: long, ulong, or an unrecognised subtype
                            buffer.WriteBits((ulong)cache.LongValue, BitConstants.BitsInLong);
                            break;
                    }
                    break;
                case SerialVariantType.Float:
                    PutFloat(buffer, cache.FloatValue);
                    break;
                case SerialVariantType.String:
                    NetWriter.WriteString(buffer, cache.StringValue ?? "");
                    break;
                case SerialVariantType.Vector2:
                    PutVector2(buffer, cache.Vec2Value);
                    break;
                case SerialVariantType.Vector3:
                    PutVector3(buffer, cache.Vec3Value);
                    break;
                case SerialVariantType.Quaternion:
                    QuantizedCodec.WriteQuat(buffer, cache.QuatValue, QuantizedCodec.UnquantizedQuatBits);
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

        /// <summary>
        /// Resolves the cache slot an object property should serialize for one peer. Per-peer array
        /// properties (NetProperty.PerPeerState) keep a forked instance per diverged peer in
        /// PerPeerValues; every other object property, and any peer that has not diverged, uses the
        /// shared base in CachedProperties. Returns by ref and never allocates.
        /// </summary>
        private ref PropertyCache ResolveObjectCache(int propIndex, UUID peerId)
        {
            if (_propIsPerPeer[propIndex] && network.PerPeerValues != null)
            {
                var peerValues = network.PerPeerValues[propIndex];
                if (peerValues != null)
                {
                    ref var perPeer = ref CollectionsMarshal.GetValueRefOrNullRef(peerValues, peerId);
                    if (!Unsafe.IsNullRef(ref perPeer) && perPeer.RefValue != null)
                    {
                        return ref perPeer;
                    }
                }
            }
            return ref network.CachedProperties[propIndex];
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

                // Forked per-peer instances carry their own global dirty set and are invisible to
                // the base slot above. Skipping them would leave a fork's _dirtyMask set forever,
                // so it would re-merge the same bits into its pending queue every single tick.
                if (!_propIsPerPeer[i] || network.PerPeerValues == null) continue;
                var peerValues = network.PerPeerValues[i];
                if (peerValues == null) continue;
                foreach (var entry in peerValues)
                {
                    if (entry.Value.RefValue is INetExportAware forkExportAware)
                    {
                        forkExportAware.OnExportComplete();
                    }
                }
            }
        }

        /// <summary>
        /// Removes all cached data for a specific peer. Call this when a peer disconnects.
        /// Returns the PeerPropertyState to the pool for reuse.
        /// </summary>
        /// <summary>
        /// Forget everything ever sent to this peer, because the client is about to rebuild
        /// this node from scratch (spawn or interest-regain respawn). The fresh client-side
        /// serializer starts with an empty applied ring, so any delta baseline retained here
        /// would point at a tick the client can never resolve - the next payload after a
        /// respawn must be fully absolute, and initial sync must re-ship non-default values.
        /// </summary>
        public void ResetPeerBaseline(UUID peerId)
        {
            peerInitialPropSync.Remove(peerId);

            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(_peerStates, peerId);
            if (!Unsafe.IsNullRef(ref state) && state.IsInitialized)
            {
                ClearPeerState(ref state);
            }
        }

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

                // Route to the peer's forked instance when it has one - the base array holds no
                // state for a diverged peer (ForkForPeer moved it across).
                ref var cache = ref ResolveObjectCache(i, peerId);
                if (cache.RefValue != null)
                {
                    onDisconnected(cache.RefValue, peerId);
                }
            }
        }

        public void Acknowledge(WorldRunner currentWorld, NetPeer peer, Tick latestAck)
        {
            var peerId = NetRunner.Instance.GetPeerId(peer);

            // Zero-alloc ref access. ExportCore creates this state before it writes a single
            // byte and CommitExport refuses to stamp without it, so "no state" means no
            // section of this node has ever been committed to this peer: nothing to ack.
            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(_peerStates, peerId);
            if (Unsafe.IsNullRef(ref state) || !state.IsInitialized)
            {
                return;
            }

            // An ack for tick N proves the peer received the packet exported at N, which
            // contained the tick-N value of every then-pending property. It proves nothing
            // about sends at later ticks, so only the tick-N record is committed.
            //
            // The record gates the OBJECT properties too: their bytes only ever ship inside a
            // committed props section, and CommitExport stamps this record for every committed
            // section, object-only ones included (primitiveSentMask == 0). No record means no
            // section rode tick N, so there is nothing for an array to commit - and skipping
            // the loop is what makes an ack cost nothing for a node that only shipped a spawn
            // or resync byte that tick. Consequence for an ack older than the 32-tick history
            // (slot overwritten): the object loop no longer runs where it used to. Object
            // properties resend every tick until acked, so the next in-range ack clears them.
            ref var ackedRecord = ref state.SentHistory[latestAck % SNAPSHOT_RING_SIZE];
            if (ackedRecord.Tick != latestAck)
            {
                return;
            }

            // Call OnPeerAcknowledge on all object properties (they gate on the tick themselves)
            for (int i = 0; i < _propertyCount; i++)
            {
                if (!_propIsObject[i]) continue;

                var classIndex = _propClassIndex[i];
                if (classIndex < 0) continue;

                var onAck = Protocol.GetOnPeerAcknowledge(classIndex);
                if (onAck == null) continue;

                // Must resolve per peer: an ack delivered to the base array while this peer is
                // served by a fork would never clear the fork's pending mask, so it would resend
                // the same delta forever.
                ref var cache = ref ResolveObjectCache(i, peerId);
                if (cache.RefValue != null)
                {
                    onAck(cache.RefValue, peerId, latestAck);
                }
            }

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

                // Stop resending only props whose value did NOT change again after the
                // acked tick - a later DIRTY send carries a newer value the client may not
                // have yet. A later RESEND of the same value does not (see DirtySentMask).
                long laterSent = 0;
                for (int i = 0; i < state.SentHistory.Length; i++)
                {
                    if (state.SentHistory[i].Tick > latestAck)
                    {
                        laterSent |= state.SentHistory[i].DirtySentMask;
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

    }
}
