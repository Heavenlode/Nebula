namespace Nebula.Serialization
{
    /// <summary>
    /// Everything the export phase writes for ONE peer: the packet being assembled, the ack ring,
    /// the props round-robin cursor and the tallies the metrics replay once every peer has run.
    ///
    /// One object per peer, created on the tick thread in ExportState's prologue (never from
    /// JoinPeer, which runs on main while this world may be mid-export) and dropped with the rest
    /// of the peer's state in TeardownPeer/ExitPeer. A lane writes only its own peer's object, so
    /// no world-level dictionary is written while peers export - which is what lets several peers
    /// export at once.
    /// </summary>
    internal sealed class PeerExport
    {
        /// <summary>The tick packet for this peer; pooled, Reset() before each export.</summary>
        public readonly NetBuffer Packet = new();

        /// <summary>Which nodes had a section in each recent tick's packet (ack routing).</summary>
        public readonly SentNodeRing Ring = new();

        /// <summary>
        /// Props phase round-robin cursor: the NetId of the next shared node owed property
        /// service. Without it, whichever nodes iterate first would monopolize every
        /// budget-limited packet and later nodes would starve.
        /// </summary>
        public long PropsCursor;
        public bool HasPropsCursor;

        /// <summary>True once this tick's packet has been assembled; the send loop skips the rest.</summary>
        public bool Exported;

        // Recorded by the lane, replayed into ServerMetrics by the tick thread after the join, in
        // the same call sequence the serial loop used to make, so the metrics output is unchanged.
        public int UsedBytes;
        public int BudgetBytes;
        public int SpawnDeferred;
        public int PropsDeferred;
        public int OwnedSpawnDeferred;
        public int OwnedPropsDeferred;
        public int SpawningCount;
    }
}
