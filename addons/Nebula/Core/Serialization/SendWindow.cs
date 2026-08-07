namespace Nebula.Serialization
{
    /// <summary>
    /// Tracks the contiguous run of ticks on which a resend-until-acked record
    /// (spawn/despawn marker) actually rode a packet to one peer. The ack commit rule is
    /// <see cref="Covers"/>: an acked tick proves delivery only if the record was in that
    /// tick's packet, and with budget splitting a record can be deferred on any tick.
    /// <see cref="RecordSend"/> therefore restarts the window whenever a gap appears —
    /// an ack for a pre-gap send then simply doesn't commit (one extra resend round),
    /// which is always safe; a false commit never is.
    /// Ticks start at 1, so First == 0 means "never sent".
    /// </summary>
    internal struct SendWindow
    {
        public Tick First;
        public Tick Last;

        /// <summary>
        /// Records that the record rode the packet exported at <paramref name="tick"/>.
        /// Consecutive sends extend the window; any skipped tick restarts it.
        /// </summary>
        public void RecordSend(Tick tick)
        {
            if (First == 0 || tick > Last + 1)
            {
                First = tick;
            }
            Last = tick;
        }

        /// <summary>
        /// True if the record was provably included in the packet exported at
        /// <paramref name="ackedTick"/>.
        /// </summary>
        public readonly bool Covers(Tick ackedTick)
            => First != 0 && ackedTick >= First && ackedTick <= Last;
    }
}
