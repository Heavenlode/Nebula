using System.Collections.Generic;
using Nebula;
using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// ExportRotation.FindStartIndex resolves the per-peer props cursor (the NetId of the
/// next node owed service) against the world node list, which is insertion-ordered, not
/// sorted. Identity match wins; a despawned cursor node falls to the nearest id above;
/// nothing above wraps to index 0.
/// </summary>
[NebulaUnitTest]
public class ExportRotationTests
{
    private static List<NetworkController> Nodes(List<NetNode> keepAlive, params long[] ids)
    {
        var list = new List<NetworkController>();
        foreach (var id in ids)
        {
            var node = new NetNode();
            keepAlive.Add(node);
            node.Network.NetId = new NetId(id);
            list.Add(node.Network);
        }
        return list;
    }

    private static void Free(List<NetNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.Free();
        }
    }

    [NebulaUnitTest]
    public void EmptyListOrUnsetCursor_StartsAtZero()
    {
        var keepAlive = new List<NetNode>();
        var nodes = Nodes(keepAlive, 5, 9, 2);

        Assert.Equal(0, ExportRotation.FindStartIndex(new List<NetworkController>(), 5));
        Assert.Equal(0, ExportRotation.FindStartIndex(nodes, 0));
        Assert.Equal(0, ExportRotation.FindStartIndex(nodes, -1));

        Free(keepAlive);
    }

    [NebulaUnitTest]
    public void ExactMatch_ReturnsItsIndex_EvenUnsorted()
    {
        var keepAlive = new List<NetNode>();
        // Insertion order with id reuse: not ascending
        var nodes = Nodes(keepAlive, 7, 3, 12, 5);

        Assert.Equal(1, ExportRotation.FindStartIndex(nodes, 3));
        Assert.Equal(3, ExportRotation.FindStartIndex(nodes, 5));
        Assert.Equal(0, ExportRotation.FindStartIndex(nodes, 7));

        Free(keepAlive);
    }

    [NebulaUnitTest]
    public void RemovedCursorNode_FallsToNearestIdAbove()
    {
        var keepAlive = new List<NetNode>();
        var nodes = Nodes(keepAlive, 7, 3, 12, 5);

        // Cursor node 6 despawned: nearest above is 7 (index 0), not 12
        Assert.Equal(0, ExportRotation.FindStartIndex(nodes, 6));
        // Cursor 8: nearest above is 12 (index 2)
        Assert.Equal(2, ExportRotation.FindStartIndex(nodes, 8));

        Free(keepAlive);
    }

    [NebulaUnitTest]
    public void CursorPastAllIds_WrapsToZero()
    {
        var keepAlive = new List<NetNode>();
        var nodes = Nodes(keepAlive, 7, 3, 12, 5);

        Assert.Equal(0, ExportRotation.FindStartIndex(nodes, 13));

        Free(keepAlive);
    }

    // Fairness: rotating from the cursor and always advancing it past the served window
    // must visit every node within ceil(N / perTick) rotations, regardless of list order.
    [NebulaUnitTest]
    public void Rotation_ServesEveryNodeWithinOneCycle()
    {
        var keepAlive = new List<NetNode>();
        var nodes = Nodes(keepAlive, 7, 3, 12, 5, 9, 1);
        const int perTick = 2;
        var served = new HashSet<long>();
        long cursor = 0;

        for (var tick = 0; tick < (nodes.Count + perTick - 1) / perTick; tick++)
        {
            var start = ExportRotation.FindStartIndex(nodes, cursor);
            for (var k = 0; k < perTick; k++)
            {
                var node = nodes[(start + k) % nodes.Count];
                served.Add(node.NetId.Value);
            }
            cursor = nodes[(start + perTick) % nodes.Count].NetId.Value;
        }

        Assert.Equal(nodes.Count, served.Count);

        Free(keepAlive);
    }
}
