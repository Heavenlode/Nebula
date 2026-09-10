using System;
using Nebula;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// The per-peer prepare pass (IStateSerializer.PreparePeer): every per-peer entry a
/// serializer keeps exists before the first Export, and nothing on the export, commit, ack or
/// baseline-reset paths inserts or removes one afterwards. That is the invariant that lets
/// export lanes write the same node's dictionaries for different peers at the same time.
/// The second half checks the presence semantics that moved with it: a pre-created, closed
/// SendWindow means "nothing in flight", and acks still commit through it.
/// </summary>
[NebulaUnitTest]
public class ExportPrepareTests
{
    private sealed class Fixture : IDisposable
    {
        public WorldRunner World;
        public NetPeer Peer;      // default(NetPeer): ID 0, mapped in PeerIds below
        public UUID PeerId;
        public NetNode Node;

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
        }

        public NetPropertiesSerializer Props(int intProps)
        {
            var propTypes = new SerialVariantType[intProps];
            Array.Fill(propTypes, SerialVariantType.Int);
            for (var i = 0; i < propTypes.Length; i++)
            {
                Node.Network.CachedProperties[i] = new PropertyCache { Type = propTypes[i], IntValue = 40 + i };
            }
            return new NetPropertiesSerializer(Node.Network, propTypes) { ForceRingCaptureForTests = true };
        }

