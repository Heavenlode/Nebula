namespace Nebula.Testing.Load
{
    /// <summary>
    /// The rules that decide, per prediction tick, whether an input packet goes out and whether it
    /// carries the pending tick acknowledgement.
    ///
    /// <para>Pulled out as a pure struct with no ENet and no Godot in it so the rules can be tested
    /// directly. They are worth testing: getting them wrong does not break the run, it quietly
    /// changes the inbound packet rate the server sees — which is the thing being measured. A
    /// synthetic peer that sent input on every tick would inflate the server's inbound work by 4x
    /// against a real client that mostly holds its keys.</para>
    ///
    /// <para>Mirrors <c>WorldRunner.SendInput</c>. The two rules there:
    /// <list type="bullet">
    /// <item>unchanged input is suppressed EXCEPT every fourth tick, so a lost packet carrying the
    /// last change cannot leave the server replaying stale keys forever;</item>
    /// <item>the acknowledgement rides the FIRST input packet of a prediction tick and no other,
    /// because the ack is per peer while <c>SendInput</c> runs per owned node.</item>
    /// </list></para>
    /// </summary>
    public struct SyntheticInputCadence
    {
        /// <summary>
        /// Keepalive period. A node whose input has not changed still sends when
        /// <c>tick &amp; KeepaliveMask</c> is zero — every fourth tick, matching
        /// <c>(_clientPredictedTick &amp; 3) != 0</c> in SendInput.
        /// </summary>
        public const int KeepaliveMask = 3;

        private bool _ackAttachedThisTick;

        /// <summary>Call once at the top of each prediction tick, before any node is considered.</summary>
        public void BeginTick() => _ackAttachedThisTick = false;

        /// <summary>Whether a node's input packet goes out on this tick.</summary>
        public static bool ShouldSend(bool inputChanged, int predictedTick)
            => inputChanged || (predictedTick & KeepaliveMask) == 0;

        /// <summary>
        /// Whether this packet carries the pending ack, and claims it if so. Only the first packet
        /// of a tick can — the rest of that tick's nodes send none.
        /// </summary>
        public bool TryClaimAck(int pendingAckTick)
        {
            if (pendingAckTick < 0 || _ackAttachedThisTick) return false;
            _ackAttachedThisTick = true;
            return true;
        }

        /// <summary>True once some packet this tick has taken the ack.</summary>
        public readonly bool AckAttachedThisTick => _ackAttachedThisTick;
    }
}
