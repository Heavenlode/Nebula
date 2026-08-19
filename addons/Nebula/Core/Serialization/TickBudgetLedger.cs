namespace Nebula.Serialization
{
    /// <summary>
    /// Byte-budget ledger for one peer's tick payload. Charges every cost that ends up in
    /// the assembled payload so the final buffer can never exceed the budget:
    /// the 1-byte group mask upfront, then per committed section its payload bytes plus
    /// the framing a node pays the first time it enters the packet — 1 byte for its
    /// serializersRun mask, and 8 bytes for the group's node mask when the node is the
    /// first in its 64-node group. Pure struct, allocation-free, unit-testable.
    /// </summary>
    internal struct TickBudgetLedger
    {
        /// <summary>
        /// Worst-case framing around a single section in an otherwise empty packet:
        /// the group-mask byte, the node's serializersRun byte, and the int64 node mask
        /// of a newly opened group. A section larger than budget minus this can never
        /// ship, no matter how empty the packet.
        /// </summary>
        public const int MaxSectionOverheadBytes = 1 + 1 + 8;

        private readonly int _budget;
        private int _used;

        /// <param name="budget">Total payload budget (see NetRunner.TickPayloadBudget).</param>
        public TickBudgetLedger(int budget)
        {
            _budget = budget;
            _used = 1; // group mask byte, always present
        }

        public readonly int Budget => _budget;
        public readonly int Used => _used;
        public readonly int Remaining => _budget - _used;

        private static int FramingCost(bool firstSectionForNode, bool opensNewGroup)
            => (firstSectionForNode ? 1 : 0) + (opensNewGroup ? 8 : 0);

        /// <summary>
        /// Max section payload bytes that can still be committed for a node with the given
        /// framing situation. Never negative.
        /// </summary>
        public readonly int SectionBudget(bool firstSectionForNode, bool opensNewGroup)
        {
            var available = Remaining - FramingCost(firstSectionForNode, opensNewGroup);
            return available > 0 ? available : 0;
        }

        /// <summary>
        /// Charges a section (payload + framing) if it fits. Returns false without
        /// charging when it doesn't.
        /// </summary>
        public bool TryCommitSection(int sectionBytes, bool firstSectionForNode, bool opensNewGroup)
        {
            var cost = sectionBytes + FramingCost(firstSectionForNode, opensNewGroup);
            if (cost > Remaining)
            {
                return false;
            }
            _used += cost;
            return true;
        }
    }
}
