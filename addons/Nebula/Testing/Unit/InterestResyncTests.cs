using System;
using System.Collections.Generic;
using Nebula;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// The interest resync serializer as send-on-change with resend-until-acked. The failure
/// modes that matter: an idle node paying anything at all, a lost ack stranding a peer on the
/// wrong value (the flip-back trap), and a window leaking so the node never returns to the
/// idle fast path. Every scenario ends by asserting the pending count is back to zero.
///
/// Interest is driven through the peer's InterestLayers directly: layers == 0 is "no
/// interest" in NetworkController.IsPeerInterested, and the test node has no [NetInterest]
/// scene entry, so any nonzero value is "interested".
/// </summary>
[NebulaUnitTest]
public class InterestResyncTests
{
    private const int Interval = 3; // InterestResyncSerializer.SYNC_INTERVAL

    private sealed class Fixture : IDisposable
    {
        public WorldRunner World;
        public NetPeer Peer;
        public UUID PeerId;
        public NetNode Node;
        public InterestResyncSerializer Server;

        public Fixture()
        {
            World = new WorldRunner();
            Peer = default;
            PeerId = UUID.NewUUID();
            NetRunner.Instance.PeerIds[0] = PeerId;
            World.CreatePeerStateForTests(Peer, PeerId);

            Node = new NetNode();
            Node.Network.InterestLayers[PeerId] = 1;
            Node.Network.CurrentWorld = World;
            Server = new InterestResyncSerializer(Node.Network);
            World.SetClientSpawnState(Node.Network.NetId, Peer, WorldRunner.ClientSpawnState.Spawned);
        }

        public void SetInterest(bool interested) => Node.Network.InterestLayers[PeerId] = interested ? 1 : 0;

        /// <summary>The node's stagger slot: the first tick at or after <paramref name="from"/> that evaluates.</summary>
        public int NextSlot(int from)
        {
            int offset = (int)(Node.Network.NetId.Value % Interval);
            int tick = from;
            while ((tick + offset) % Interval != 0) tick++;
            return tick;
        }

        /// <summary>Export at a tick; returns the bit written (0/1), or -1 for nothing. Commits when written.</summary>
        public int Tick(int tick, int maxBits = int.MaxValue)
        {
            World.CurrentTick = tick;
            var buf = new NetBuffer(8, usePool: false);
            var result = Server.Export(World, Peer, buf, maxBits);
            if (result == ExportResult.None)
            {
                Assert.Equal(0, buf.WrittenBits);
                return -1;
            }
            Assert.Equal(1, buf.WrittenBits);
            Server.CommitExport(World, Peer, tick);
            buf.ResetRead();
            return buf.ReadBool() ? 1 : 0;
        }

        public void Ack(int tick) => Server.Acknowledge(World, Peer, tick);

        public void Dispose()
        {
            NetRunner.Instance.PeerIds.Remove(0);
            Node.Free();
            World.Free();
        }
    }

    // 1. Idle: an interested peer is never sent anything, on or off the stagger slot.
    [NebulaUnitTest]
    public void Idle_NeverShips()
    {
        using var f = new Fixture();
        for (int tick = 1; tick <= 200; tick++) Assert.Equal(-1, f.Tick(tick));
        Assert.Equal(0, f.Server.PendingPeersForTests);
    }

    // 2. Loss: detected on the next stagger slot, resent every tick, stopped by a covering ack.
    [NebulaUnitTest]
    public void Loss_ShipsOnSlot_ResendsUntilCoveredAck()
    {
        using var f = new Fixture();
        Assert.Equal(-1, f.Tick(1));
        f.SetInterest(false);

        int slot = f.NextSlot(2);
        for (int tick = 2; tick < slot; tick++) Assert.Equal(-1, f.Tick(tick));
        Assert.Equal(0, f.Tick(slot));
        Assert.Equal(1, f.Server.PendingPeersForTests);
        Assert.Equal(0, f.Tick(slot + 1));   // every tick now, not just the slot
        Assert.Equal(0, f.Tick(slot + 2));

        f.Ack(slot - 1);                     // outside the window: ignored
        Assert.Equal(1, f.Server.PendingPeersForTests);
        Assert.Equal(0, f.Tick(slot + 3));

        f.Ack(slot + 1);                     // covered: committed
        Assert.Equal(0, f.Server.PendingPeersForTests);
        for (int tick = slot + 4; tick <= slot + 40; tick++) Assert.Equal(-1, f.Tick(tick));
    }

