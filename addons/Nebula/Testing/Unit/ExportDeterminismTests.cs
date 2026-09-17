using System;
using System.Collections.Generic;
using System.Reflection;
using Nebula;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Parallel export is byte-identical to serial export. Two worlds are built the same way
/// (same nodes, same NetIds, same two peers) and driven through the same script - spawns,
/// a budget-limited props phase with deferrals, acks on one peer and not the other, an
/// interest loss and regain (resync), a despawn with a nested child (cascade) - one world
/// exporting on the tick thread alone and the other on two worker lanes plus the tick
/// thread. Every tick, every peer's packet must match.
///
/// The peers are minted by reflection: ENet.Peer caches its id in a private field and the
/// export path reads nothing else from it, so a non-zero dummy handle never reaches ENet.
/// Real ExportState runs, which needs the server role and a scene path the protocol knows -
/// any registered scene will do, so the first one is borrowed rather than a fixture scene
/// being added to the addon.
/// </summary>
[NebulaUnitTest]
public class ExportDeterminismTests
{
    private const int NodeCount = 6;
    private const int IntProps = 40;
    private const uint PeerANativeId = 0;
    private const uint PeerBNativeId = 1;

    private static NetPeer MintPeer(uint nativeId)
    {
        object boxed = default(ENet.Peer);
        var type = typeof(ENet.Peer);
        var handle = type.GetField("nativePeer", BindingFlags.NonPublic | BindingFlags.Instance);
        var id = type.GetField("nativeID", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(handle);
        Assert.NotNull(id);
        // Non-zero so IsSet holds; never dereferenced (ENet is never called on the export path).
        handle.SetValue(boxed, new IntPtr(0x1000 + nativeId));
        id.SetValue(boxed, nativeId);
        return (ENet.Peer)boxed;
    }

    private static string BorrowedScenePath()
    {
        string best = null;
        foreach (var path in GeneratedProtocol.ScenesPack.Keys)
        {
            if (best == null || string.CompareOrdinal(path, best) < 0) best = path;
        }
        Assert.NotNull(best);
        return best;
    }

    /// <summary>One world with the scripted scene, exporting on the given number of worker lanes.</summary>
    private sealed class Scenario : IDisposable
    {
        public readonly WorldRunner World;
        public readonly List<NetNode> Nodes = new();
        public readonly NetNode NestedChild;
        public readonly List<NetPeer> Peers;

        public Scenario(int workers, NetPeer peerA, NetPeer peerB, UUID idA, UUID idB, string scenePath)
        {
            World = new WorldRunner { ExportWorkerCountOverrideForTests = workers };
            World.CreatePeerStateForTests(peerA, idA);
            World.CreatePeerStateForTests(peerB, idB);
            Peers = new List<NetPeer> { peerA, peerB };

            for (int i = 0; i < NodeCount; i++)
            {
                Nodes.Add(NewNode(scenePath, idA, idB, null));
            }
            // An authored child of the first node: rides its spawn table, falls with its despawn.
            NestedChild = NewNode(scenePath, idA, idB, Nodes[0]);
        }

        private NetNode NewNode(string scenePath, UUID idA, UUID idB, NetNode parent)
        {
            var node = new NetNode();
            node.SceneFilePath = scenePath;
            var net = node.Network;
            net.CurrentWorld = World;
            net.NetId = World.AllocateNetId();
            net.InterestLayers[idA] = -1;
            net.InterestLayers[idB] = -1;
            var propTypes = new SerialVariantType[IntProps];
            Array.Fill(propTypes, SerialVariantType.Int);
            for (int i = 0; i < IntProps; i++)
            {
                net.CachedProperties[i] = new PropertyCache { Type = SerialVariantType.Int, IntValue = 1000 * Nodes.Count + i };
            }
            // Values written through the setters would have marked these dirty; that first
            // dirty pass is what makes them non-default, i.e. part of the initial sync.
            net.DirtyMask = (1L << IntProps) - 1;
            node.SetSerializersForTests(new IStateSerializer[]
            {
                new SpawnSerializer(net),
                new NetPropertiesSerializer(net, propTypes) { ForceRingCaptureForTests = true },
                new InterestResyncSerializer(net),
            });
            World.AddNetScene(net.NetId, net);
            if (parent != null)
            {
                net.ExistsInParentScene = true;
                net.NetParentId = parent.Network.NetId; // also registers it as the parent's dynamic child
            }
            return node;
        }

        public void Dirty(int node, params int[] props)
        {
            long mask = 0;
            foreach (var p in props) mask |= 1L << p;
            var net = Nodes[node].Network;
            net.DirtyMask |= mask;
            foreach (var p in props) net.CachedProperties[p].IntValue += 7;
        }

        public void Export(int tick)
        {
            World.CurrentTick = tick;
            World.ExportState(Peers);
        }

        public void Dispose()
        {
            foreach (var node in Nodes) node.Free();
            NestedChild.Free();
            World.Free();
        }
    }

    [NebulaUnitTest]
    public void TwoLanes_ProduceTheSerialPackets_EveryTick()
    {
        var peerA = MintPeer(PeerANativeId);
        var peerB = MintPeer(PeerBNativeId);
        var idA = UUID.NewUUID();
        var idB = UUID.NewUUID();
        NetRunner.Instance.PeerIds[PeerANativeId] = idA;
        NetRunner.Instance.PeerIds[PeerBNativeId] = idB;
        NetRunner.ForceRoleForTests(true);
        var scenePath = BorrowedScenePath();
        try
        {
            using var serial = new Scenario(0, peerA, peerB, idA, idB, scenePath);
            using var lanes = new Scenario(2, peerA, peerB, idA, idB, scenePath);
            var both = new[] { serial, lanes };

            int packets = 0;
            var sizesA = new Dictionary<int, int>();
            void Step(int tick, Action<Scenario> before = null)
            {
                foreach (var s in both)
                {
                    before?.Invoke(s);
                    s.Export(tick);
                }
                var a0 = serial.World.PeerPacketForTests(idA);
                var a1 = lanes.World.PeerPacketForTests(idA);
                var b0 = serial.World.PeerPacketForTests(idB);
                var b1 = lanes.World.PeerPacketForTests(idB);
                Assert.NotNull(a0);
                Assert.NotNull(b0);
                Assert.True(a0.AsSpan().SequenceEqual(a1), $"peer A packet differs at tick {tick}");
                Assert.True(b0.AsSpan().SequenceEqual(b1), $"peer B packet differs at tick {tick}");
                packets += a0.Length + b0.Length;
                sizesA[tick] = a0.Length;
            }

            Step(1);                                        // spawn-ready pass
            Step(2);                                        // spawn records + props riding them, over budget
            Step(3, s => { s.World.PeerAcknowledge(peerA, 2); s.Dirty(1, 0, 5, 9); });
            Step(4, s => { s.World.PeerAcknowledge(peerB, 3); s.Dirty(2, 1); s.Dirty(4, 30, 31); });
            Step(5, s => { s.Nodes[3].Network.InterestLayers[idB] = 0; s.Dirty(3, 2); });   // B loses interest
            Step(6, s => { s.World.PeerAcknowledge(peerA, 5); s.World.QueueDespawnedNodes.Add(s.Nodes[0].Network); });
            Step(7, s => { s.World.PeerAcknowledge(peerB, 6); s.Dirty(5, 3, 4); });
            Step(8, s => { s.Nodes[3].Network.InterestLayers[idB] = -1; s.Dirty(1, 6); });  // B regains interest
            Step(9, s => { s.World.PeerAcknowledge(peerA, 8); s.World.PeerAcknowledge(peerB, 8); });
            Step(10, s => s.Dirty(2, 7, 8));
            Step(11);

            Assert.True(packets > 0);
            // The script did what it says: the spawn tick fills the packet (seven spawn records
            // and their props do not fit the tick budget, so the props phase deferred), and
            // the quiet final tick is a fraction of that.
            var budget = NetRunner.TickPayloadBudget(NetRunner.MTU);
            Assert.True(sizesA[2] > budget / 2, $"spawn tick packet only {sizesA[2]} bytes of a {budget} byte budget");
            Assert.True(sizesA[2] <= budget, $"spawn tick packet {sizesA[2]} bytes exceeds the {budget} byte budget");
            Assert.True(sizesA[11] < sizesA[2] / 4, $"quiet tick packet {sizesA[11]} bytes is not small next to the spawn tick's {sizesA[2]}");
            Assert.Equal(0, serial.World.PeersExportedByWorkersForTests);
            Assert.True(lanes.World.PeersExportedByWorkersForTests > 0, "no worker lane ever exported a peer");
        }
        finally
        {
            NetRunner.ForceRoleForTests(false);
            NetRunner.Instance.PeerIds.Remove(PeerANativeId);
            NetRunner.Instance.PeerIds.Remove(PeerBNativeId);
        }
    }
}
