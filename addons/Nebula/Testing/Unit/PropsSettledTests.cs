using System;
using Nebula;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// The settled flag: a (node, peer) pair with provably nothing to send skips the whole
/// Export prologue. The unaffordable failure is a node that settles WHILE still owing
/// bytes, or that stays settled through an event that creates them — so every test here
/// asserts delivery, not just the flag. The two named regressions came from the
/// adversarial design review:
///  - Hole 1: a bypassed dirty tick banks pending bits; if the packet is lost and the
///    flag survived, resend-until-acked never runs again.
///  - Hole 2: a pooled peer state must not hand its Settled flag to the next peer.
/// </summary>
[NebulaUnitTest]
public class PropsSettledTests
{
    private sealed class Fixture : IDisposable
    {
        public WorldRunner World;
        public NetPeer Peer;
        public UUID PeerId;
        public NetNode Node;
        public NetPropertiesSerializer Serializer;

        public Fixture(UUID peerId, params SerialVariantType[] propTypes)
        {
            World = new WorldRunner();
            Peer = default;
            PeerId = peerId;
            NetRunner.Instance.PeerIds[0] = PeerId;
            World.CreatePeerStateForTests(Peer, PeerId);

            Node = new NetNode();
            Node.Network.InterestLayers[PeerId] = 1;
            Node.Network.CurrentWorld = World;
            for (var i = 0; i < propTypes.Length; i++)
            {
                Node.Network.CachedProperties[i] = new PropertyCache { Type = propTypes[i], IntValue = 40 + i };
            }
            Serializer = new NetPropertiesSerializer(Node.Network, propTypes);
            World.SetClientSpawnState(Node.Network.NetId, Peer, WorldRunner.ClientSpawnState.Spawning);
        }

        /// <summary>One full tick: mark dirty (or not), Begin, Export, optionally commit+ack.</summary>
        public ExportResult Tick(int tick, long dirty, bool commit, bool ack, out NetBuffer buf)
        {
            World.CurrentTick = tick;
            Node.Network.DirtyMask = dirty;
            Serializer.Begin();
            buf = new NetBuffer(512, usePool: false);
            var result = Serializer.Export(World, Peer, buf, int.MaxValue);
            if (commit) Serializer.CommitExport(World, Peer, tick);
            if (ack) Serializer.Acknowledge(World, Peer, tick);
            return result;
        }

        public void Dispose()
        {
            NetRunner.Instance.PeerIds.Remove(0);
            Node.Free();
            World.Free();
        }
    }

    // 1. The healthy lifecycle: ship, ack, go quiet -> settled; and settled produces
    //    None with zero bytes.
    [NebulaUnitTest]
    public void CleanAckedNode_Settles_AndSkipsClean()
    {
        using var f = new Fixture(UUID.NewUUID(), SerialVariantType.Int);

        Assert.Equal(ExportResult.Written, f.Tick(1, 0b1, commit: true, ack: true, out _));
        Assert.False(f.Serializer.SettledForTests(f.PeerId));   // full run this tick: cleared

        Assert.Equal(ExportResult.None, f.Tick(2, 0, commit: false, ack: false, out var quiet));
        Assert.Equal(0, quiet.WrittenSpan.Length);
        Assert.True(f.Serializer.SettledForTests(f.PeerId));

        // Tick 3 rides the guard: still None, still zero bytes, flag intact.
        Assert.Equal(ExportResult.None, f.Tick(3, 0, commit: false, ack: false, out var skipped));
        Assert.Equal(0, skipped.WrittenSpan.Length);
        Assert.True(f.Serializer.SettledForTests(f.PeerId));
    }

    // 2. HOLE 1 REGRESSION: dirty tick bypasses the settled guard and ships; the packet
    //    is LOST (no ack). The flag must have died with the full run, and the pending
    //    machinery must resend on the next quiet tick — Written, not None.
    [NebulaUnitTest]
    public void BypassedDirtyTick_PacketLost_ResendsInsteadOfSkipping()
    {
        using var f = new Fixture(UUID.NewUUID(), SerialVariantType.Int);

        f.Tick(1, 0b1, commit: true, ack: true, out _);
        f.Tick(2, 0, commit: false, ack: false, out _);
        Assert.True(f.Serializer.SettledForTests(f.PeerId));

        // Value changes; the guard is bypassed by broadcast dirt; the send is committed
        // but never acked — the loss case.
        f.Node.Network.CachedProperties[0] = new PropertyCache { Type = SerialVariantType.Int, IntValue = 99 };
        Assert.Equal(ExportResult.Written, f.Tick(3, 0b1, commit: true, ack: false, out _));
        Assert.False(f.Serializer.SettledForTests(f.PeerId));

        // Quiet tick: the banked pending bit MUST produce a resend.
        Assert.Equal(ExportResult.Written, f.Tick(4, 0, commit: true, ack: false, out var resend));
        Assert.NotEqual(0, resend.WrittenSpan.Length);
    }

