using Nebula.Serialization.Serializers;

namespace Nebula.Serialization
{
    /// <summary>
    /// Bit-budget ledger for one peer's tick payload. Charges every cost that ends up in the
    /// assembled payload so the final (byte-padded) buffer can never exceed the byte budget it
    /// was built from: the 8-bit group presence upfront, then per committed section its
    /// payload bits plus the framing a node pays the first time it enters the packet - the
    /// serializers-run word, and the group's node set when the node is the first in its
    /// 64-node group. Framing is charged at its WORST case (see PacketFraming: the writer
    /// picks the shorter form at assembly), so the ledger is an upper bound and the packet
    /// only ever comes in under it. Pure struct, allocation-free, unit-testable.
    /// </summary>
    internal struct TickBudgetLedger
    {
        /// <summary>
        /// Worst-case framing around a single section in an otherwise empty packet: the group
        /// presence word, the node's serializers-run word, and the node set of a newly opened
        /// group. A section larger than budget minus this can never ship, no matter how empty
        /// the packet.
        /// </summary>
        public const int MaxSectionOverheadBits =
            PacketFraming.GroupPresenceBits + PacketFraming.SerializersRunWorstBits + PacketFraming.NodeSetWorstBits;

        private readonly int _budget;
        private int _used;

        /// <param name="budgetBits">Total payload budget in bits (NetRunner.TickPayloadBudget * 8).</param>
        public TickBudgetLedger(int budgetBits)
        {
            _budget = budgetBits;
            _used = PacketFraming.GroupPresenceBits; // always present
        }

        /// <summary>Budget in bits.</summary>
        public readonly int Budget => _budget;
        /// <summary>Bits charged so far (an upper bound on the assembled packet's bits).</summary>
        public readonly int Used => _used;
        public readonly int Remaining => _budget - _used;

        /// <summary>Whole bytes the charged bits occupy - what the metrics report as payload size.</summary>
        public readonly int UsedBytes => (_used + BitConstants.BitsInByte - 1) / BitConstants.BitsInByte;
        public readonly int BudgetBytes => _budget / BitConstants.BitsInByte;

        private static int FramingCost(bool firstSectionForNode, bool opensNewGroup)
            => (firstSectionForNode ? PacketFraming.SerializersRunWorstBits : 0)
             + (opensNewGroup ? PacketFraming.NodeSetWorstBits : 0);

        /// <summary>Framing a section was charged, for attribution in diagnostics counters.</summary>
        public static int FramingCostForDiagnostics(bool firstSectionForNode, bool opensNewGroup)
            => FramingCost(firstSectionForNode, opensNewGroup);

        /// <summary>
        /// Max section payload bits that can still be committed for a node with the given
        /// framing situation. Never negative.
        /// </summary>
        public readonly int SectionBudget(bool firstSectionForNode, bool opensNewGroup)
        {
            var available = Remaining - FramingCost(firstSectionForNode, opensNewGroup);
            return available > 0 ? available : 0;
        }

        /// <summary>
        /// Charges a section (payload bits + framing) if it fits. Returns false without
        /// charging when it doesn't.
        /// </summary>
        public bool TryCommitSection(int sectionBits, bool firstSectionForNode, bool opensNewGroup)
        {
            var cost = sectionBits + FramingCost(firstSectionForNode, opensNewGroup);
            if (cost > Remaining)
            {
                return false;
            }
            _used += cost;
            return true;
        }
    }
}
