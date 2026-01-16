namespace Nebula
{
    /// <summary>
    /// Utilities for hierarchical node ID bitmask operations.
    /// Node IDs are organized into 8 groups of 64 nodes each (512 total).
    /// The wire format uses a 1-byte group mask followed by 8-byte node masks
    /// for each active group, enabling variable-length encoding.
    /// </summary>
    internal static class NodeIdUtils
    {
        /// <summary>
        /// Number of groups in the hierarchical bitmask.
        /// </summary>
        public const int NODE_GROUPS = 8;

        /// <summary>
        /// Number of nodes per group (bits in a long).
        /// </summary>
        public const int NODES_PER_GROUP = 64;

        /// <summary>
        /// Maximum number of network nodes per peer.
        /// </summary>
        public const int MAX_NETWORK_NODES = NODE_GROUPS * NODES_PER_GROUP; // 512

        /// <summary>
        /// Splits a node ID into group and local indices.
        /// </summary>
        /// <param name="nodeId">The full node ID (0-511)</param>
        /// <returns>Tuple of (group index 0-7, local index 0-63)</returns>
        public static (int group, int local) Split(ushort nodeId)
            => (nodeId >> 6, nodeId & 0x3F);

        /// <summary>
        /// Combines group and local indices into a full node ID.
        /// </summary>
        /// <param name="group">Group index (0-7)</param>
        /// <param name="local">Local index within group (0-63)</param>
        /// <returns>Full node ID (0-511)</returns>
        public static ushort Combine(int group, int local)
            => (ushort)((group << 6) | local);

        /// <summary>
        /// Sets a bit in the hierarchical bitmask for the given node ID.
        /// </summary>
        /// <param name="masks">Array of 8 longs representing node availability</param>
        /// <param name="nodeId">The node ID to set</param>
        public static void SetBit(long[] masks, ushort nodeId)
        {
            var (group, local) = Split(nodeId);
            masks[group] |= 1L << local;
        }

        /// <summary>
        /// Clears a bit in the hierarchical bitmask for the given node ID.
        /// </summary>
        /// <param name="masks">Array of 8 longs representing node availability</param>
        /// <param name="nodeId">The node ID to clear</param>
        public static void ClearBit(long[] masks, ushort nodeId)
        {
            var (group, local) = Split(nodeId);
            masks[group] &= ~(1L << local);
        }

        /// <summary>
        /// Checks if a bit is set in the hierarchical bitmask for the given node ID.
        /// </summary>
        /// <param name="masks">Array of 8 longs representing node availability</param>
        /// <param name="nodeId">The node ID to check</param>
        /// <returns>True if the bit is set</returns>
        public static bool IsBitSet(long[] masks, ushort nodeId)
        {
            var (group, local) = Split(nodeId);
            return (masks[group] & (1L << local)) != 0;
        }

        /// <summary>
        /// Computes a byte mask indicating which groups have any bits set.
        /// </summary>
        /// <param name="masks">Array of 8 longs representing node availability</param>
        /// <returns>Byte where bit N is set if masks[N] has any bits set</returns>
        public static byte ComputeGroupMask(long[] masks)
        {
            byte groupMask = 0;
            for (int g = 0; g < NODE_GROUPS; g++)
            {
                if (masks[g] != 0)
                {
                    groupMask |= (byte)(1 << g);
                }
            }
            return groupMask;
        }

        /// <summary>
        /// Finds the first available (unset) node ID in the hierarchical bitmask.
        /// </summary>
        /// <param name="masks">Array of 8 longs representing node availability</param>
        /// <returns>The first available node ID (1-511), or 0 if none available</returns>
        public static ushort FindFirstAvailable(long[] masks)
        {
            for (int group = 0; group < NODE_GROUPS; group++)
            {
                // Check if this group has any available slots
                if (masks[group] == -1L) continue; // All 64 bits set = full

                // Find first unset bit in this group
                // Node IDs start at 1, so local index 0 in group 0 is node ID 1
                for (int local = 0; local < NODES_PER_GROUP; local++)
                {
                    ushort nodeId = Combine(group, local);
                    if (nodeId == 0) continue; // Skip node ID 0 (invalid/reserved)
                    
                    if ((masks[group] & (1L << local)) == 0)
                    {
                        return nodeId;
                    }
                }
            }
            return 0; // No available slots
        }

        /// <summary>
        /// Creates a new initialized array of node masks.
        /// </summary>
        /// <returns>Array of 8 longs initialized to 0</returns>
        public static long[] CreateMasks() => new long[NODE_GROUPS];

        // TODO: Add ID defragmentation logic
        // When peer's node IDs become sparse (e.g., >50% gaps),
        // reassign IDs to be contiguous and sync remap to client.
        // This would involve:
        // 1. Detecting sparsity: activeNodes / highestId < 0.5
        // 2. Building a remap table: oldId -> newId
        // 3. Sending remap to client on reliable channel
        // 4. Client updates all internal references atomically
        // 5. Subsequent ticks use new IDs
    }
}