        public void Dispose()
        {
            NetRunner.Instance.PeerIds.Remove(0);
            Node.Free();
            World.Free();
        }
    }

    private static NetBuffer Buffer() => new(1024, usePool: false);

    // 1. Props: prepare creates both entries; export, commit, ack and a baseline reset keep
    //    the counts exactly there.
    [NebulaUnitTest]
    public void Props_PrepareCreatesEntries_ExportPathNeverInsertsOrRemoves()
    {
        using var f = new Fixture();
        var props = f.Props(8);
        f.World.SetClientSpawnState(f.Node.Network.NetId, f.Peer, WorldRunner.ClientSpawnState.Spawning);

        Assert.Equal((0, 0), props.PeerEntryCountsForTests);
        props.PreparePeer(f.PeerId);
        Assert.Equal((1, 1), props.PeerEntryCountsForTests);
        props.PreparePeer(f.PeerId); // idempotent
        Assert.Equal((1, 1), props.PeerEntryCountsForTests);

        for (int tick = 1; tick <= 3; tick++)
        {
            f.World.CurrentTick = tick;
            f.Node.Network.DirtyMask = 1L << (tick % 8);
            props.Begin();
            var buf = Buffer();
            Assert.Equal(ExportResult.Written, props.Export(f.World, f.Peer, buf, int.MaxValue));
            props.CommitExport(f.World, f.Peer, tick);
            props.Acknowledge(f.World, f.Peer, tick);
            props.Cleanup();
            Assert.Equal((1, 1), props.PeerEntryCountsForTests);
        }

        props.ResetPeerBaseline(f.PeerId);
        Assert.Equal((1, 1), props.PeerEntryCountsForTests);

        // After the reset the peer is owed the full initial sync again: the next export
        // ships every non-default property, not just the dirty one.
        f.World.CurrentTick = 4;
        f.Node.Network.DirtyMask = 0;
        props.Begin();
        var resync = Buffer();
        Assert.Equal(ExportResult.Written, props.Export(f.World, f.Peer, resync, int.MaxValue));

        props.CleanupPeer(f.PeerId);
        Assert.Equal((0, 0), props.PeerEntryCountsForTests);
    }

    // 2. Resync: prepare seeds the "interested" baseline; a lose/regain cycle and a baseline
    //    reset keep the entry in place, cleanup removes it.
    [NebulaUnitTest]
    public void Resync_PrepareSeedsBaseline_ExportPathNeverInsertsOrRemoves()
    {
        using var f = new Fixture();
        var resync = new InterestResyncSerializer(f.Node.Network);
        f.World.SetClientSpawnState(f.Node.Network.NetId, f.Peer, WorldRunner.ClientSpawnState.Spawned);

        resync.PreparePeer(f.PeerId);
        Assert.Equal(1, resync.PeerEntryCountForTests);
        Assert.False(resync.InFlightForTests(f.PeerId));

        // Interest lost: the next stagger slot writes a 0 and puts the peer in flight.
        f.Node.Network.InterestLayers[f.PeerId] = 0;
        int tick = 1;
        var buf = new NetBuffer(8, usePool: false);
        while (true)
        {
            f.World.CurrentTick = tick;
            buf.Reset();
            if (resync.Export(f.World, f.Peer, buf, int.MaxValue) == ExportResult.Written) break;
            tick++;
        }
        resync.CommitExport(f.World, f.Peer, tick);
        Assert.True(resync.InFlightForTests(f.PeerId));
        Assert.Equal(1, resync.PeerEntryCountForTests);

        resync.Acknowledge(f.World, f.Peer, tick);
        Assert.False(resync.InFlightForTests(f.PeerId));
        Assert.Equal(0, resync.PendingPeersForTests);
        Assert.Equal(1, resync.PeerEntryCountForTests);

        resync.ResetPeerBaseline(f.PeerId);
        Assert.Equal(1, resync.PeerEntryCountForTests);
        Assert.True(resync.HasPeerStateForTests(f.PeerId));

        resync.CleanupPeer(f.PeerId);
        Assert.Equal(0, resync.PeerEntryCountForTests);
    }

    // 3. Spawn: prepared windows are present but closed, so "nothing in flight"; a stamped
    //    spawn commits on ack through the pre-created entry, and the entry survives the ack.
    [NebulaUnitTest]
    public void Spawn_PreparedWindowsAreClosed_AckStillCommitsThroughThem()
    {
        using var f = new Fixture();
        var spawn = new SpawnSerializer(f.Node.Network);
        var netId = f.Node.Network.NetId;

        spawn.PreparePeer(f.PeerId);
        Assert.Equal((1, 1), spawn.WindowCountsForTests);
        Assert.False(spawn.SpawnInFlightForTests(f.PeerId));
        Assert.False(spawn.DespawnInFlightForTests(f.PeerId));

        // Spawn record committed at tick 5 (what CommitExport does), then acked.
        f.World.SetClientSpawnState(netId, f.Peer, WorldRunner.ClientSpawnState.Spawning);
        spawn.StampSpawnForTests(f.PeerId, 5);
        Assert.True(spawn.SpawnInFlightForTests(f.PeerId));

        // A closed despawn window must not swallow the spawn ack.
        spawn.Acknowledge(f.World, f.Peer, 5);
        Assert.Equal(WorldRunner.ClientSpawnState.Spawned, f.World.GetClientSpawnState(netId, f.Peer));
        Assert.False(spawn.SpawnInFlightForTests(f.PeerId));
        Assert.Equal((1, 1), spawn.WindowCountsForTests);

        // An ack outside the window commits nothing.
        f.World.SetClientSpawnState(netId, f.Peer, WorldRunner.ClientSpawnState.Spawning);
        spawn.StampSpawnForTests(f.PeerId, 8);
        spawn.Acknowledge(f.World, f.Peer, 7);
        Assert.Equal(WorldRunner.ClientSpawnState.Spawning, f.World.GetClientSpawnState(netId, f.Peer));
        Assert.True(spawn.SpawnInFlightForTests(f.PeerId));
        spawn.Acknowledge(f.World, f.Peer, 8);
        Assert.Equal(WorldRunner.ClientSpawnState.Spawned, f.World.GetClientSpawnState(netId, f.Peer));
    }

    // 4. Despawn: the despawn window takes priority over the spawn window only while it is
    //    actually open; its ack closes both windows in place.
    [NebulaUnitTest]
    public void Despawn_OpenWindowTakesPriority_AckClosesInPlace()
    {
        using var f = new Fixture();
        var spawn = new SpawnSerializer(f.Node.Network);
        var netId = f.Node.Network.NetId;
        spawn.PreparePeer(f.PeerId);

        f.World.SetClientSpawnState(netId, f.Peer, WorldRunner.ClientSpawnState.Despawning);
        spawn.StampSpawnForTests(f.PeerId, 3);   // stale spawn send, superseded
        spawn.StampDespawnForTests(f.PeerId, 4);
        Assert.True(spawn.DespawnInFlightForTests(f.PeerId));

        // Acking the stale spawn tick does not commit a spawn while a despawn is in flight.
        spawn.Acknowledge(f.World, f.Peer, 3);
        Assert.Equal(WorldRunner.ClientSpawnState.Despawning, f.World.GetClientSpawnState(netId, f.Peer));

        spawn.Acknowledge(f.World, f.Peer, 4);
        Assert.Equal(WorldRunner.ClientSpawnState.Despawned, f.World.GetClientSpawnState(netId, f.Peer));
        Assert.False(spawn.DespawnInFlightForTests(f.PeerId));
        Assert.False(spawn.SpawnInFlightForTests(f.PeerId));
        Assert.Equal((1, 1), spawn.WindowCountsForTests);

        spawn.CleanupPeer(f.PeerId);
        Assert.Equal((0, 0), spawn.WindowCountsForTests);
    }

    // 5. The window primitive itself: closed by default, open after a send, closed again by
    //    the in-place reset the serializers use instead of a removal.
    [NebulaUnitTest]
    public void SendWindow_IsOpen_TracksFirstTick()
    {
        var window = default(SendWindow);
        Assert.False(window.IsOpen);
        window.RecordSend(7);
        Assert.True(window.IsOpen);
        Assert.True(window.Covers(7));
        window = default;
        Assert.False(window.IsOpen);
        Assert.False(window.Covers(7));
    }
}
