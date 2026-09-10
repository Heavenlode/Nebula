using System;

namespace Nebula
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class NetProperty : Attribute
    {
        public enum SyncFlags
        {
            LinearInterpolation = 1 << 0,
            LossyConsistency = 1 << 1,
        }

        public SyncFlags Flags;
        public long InterestMask = long.MaxValue;
        public long InterestRequired = 0;

        /// <summary>
        /// When true, the source generator will emit a virtual OnNetworkChange{PropertyName} method
        /// that you can override to handle property changes. This provides compile-time type safety
        /// and zero-allocation change notifications.
        /// </summary>
        public bool NotifyOnChange = false;

        /// <summary>
        /// When true, the source generator will emit a virtual Interpolate{PropertyName} method
        /// that smoothly interpolates this property toward network values each frame.
        /// The property value is not set immediately on network receive; instead it lerps toward the target.
        /// </summary>
        public bool Interpolate = false;

        /// <summary>
        /// Speed of interpolation when Interpolate = true. Higher = faster catch-up.
        /// Typical values: 10-20 for responsive feel, 5-10 for smooth feel.
        /// </summary>
        public float InterpolateSpeed = 15f;

        /// <summary>
        /// When true, this property participates in client-side prediction.
        /// The generator will emit snapshot/restore methods for rollback.
        /// You MUST define a {PropertyName}PredictionTolerance property (float) to specify
        /// the tolerance for misprediction detection, or a compile error (NEBULA002) will occur.
        /// Only meaningful on client for owned entities.
        /// </summary>
        public bool Predicted = false;

        /// <summary>
        /// Maximum bytes per tick for chunked initial sync of NetArray properties.
        /// When a new client joins, large arrays are synced gradually across multiple ticks
        /// to avoid bandwidth spikes. Default: 256 bytes per tick.
        /// Only applicable to NetArray&lt;T&gt; properties.
        /// </summary>
        public int ChunkBudget = 256;

        /// <summary>
        /// When true, this property holds a distinct value per peer. Useful when each peer sees
        /// a unique value, such as quest state or per-player interactability.
        ///
        /// The property MUST be declared <c>partial</c> (NEBULA006) — the generator implements
        /// per-peer-aware accessors. Because partial property definitions cannot carry
        /// initializers, move any non-default initial value into the constructor.
        ///
        /// Semantics:
        /// <list type="bullet">
        /// <item>Server, inside <c>using (Network.ForPeer(peerId))</c>: gets and sets target that
        /// peer's value. Gets fall back to the base value when the peer has no override. Sets
        /// always deliver — there is deliberately no equality short-circuit, so writing a value
        /// one peer already holds still reaches the peer in scope. The scope is lexical and
        /// process-wide: ANY per-peer property on ANY node accessed inside it targets that peer.</item>
        /// <item>Server, outside any scope: accessors target the base value. A base write
        /// broadcasts to exactly the peers WITHOUT an override; peers with overrides keep theirs.
        /// A joining peer with no override receives no wire value and uses the constructor
        /// default — keep constructor defaults identical on server and client.</item>
        /// <item>Client: behaves as a plain property, written by import. NotifyOnChange and
        /// NetChangeListener fire as usual.</item>
        /// </list>
        ///
        /// Restrictions: primitive value types (int, bool, long, float, enums, Vector*, UUID,
        /// NetId, ...) and <c>NetArray&lt;T&gt;</c>. Other reference types, string and
        /// INetSerializable are rejected (NEBULA007), as is combining with Predicted or
        /// Interpolate (NEBULA008). Per-peer values are always sent absolute (never
        /// delta-encoded). Per-peer state is cleaned up on disconnect; a peer that merely leaves
        /// a world keeps its entries on that world's nodes until the world is torn down.
        ///
        /// <para><b>NetArray properties</b> follow the same scoping but differ in three ways,
        /// because a collection is mutated in place rather than assigned:</para>
        /// <list type="bullet">
        /// <item>The server keeps a whole <c>NetArray&lt;T&gt;</c> instance per diverged peer.
        /// The getter forks one from the base on the first access inside a scope — and since the
        /// getter cannot tell <c>var x = Arr[3]</c> from <c>Arr[3] = x</c>, ANY scoped access
        /// forks, including a read. The fork is content-identical and inherits the peer's sync
        /// state, so it costs a server-side allocation but no bandwidth. Keep scoped access out
        /// of hot loops.</item>
        /// <item>Memory is O(peers × array). A 4096-element array across 64 diverged peers is
        /// ~256 KB on this node alone. Only peers that actually diverge pay.</item>
        /// <item><b>Once a peer is forked it is divorced from the base</b>: later base mutations
        /// never reach it. This differs from the primitive contract above, where a base write
        /// still broadcasts to every peer without an override — there is no cheap base+override
        /// merge for a collection. Write per-peer arrays through the scope from then on.</item>
        /// </list>
        /// </summary>
        public bool PerPeerState = false;

        /// <summary>
        /// Grid step for wire quantization, in the property's own units; 0 (default) keeps the
        /// float/half-float encoding. Only float, Vector2, Vector3 and Quaternion may set it
        /// (NEBULA010). The value is rounded to the nearest multiple of the step on the server
        /// and replicated as integer step counts, so every delta is exact: the client
        /// reconstructs the server's grid value bit for bit and no lossy "settle" absolute is
        /// ever needed. A change smaller than one step is not sent at all (server-side
        /// dead-band). For a Quaternion the step is per smallest-three component and resolves
        /// to a bit width (at most 10 bits per component). Cannot combine with PerPeerState.
        /// Wire encoding, so it is part of the protocol hash.
        /// </summary>
        public float Quantize = 0f;

        /// <summary>
        /// Vector3 only: the value is a unit direction. It is replicated as an octahedral
        /// projection (two components in [-1, 1]) instead of three coordinates, and
        /// <see cref="Quantize"/> is the step in those octahedral units (roughly the angular
        /// resolution in radians / 2). Requires Quantize > 0. A zero-length input is not a
        /// direction and is sent as +Y, so properties that use Vector3.Zero as a sentinel must
        /// not set this. Renormalised on decode.
        /// </summary>
        public bool UnitVector = false;
    }
}