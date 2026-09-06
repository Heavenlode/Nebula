using Nebula;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Server-side budget behavior of NetPropertiesSerializer under MTU splitting:
/// a deferred export (maxBytes too small for anything) must preserve the tick's
/// broadcast dirty bits in PendingDirtyMask instead of losing them with
/// processingDirtyMask; a tight budget must self-limit (never exceed maxBytes,
/// report Partial, defer the leftovers); and the packet-coupled stamps must only
/// exist after CommitExport, so an ack can only clear what provably shipped.
///
/// Built on the Protocol-free ctor (primitive props only) plus
/// WorldRunner.CreatePeerStateForTests - no live ENet connection.
/// </summary>
[NebulaUnitTest]
public class PropsBudgetTests
{
    private sealed class Fixture : System.IDisposable
    {
        public WorldRunner World;
        public NetPeer Peer;      // default(NetPeer): ID 0, mapped in PeerIds below
        public UUID PeerId;
        public NetNode Node;
        public NetPropertiesSerializer Serializer;

        public Fixture(params SerialVariantType[] propTypes)
        {
            World = new WorldRunner();
            Peer = default;
            PeerId = UUID.NewUUID();
            NetRunner.Instance.PeerIds[0] = PeerId;
            World.CreatePeerStateForTests(Peer, PeerId);

            Node = new NetNode();
            Node.Network.InterestLayers[PeerId] = 1;
            for (var i = 0; i < propTypes.Length; i++)
            {
                Node.Network.CachedProperties[i] = new PropertyCache { Type = propTypes[i], IntValue = 40 + i };
            }
            Serializer = new NetPropertiesSerializer(Node.Network, propTypes);

            // Spawning: the props gate allows exports while the spawn is in flight
            World.SetClientSpawnState(Node.Network.NetId, Peer, WorldRunner.ClientSpawnState.Spawning);
        }

        public void Dispose()
        {
            NetRunner.Instance.PeerIds.Remove(0);
            Node.Free();
            World.Free();
        }
    }

    /// <summary>The flat presence mask of a section: skips [maskMode:1][age:5], reads propertyCount bits.</summary>
    private static int MaskBitsOf(NetBuffer buf, int propertyCount)
    {
        buf.ResetRead();
        Assert.False(buf.ReadBool());   // no baseline in these fixtures -> mask always on the wire
        buf.ReadBits(5);
        return (int)buf.ReadBits(propertyCount);
    }

    private static NetBuffer Buffer() => new(512, usePool: false);

    // 1. THE data-loss hazard of splitting: Begin() consumes the global dirty mask, so a
    //    peer whose export is deferred for budget must bank those bits in PendingDirtyMask
    //    and ship them (absolute) on a later tick.
    [NebulaUnitTest]
    public void DeferredExport_BanksDirtyBits_ShipsThemLater()
    {
        using var f = new Fixture(SerialVariantType.Int, SerialVariantType.Int);

        f.World.CurrentTick = 1;
        f.Node.Network.DirtyMask = 0b11;
        f.Serializer.Begin();

        var buf = Buffer();
        var result = f.Serializer.Export(f.World, f.Peer, buf, 0);

        Assert.Equal(ExportResult.None, result);
        Assert.Equal(0, buf.WrittenSpan.Length);
        Assert.Equal(0b11, f.Serializer.PendingDirtyByteForTests(f.PeerId, 0));

        // Next tick, no new dirt: the banked bits ship, presence mask covers both props
        f.World.CurrentTick = 2;
        f.Serializer.Begin();
        buf.Reset();
        result = f.Serializer.Export(f.World, f.Peer, buf, int.MaxValue);

        Assert.Equal(ExportResult.Written, result);
        Assert.Equal(0b11, MaskBitsOf(buf, 2));
    }

    // 2. A deferred export with nothing eligible banks nothing.
    [NebulaUnitTest]
    public void DeferredExport_NothingDirty_BanksNothing()
    {
        using var f = new Fixture(SerialVariantType.Int, SerialVariantType.Int);

        f.World.CurrentTick = 1;
        f.Serializer.Begin();

        var buf = Buffer();
        var result = f.Serializer.Export(f.World, f.Peer, buf, 0);

        Assert.Equal(ExportResult.None, result);
        Assert.Equal(0, f.Serializer.PendingDirtyByteForTests(f.PeerId, 0));
    }

