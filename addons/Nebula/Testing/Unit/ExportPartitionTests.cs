using System.Collections.Generic;
using Nebula;
using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// ExportPartition splits a tick's node list into a peer's input-authority nodes and the
/// rest, so the export phases can serve a player's own character before the crowd.
///
/// The partition being disjoint AND exhaustive is a correctness property, not tidiness: a
/// node in neither list never gets its props exported for that peer, and its dirty bits
/// have already been consumed out of the node's mask by Begin(), so they die unbanked.
/// </summary>
[NebulaUnitTest]
public class ExportPartitionTests
{
    /// <summary>
    /// Builds controllers with distinct NetIds. Authority is assigned by the caller —
    /// default(NetPeer) means unowned, which is the case the IsSet guard exists for.
    /// </summary>
    private static List<NetworkController> Nodes(List<NetNode> keepAlive, int count)
    {
        var list = new List<NetworkController>();
        for (var i = 0; i < count; i++)
        {
            var node = new NetNode();
            keepAlive.Add(node);
            node.Network.NetId = new NetId(i + 1);
            list.Add(node.Network);
        }
        return list;
    }

    private static void Free(List<NetNode> nodes)
    {
        foreach (var node in nodes) node.Free();
    }

    [NebulaUnitTest]
    public void UnsetInputAuthority_IsNeverOwned()
    {
        var keepAlive = new List<NetNode>();
        try
        {
            var nodes = Nodes(keepAlive, 1);
            // default(NetPeer) has ID 0. A peer legitimately holding id 0 must NOT sweep up
            // every unowned node in the world - that is what the IsSet guard prevents.
            Assert.False(ExportPartition.IsOwnedBy(nodes[0], default));
        }
        finally { Free(keepAlive); }
    }

    [NebulaUnitTest]
    public void PartitionIsDisjointAndExhaustive()
    {
        var keepAlive = new List<NetNode>();
        try
        {
            var nodes = Nodes(keepAlive, 6);
            var owned = new List<NetworkController>();
            var shared = new List<NetworkController>();

            ExportPartition.Partition(nodes, default, owned, shared);

            // Nothing is owned (all authorities unset), so everything must be shared -
            // and crucially nothing may be dropped on the floor.
            Assert.Empty(owned);
            Assert.Equal(nodes.Count, shared.Count);
            Assert.Equal(nodes.Count, owned.Count + shared.Count);
            foreach (var n in nodes) Assert.Contains(n, shared);
        }
        finally { Free(keepAlive); }
    }

    [NebulaUnitTest]
    public void PartitionPreservesSourceOrderWithinEachSide()
    {
        var keepAlive = new List<NetNode>();
        try
        {
            var nodes = Nodes(keepAlive, 5);
            var owned = new List<NetworkController>();
            var shared = new List<NetworkController>();

            ExportPartition.Partition(nodes, default, owned, shared);

            // World order inside a partition is what keeps parent-before-child ordering
            // meaningful in the spawn phase.
            for (var i = 1; i < shared.Count; i++)
            {
                Assert.True(shared[i - 1].NetId.Value < shared[i].NetId.Value);
            }
        }
        finally { Free(keepAlive); }
    }

    [NebulaUnitTest]
    public void PartitionClearsPriorContents()
    {
        var keepAlive = new List<NetNode>();
        try
        {
            var nodes = Nodes(keepAlive, 3);
            var owned = new List<NetworkController>();
            var shared = new List<NetworkController>();

            // The buffers are reused across every peer and every tick, so a stale entry
            // would leak one peer's nodes into another peer's packet selection.
            ExportPartition.Partition(nodes, default, owned, shared);
            ExportPartition.Partition(nodes, default, owned, shared);

            Assert.Equal(nodes.Count, shared.Count);
            Assert.Equal(nodes.Count, owned.Count + shared.Count);
        }
        finally { Free(keepAlive); }
    }

    [NebulaUnitTest]
    public void EmptySourceProducesEmptyPartitions()
    {
        var owned = new List<NetworkController> { null };
        var shared = new List<NetworkController> { null };

        ExportPartition.Partition(new List<NetworkController>(), default, owned, shared);

        Assert.Empty(owned);
        Assert.Empty(shared);
    }
}