    // 3. The flip-back trap: interest regained while the loss is unacked must keep sending
    //    the new value until IT is acked, and an ack for the old value's tick must not stop it.
    [NebulaUnitTest]
    public void FlipBackWhileInFlight_KeepsSendingNewValueUntilAcked()
    {
        using var f = new Fixture();
        f.SetInterest(false);
        int t = f.NextSlot(1);
        Assert.Equal(0, f.Tick(t));
        Assert.Equal(0, f.Tick(t + 1));
        Assert.Equal(0, f.Tick(t + 2));

        f.SetInterest(true);                 // back to the value the peer "already holds"
        Assert.Equal(1, f.Tick(t + 3));      // still ships: only an ack says what the peer has
        Assert.Equal(1, f.Tick(t + 4));

        f.Ack(t + 1);                        // the 0's window was restarted; this proves nothing
        Assert.Equal(1, f.Server.PendingPeersForTests);
        Assert.Equal(1, f.Tick(t + 5));

        f.Ack(t + 4);
        Assert.Equal(0, f.Server.PendingPeersForTests);
        for (int tick = t + 6; tick <= t + 30; tick++) Assert.Equal(-1, f.Tick(tick));
    }

    // 4. Budget deferral mid-window: the gap restarts the window, so an ack for a pre-gap
    //    tick does not commit; the post-gap send does.
    [NebulaUnitTest]
    public void Deferral_RestartsWindow()
    {
        using var f = new Fixture();
        f.SetInterest(false);
        int t = f.NextSlot(1);
        Assert.Equal(0, f.Tick(t));
        Assert.Equal(0, f.Tick(t + 1));
        Assert.Equal(-1, f.Tick(t + 2, maxBits: 0));   // deferred, nothing committed
        Assert.Equal(0, f.Tick(t + 3));

        f.Ack(t + 1);                        // pre-gap
        Assert.Equal(1, f.Server.PendingPeersForTests);
        f.Ack(t + 3);
        Assert.Equal(0, f.Server.PendingPeersForTests);
    }

    // 5. A respawn resets the peer to the spawn baseline: an uninterested peer is told again.
    [NebulaUnitTest]
    public void ResetPeerBaseline_ForgetsInFlight_AndRestartsFromInterested()
    {
        using var f = new Fixture();
        f.SetInterest(false);
        int t = f.NextSlot(1);
        Assert.Equal(0, f.Tick(t));
        Assert.Equal(1, f.Server.PendingPeersForTests);

        f.Server.ResetPeerBaseline(f.PeerId);
        Assert.Equal(0, f.Server.PendingPeersForTests);
        Assert.False(f.Server.HasPeerStateForTests(f.PeerId));

        // Fresh instance assumes interest; the loss is re-detected on the next slot.
        int next = f.NextSlot(t + 1);
        for (int tick = t + 1; tick < next; tick++) Assert.Equal(-1, f.Tick(tick));
        Assert.Equal(0, f.Tick(next));
        f.Ack(next);
        Assert.Equal(0, f.Server.PendingPeersForTests);

        f.Server.CleanupPeer(f.PeerId);
        Assert.False(f.Server.HasPeerStateForTests(f.PeerId));
    }

    // 6. Client: starts interested; fires only on a change.
    [NebulaUnitTest]
    public void Client_StartsInterested_FiresOnlyOnChange()
    {
        var node = new NetNode();
        try
        {
            var client = new InterestResyncSerializer(node.Network);
            var fired = new List<bool>();
            node.Network.OnInterestChanged += v => fired.Add(v);

            Assert.True(client.Import(null, Wire(1), out _));
            Assert.Empty(fired);                        // already interested
            Assert.True(client.Import(null, Wire(0), out _));
            Assert.Equal(new[] { false }, fired);
            Assert.True(client.Import(null, Wire(0), out _));
            Assert.Equal(new[] { false }, fired);       // duplicate resend: no re-fire
            Assert.True(client.Import(null, Wire(1), out _));
            Assert.Equal(new[] { false, true }, fired);
        }
        finally
        {
            node.Free();
        }
    }

    private static NetBuffer Wire(byte b)
    {
        var buf = new NetBuffer(8, usePool: false);
        NetWriter.WriteByte(buf, b);
        buf.ResetRead();
        return buf;
    }
}