    // 3. Self-limiting: one byte under the full section size must drop exactly the last
    //    property - section stays within maxBytes, result is Partial, and the rewound
    //    bit is banked for a later tick.
    [NebulaUnitTest]
    public void TightBudget_SelfLimits_ReportsPartial_BanksLeftover()
    {
        int fullSize;
        using (var sizing = new Fixture(SerialVariantType.Int, SerialVariantType.Int))
        {
            sizing.World.CurrentTick = 1;
            sizing.Node.Network.DirtyMask = 0b11;
            sizing.Serializer.Begin();
            var sizingBuf = Buffer();
            Assert.Equal(ExportResult.Written, sizing.Serializer.Export(sizing.World, sizing.Peer, sizingBuf, int.MaxValue));
            fullSize = sizingBuf.WrittenBits;
        }

        using var f = new Fixture(SerialVariantType.Int, SerialVariantType.Int);
        f.World.CurrentTick = 1;
        f.Node.Network.DirtyMask = 0b11;
        f.Serializer.Begin();

        var buf = Buffer();
        var maxBits = fullSize - 1;
        var result = f.Serializer.Export(f.World, f.Peer, buf, maxBits);

        Assert.Equal(ExportResult.Partial, result);
        Assert.True(buf.WrittenBits <= maxBits);
        Assert.Equal(0b01, MaskBitsOf(buf, 2));
        Assert.Equal(0b10, f.Serializer.PendingDirtyByteForTests(f.PeerId, 0));
    }

    // 4. Packet-coupled stamps live in CommitExport: after commit, the exact-tick ack
    //    clears the pending bits (nothing left unacked); the same ack without a commit
    //    would find no SentHistory record and clear nothing.
    [NebulaUnitTest]
    public void CommittedTickAck_ClearsPending()
    {
        using var f = new Fixture(SerialVariantType.Int, SerialVariantType.Int);
        f.World.CurrentTick = 5;
        f.Node.Network.DirtyMask = 0b11;
        f.Serializer.Begin();

        var buf = Buffer();
        Assert.Equal(ExportResult.Written, f.Serializer.Export(f.World, f.Peer, buf, int.MaxValue));
        f.Serializer.CommitExport(f.World, f.Peer, 5);
        Assert.Equal(0b11, f.Serializer.PendingDirtyByteForTests(f.PeerId, 0));

        f.Serializer.Acknowledge(f.World, f.Peer, 5);

        Assert.Equal(0, f.Serializer.PendingDirtyByteForTests(f.PeerId, 0));
    }

    // 5. Split safety: an ack of a tick whose export was deferred (no committed section,
    //    no SentHistory record) must NOT clear pending bits stamped by an earlier commit.
    [NebulaUnitTest]
    public void DeferredTickAck_DoesNotFalselyClearPending()
    {
        using var f = new Fixture(SerialVariantType.Int, SerialVariantType.Int);
        f.World.CurrentTick = 5;
        f.Node.Network.DirtyMask = 0b11;
        f.Serializer.Begin();

        var buf = Buffer();
        Assert.Equal(ExportResult.Written, f.Serializer.Export(f.World, f.Peer, buf, int.MaxValue));
        f.Serializer.CommitExport(f.World, f.Peer, 5);

        // Tick 6 is deferred for budget - no section, no record
        f.World.CurrentTick = 6;
        f.Serializer.Begin();
        buf.Reset();
        Assert.Equal(ExportResult.None, f.Serializer.Export(f.World, f.Peer, buf, 0));

        f.Serializer.Acknowledge(f.World, f.Peer, 6);

        Assert.Equal(0b11, f.Serializer.PendingDirtyByteForTests(f.PeerId, 0));

        // The genuine send-tick ack still lands
        f.Serializer.Acknowledge(f.World, f.Peer, 5);
        Assert.Equal(0, f.Serializer.PendingDirtyByteForTests(f.PeerId, 0));
    }
}
