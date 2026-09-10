namespace Nebula.Serialization.Serializers
{
    /// <summary>
    /// Result of a server-side <see cref="IStateSerializer.Export"/> call.
    /// </summary>
    public enum ExportResult : byte
    {
        /// <summary>Nothing was written.</summary>
        None,

        /// <summary>
        /// Everything the serializer wanted to send was written. A self-limiting
        /// serializer guarantees the section is within maxBits; an atomic serializer
        /// may have exceeded it, in which case the host drops the bytes and never calls
        /// CommitExport.
        /// </summary>
        Written,

        /// <summary>
        /// The serializer self-limited to maxBits and has more data queued for this
        /// peer. The host uses this to place the round-robin cursor.
        /// </summary>
        Partial,
    }

    /// <summary>
    /// Defines an object which the server utilizes to serialize and send data to the client,
    /// and the client can then receive and deserialize from the server.
    /// </summary>
    public interface IStateSerializer
    {
        public void Begin();

        /// <summary>
        /// Rotation pre-gate: true when this serializer provably owes the peer nothing
        /// this tick, letting the host skip its per-node bookkeeping and the Export call
        /// entirely. Default false - only serializers that track per-peer settledness
        /// (the props serializer) ever say yes.
        /// </summary>
        public bool NothingForPeer(UUID peerId) => false;

        /// <summary>
        /// Client-side only. Receive and deserialize binary received from the server.
        /// </summary>
        /// <param name="currentWorld">The current world runner</param>
        /// <param name="data">The network buffer containing serialized data</param>
        /// <param name="nodeOut">Output network controller</param>
        /// <returns>
        /// True if the payload was fully applied. False if it was parsed but (partly)
        /// discarded — the stream is still aligned, but the caller must NOT ack this
        /// tick: an ack tells the server "I have this tick's data", and acking a
        /// discarded payload permanently latches delta encoding onto a baseline the
        /// client never recorded.
        /// </returns>
        public bool Import(WorldRunner currentWorld, NetBuffer data, out NetworkController nodeOut);

        /// <summary>
        /// Server-side only. Serialize and write data to the provided buffer.
        /// Writes nothing if there's no data to export.
        ///
        /// Budget contract: <paramref name="maxBits"/> is the BIT budget for this
        /// section (packets are bit-packed; sections concatenate at bit granularity).
        /// maxBits &lt;= 0 means "deferred this tick": write NOTHING, but
        /// preserve any would-be-sent data for a later tick (e.g. merge dirty bits into
        /// a pending mask) and still run delivery-independent state transitions.
        ///
        /// Side-effect contract: packet-coupled state — anything asserting "these bytes
        /// rode this tick's packet" (send-tick windows, sent-history records, dirty-bit
        /// clears) — must NOT be stamped here. It belongs in <see cref="CommitExport"/>,
        /// which the host calls iff the bytes from the immediately preceding Export on
        /// this instance were committed to the packet. An atomic serializer (one that
        /// cannot split its record) may write more than maxBits; the host then discards
        /// the bits and never calls CommitExport — sound only because of this rule.
        /// A self-limiting serializer must never exceed maxBits; the host always
        /// commits what it wrote.
        /// </summary>
        /// <param name="currentWorld">The current world runner</param>
        /// <param name="peer">The target peer</param>
        /// <param name="buffer">Buffer to write serialized data into</param>
        /// <param name="maxBits">Bit budget for this section (see contract above)</param>
        public ExportResult Export(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, int maxBits);

        /// <summary>
        /// Server-side only. The bytes written by the immediately preceding
        /// <see cref="Export"/> on this instance were committed to the tick packet being
        /// assembled — stamp packet-coupled state here (send windows, sent history).
        /// Temporal contract: the host calls this before any other Export on the same
        /// instance, on the same world tick thread, so instance scratch captured during
        /// Export is still valid.
        /// </summary>
        public void CommitExport(WorldRunner currentWorld, NetPeer peer, Tick tick) { }

        /// <summary>
        /// Server-side only. Called when a peer acknowledges the packet exported at
        /// <paramref name="tick"/>. Implementations must only commit state that was sent
        /// at or before that tick.
        ///
        /// Delivery contract: the host only calls this for ticks in which this node had a
        /// section committed into the peer's packet (or, for a nested scene, rode an
        /// ancestor's spawn table) - see <c>WorldRunner.PeerAcknowledge</c> and
        /// <c>SentNodeRing</c>. An implementation must therefore stamp every "sent at T"
        /// record from <see cref="CommitExport"/> (or from inside a write the host will
        /// commit), never from a path that produces no bytes: a stamp with no section is a
        /// stamp no ack can ever reach.
        /// </summary>
        public void Acknowledge(WorldRunner currentWorld, NetPeer peer, Tick tick);

        public void Cleanup();
        
        /// <summary>
        /// Server-side only. Called when a peer disconnects to clean up any per-peer cached data.
        /// This prevents memory leaks from accumulating peer-specific state.
        /// </summary>
        /// <param name="peerId">The UUID of the disconnecting peer</param>
        public void CleanupPeer(UUID peerId) { }

        /// <summary>
        /// Server-side only. Called when the client is about to receive a fresh copy of this
        /// node (a spawn or interest-regain respawn). The client rebuilds the node from
        /// scratch, so any per-peer delta/ack baseline held here describes state the new
        /// client-side instance does not have and must be forgotten — otherwise the next
        /// export deltas against a tick the client can never resolve.
        /// </summary>
        /// <param name="peerId">The UUID of the peer being (re)sent this node</param>
        public void ResetPeerBaseline(UUID peerId) { }
    }
}
