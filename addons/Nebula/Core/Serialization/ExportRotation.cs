using System.Collections.Generic;

namespace Nebula.Serialization
{
    /// <summary>
    /// Start-index selection for the round-robin property phase of ExportState. The
    /// per-peer cursor stores the NetId of the next node owed service; the node list is
    /// world iteration order (insertion order, not sorted), so resolution is by identity
    /// first, then the closest id at-or-above the cursor for a node that despawned, then
    /// wrap to 0. Selection order only — wire order stays ascending peer-local node id.
    /// </summary>
    internal static class ExportRotation
    {
        public static int FindStartIndex(List<NetworkController> nodes, long cursorNetId)
        {
            if (nodes.Count == 0 || cursorNetId <= 0)
            {
                return 0;
            }
            var bestAbove = -1;
            long bestAboveId = long.MaxValue;
            for (var i = 0; i < nodes.Count; i++)
            {
                var id = nodes[i].NetId.Value;
                if (id == cursorNetId)
                {
                    return i;
                }
                if (id > cursorNetId && id < bestAboveId)
                {
                    bestAboveId = id;
                    bestAbove = i;
                }
            }
            return bestAbove >= 0 ? bestAbove : 0;
        }
    }
}