    // 3. HOLE 2 REGRESSION: a pooled state handed to a new peer must not inherit
    //    settledness — the new peer is owed the full initial sync.
    [NebulaUnitTest]
    public void PooledState_DoesNotInheritSettled()
    {
        var peerA = UUID.NewUUID();
        using var f = new Fixture(peerA, SerialVariantType.Int);

        f.Tick(1, 0b1, commit: true, ack: true, out _);
        f.Tick(2, 0, commit: false, ack: false, out _);
        Assert.True(f.Serializer.SettledForTests(peerA));

        // Peer A leaves; its state returns to the pool. Peer B arrives and receives the
        // pooled state.
        f.Serializer.CleanupPeer(peerA);
        var peerB = UUID.NewUUID();
        NetRunner.Instance.PeerIds[0] = peerB;
        f.World.CreatePeerStateForTests(f.Peer, peerB);
        f.Node.Network.InterestLayers[peerB] = 1;
        f.World.SetClientSpawnState(f.Node.Network.NetId, f.Peer, WorldRunner.ClientSpawnState.Spawning);

        Assert.False(f.Serializer.SettledForTests(peerB));
        // B's first export carries the initial sync (the ever-dirty prop), not a skip.
        f.World.CurrentTick = 3;
        f.Node.Network.DirtyMask = 0;
        f.Serializer.Begin();
        var buf = new NetBuffer(512, usePool: false);
        Assert.Equal(ExportResult.Written, f.Serializer.Export(f.World, f.Peer, buf, int.MaxValue));
        Assert.NotEqual(0, buf.WrittenSpan.Length);
    }

    // 4. A peer that never acked cannot settle: the pending bits are an obligation.
    [NebulaUnitTest]
    public void PendingOutstanding_NeverSettles()
    {
        using var f = new Fixture(UUID.NewUUID(), SerialVariantType.Int);

        f.Tick(1, 0b1, commit: true, ack: false, out _);
        for (int tick = 2; tick <= 5; tick++)
        {
            Assert.Equal(ExportResult.Written, f.Tick(tick, 0, commit: true, ack: false, out _));
            Assert.False(f.Serializer.SettledForTests(f.PeerId));
        }
        Assert.NotEqual(0, f.Serializer.PendingDirtyByteForTests(f.PeerId, 0));
    }

    // 5. An interest change wakes a settled pair — the event fires the unconditional
    //    clear at the top of the handler.
    [NebulaUnitTest]
    public void InterestChange_UnSettles()
    {
        using var f = new Fixture(UUID.NewUUID(), SerialVariantType.Int);

        f.Tick(1, 0b1, commit: true, ack: true, out _);
        f.Tick(2, 0, commit: false, ack: false, out _);
        Assert.True(f.Serializer.SettledForTests(f.PeerId));

        f.Node.Network.SetPeerInterest(f.PeerId, 0b11, recurse: false);
        Assert.False(f.Serializer.SettledForTests(f.PeerId));
    }

    // 6. Per-peer dirt blocks the pre-gate even while the flag is set: NothingForPeer
    //    must answer false the moment a per-peer write lands.
    [NebulaUnitTest]
    public void PerPeerDirt_BlocksTheGate()
    {
        using var f = new Fixture(UUID.NewUUID(), SerialVariantType.Int);

        f.Tick(1, 0b1, commit: true, ack: true, out _);
        f.Tick(2, 0, commit: false, ack: false, out _);
        f.World.CurrentTick = 3;
        f.Node.Network.DirtyMask = 0;
        f.Serializer.Begin();
        Assert.True(f.Serializer.NothingForPeer(f.PeerId));

        f.Node.Network.PerPeerDirtyMask ??= new System.Collections.Generic.Dictionary<UUID, long>();
        f.Node.Network.PerPeerDirtyMask[f.PeerId] = 0b1;
        Assert.False(f.Serializer.NothingForPeer(f.PeerId));
    }
}
