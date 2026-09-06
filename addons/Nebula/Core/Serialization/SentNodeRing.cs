using System.Collections.Generic;

namespace Nebula.Serialization
{
    /// <summary>
    /// Per-peer record of which nodes had a section committed into each recent tick's packet,
    /// so an acknowledgement for tick T visits exactly the nodes whose bytes rode packet T.
    ///
    /// <para>This replaces a per-peer "pending acks" set that every ack walked in full. That
    /// set only ever grew: a node with object properties never left it, so at 40 peers every
    /// ack (one per input packet, ~30/s/peer) called <c>Acknowledge</c> on every serializer of
    /// every node the peer had ever been sent. Every serializer's <c>Acknowledge</c> only acts
    /// on ticks in which it committed bytes (spawn <c>SendWindow.Covers</c>, props
    /// <c>SentHistory[T].Tick == T</c>, and object properties only ship inside a committed
    /// props section), so visiting anything else was pure cost.</para>
    ///
    /// <para>Slots are indexed by <c>tick % Depth</c> and stamped with their tick, so a slot
    /// that has wrapped is never mistaken for the requested one. Lists are cleared and reused,
    /// never reallocated, so steady state is allocation-free.</para>
    /// </summary>
    internal sealed class SentNodeRing
    {
        /// <summary>
        /// How many ticks back an acknowledgement can still be matched to its packet, ~2 s at
        /// 30 TPS. Nothing else in the system acts on an older ack: the props sent-history is
        /// 32 deep (and MAX_DELTA_AGE is 30), so 64 is a 2x margin over the oldest ack any
        /// consumer can use. An ack older than this is ignored, which
        /// costs one extra resend round for a spawn window or an object property's pending
        /// set - both resend every tick until acked. The 30 s join window is covered by that
        /// resend-until-acked rule, not by ack depth, so it does not size this.
        /// </summary>
        public const int Depth = 64;

        /// <summary>Stamp meaning "slot never written"; ticks start at 0, so -1 cannot collide.</summary>
        private const int EmptyStamp = -1;

        private readonly Tick[] _stamps = new Tick[Depth];
        private readonly List<NetworkController>[] _slots = new List<NetworkController>[Depth];

        public SentNodeRing() => Reset();

        /// <summary>
        /// Claims the slot for <paramref name="tick"/>: stamps it and empties its list. Called
        /// once per peer per tick at the start of that peer's export.
        /// </summary>
        public void Begin(Tick tick)
        {
            int idx = SlotOf(tick);
            _stamps[idx] = tick;
            var list = _slots[idx];
            if (list == null)
            {
                _slots[idx] = new List<NetworkController>(64);
            }
            else
            {
                list.Clear();
            }
            _currentIdx = idx;
        }

        private int _currentIdx = -1;

        /// <summary>Registers a node whose section was committed into the tick begun by <see cref="Begin"/>.</summary>
        public void Add(NetworkController node)
        {
            _slots[_currentIdx].Add(node);
        }

        /// <summary>
        /// The nodes committed into the packet for <paramref name="tick"/>, if that tick's slot
        /// still holds it. False when the tick was never begun here or has since wrapped.
        /// </summary>
        public bool TryGet(Tick tick, out List<NetworkController> nodes)
        {
            nodes = null;
            if (tick < 0) return false;
            int idx = SlotOf(tick);
            if (_stamps[idx] != tick) return false;
            nodes = _slots[idx];
            return nodes != null;
        }

        /// <summary>Forgets every tick. Lists are kept for reuse.</summary>
        public void Reset()
        {
            for (int i = 0; i < Depth; i++)
            {
                _stamps[i] = EmptyStamp;
                _slots[i]?.Clear();
            }
            _currentIdx = -1;
        }

        private static int SlotOf(Tick tick) => (int)((uint)(int)tick % Depth);
    }
}
