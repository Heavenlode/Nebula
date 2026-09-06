using System;
using System.Collections.Generic;
using Godot;
using Nebula;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// WorldRunner.PeerAcknowledge routes an ack for tick T to exactly the nodes registered as
/// having shipped in packet T - not to every node the peer was ever sent. Driven through the
/// RegisterSentNodeForTests seam (what ExportState's mask walk does) and a recording
/// serializer installed via SetSerializersForTests, since neither ExportState nor
/// SetupSerializers can run without the Protocol registry.
///
/// Ack ordering in these tests is deliberate: PeerAcknowledge remembers the FIRST acked tick
/// and drops any ack at or below it, so a "miss" ack must be issued before a "hit" one at a
/// higher tick, never after.
/// </summary>
[NebulaUnitTest]
public class PeerAckRingTests
{
    /// <summary>Counts Acknowledge calls and remembers the ticks they came with.</summary>
    private sealed class RecordingSerializer : IStateSerializer
    {
        public readonly List<int> AckedTicks = new();
        public void Begin() { }
        public bool Import(WorldRunner currentWorld, NetBuffer data, out NetworkController nodeOut)
        {
            nodeOut = null;
            return true;
        }
        public ExportResult Export(WorldRunner currentWorld, NetPeer peer, NetBuffer buffer, int maxBits) => ExportResult.None;
        public void Acknowledge(WorldRunner currentWorld, NetPeer peer, Tick tick) => AckedTicks.Add(tick);
        public void Cleanup() { }
    }

    private sealed class Fixture : IDisposable
    {
        public WorldRunner World;
        public NetPeer Peer;      // default(NetPeer): ID 0, mapped in PeerIds below
        public UUID PeerId;
        public readonly List<NetNode> Nodes = new();

        public Fixture()
        {
            World = new WorldRunner();
            Peer = default;
            PeerId = UUID.NewUUID();
            NetRunner.Instance.PeerIds[0] = PeerId;
            World.CreatePeerStateForTests(Peer, PeerId);
        }

        /// <summary>A node whose only serializer records the acks it receives.</summary>
        public (NetNode node, RecordingSerializer recorder) RecordingNode()
        {
            var node = new NetNode();
            node.Network.CurrentWorld = World;
            var recorder = new RecordingSerializer();
            node.SetSerializersForTests([recorder]);
            Nodes.Add(node);
            return (node, recorder);
        }

        public void Dispose()
        {
            NetRunner.Instance.PeerIds.Remove(0);
            foreach (var node in Nodes)
            {
                // Free explicitly even if a test queued the node for deletion: the queued
                // free only runs at a frame boundary, and the deferred entry is harmless
                // once the object is already gone (SceneTree skips freed ids).
                if (GodotObject.IsInstanceValid(node))
                {
                    node.Free();
                }
            }
            World.Free();
        }
    }

    // 1. The whole point: an ack visits the nodes registered for THAT tick and nothing else.
    [NebulaUnitTest]
    public void Ack_VisitsOnlyNodesRegisteredForThatTick()
    {
        using var f = new Fixture();
        var (shipped, shippedRec) = f.RecordingNode();
        var (idle, idleRec) = f.RecordingNode();
        f.World.CurrentTick = 10;

        f.World.RegisterSentNodeForTests(f.PeerId, 5, shipped.Network);
        // `idle` shipped on a different tick only.
        f.World.RegisterSentNodeForTests(f.PeerId, 7, idle.Network);

        // Tick 4: nothing shipped. Issued first so the first-ack guard doesn't mask tick 5.
        f.World.PeerAcknowledge(f.Peer, 4);
        Assert.Empty(shippedRec.AckedTicks);
        Assert.Empty(idleRec.AckedTicks);

        f.World.PeerAcknowledge(f.Peer, 5);
        Assert.Equal(new[] { 5 }, shippedRec.AckedTicks);
        Assert.Empty(idleRec.AckedTicks);

        f.World.PeerAcknowledge(f.Peer, 7);
        Assert.Equal(new[] { 5 }, shippedRec.AckedTicks);
        Assert.Equal(new[] { 7 }, idleRec.AckedTicks);
    }

    // 2. Ring depth: an ack whose slot has been reused by a later tick is dropped (the
    //    consumer resends until acked, so this costs one round); the newer tick still lands.
    [NebulaUnitTest]
    public void Ack_OlderThanRingDepth_IsIgnored_NewerStillLands()
    {
        using var f = new Fixture();
        var (old, oldRec) = f.RecordingNode();
        var (recent, recentRec) = f.RecordingNode();
        int oldTick = 5;
        int wrapTick = oldTick + SentNodeRing.Depth;
        f.World.CurrentTick = wrapTick + 1;

        f.World.RegisterSentNodeForTests(f.PeerId, oldTick, old.Network);
        f.World.RegisterSentNodeForTests(f.PeerId, wrapTick, recent.Network); // same slot

        f.World.PeerAcknowledge(f.Peer, oldTick);
        Assert.Empty(oldRec.AckedTicks);
        Assert.Empty(recentRec.AckedTicks);

        f.World.PeerAcknowledge(f.Peer, wrapTick);
        Assert.Empty(oldRec.AckedTicks);
        Assert.Equal(new[] { wrapTick }, recentRec.AckedTicks);
    }

    // 2b. A node whose ONLY section was the interest resync byte is acked through the ring
    //     like any other: the resync serializer's resend-until-acked commit depends on it.
    [NebulaUnitTest]
    public void Ack_ReachesResyncOnlySection()
    {
        using var f = new Fixture();
        var node = new NetNode();
        node.Network.CurrentWorld = f.World;
        node.Network.InterestLayers[f.PeerId] = 1;
        f.Nodes.Add(node);
        var resync = new InterestResyncSerializer(node.Network);
        node.SetSerializersForTests([resync]);
        f.World.SetClientSpawnState(node.Network.NetId, f.Peer, WorldRunner.ClientSpawnState.Spawned);

        // Interest lost: find the stagger slot, export + commit there (what phase 3 does).
        node.Network.InterestLayers[f.PeerId] = 0;
        int tick = 10;
        var buf = new NetBuffer(8, usePool: false);
        while (true)
        {
            f.World.CurrentTick = tick;
            buf.Reset();
            if (resync.Export(f.World, f.Peer, buf, int.MaxValue) == ExportResult.Written) break;
            tick++;
        }
        resync.CommitExport(f.World, f.Peer, tick);
        f.World.RegisterSentNodeForTests(f.PeerId, tick, node.Network);
        Assert.Equal(1, resync.PendingPeersForTests);

        f.World.PeerAcknowledge(f.Peer, tick);
        Assert.Equal(0, resync.PendingPeersForTests);
    }

    // 3. A node freed between commit and ack is skipped, not acked into a dead serializer.
    [NebulaUnitTest]
    public void Ack_SkipsNodeMarkedForDeletion()
    {
        using var f = new Fixture();
        var (doomed, doomedRec) = f.RecordingNode();
        var (alive, aliveRec) = f.RecordingNode();
        f.World.CurrentTick = 10;

        f.World.RegisterSentNodeForTests(f.PeerId, 5, doomed.Network);
        f.World.RegisterSentNodeForTests(f.PeerId, 5, alive.Network);
        doomed.Network.QueueNodeForDeletion();

        f.World.PeerAcknowledge(f.Peer, 5);

        Assert.Empty(doomedRec.AckedTicks);
        Assert.Equal(new[] { 5 }, aliveRec.AckedTicks);
    }

    // 4. End to end with the real props serializer: a committed section's pending bits clear
    //    on the ack for its tick when the node is routed through the ring.
    [NebulaUnitTest]
    public void Ack_ThroughRing_ClearsPropsPendingBits()
    {
        using var f = new Fixture();
        var node = new NetNode();
        f.Nodes.Add(node);
        node.Network.CurrentWorld = f.World;
        node.Network.InterestLayers[f.PeerId] = 1;
        var propTypes = new[] { SerialVariantType.Int, SerialVariantType.Int };
        for (var i = 0; i < propTypes.Length; i++)
        {
            node.Network.CachedProperties[i] = new PropertyCache { Type = propTypes[i], IntValue = 40 + i };
        }
        var props = new NetPropertiesSerializer(node.Network, propTypes);
        var recorder = new RecordingSerializer();
        node.SetSerializersForTests([recorder, props]);
        f.World.SetClientSpawnState(node.Network.NetId, f.Peer, WorldRunner.ClientSpawnState.Spawning);

        f.World.CurrentTick = 5;
        node.Network.DirtyMask = 0b11;
        props.Begin();
        var buf = new NetBuffer(512, usePool: false);
        Assert.Equal(ExportResult.Written, props.Export(f.World, f.Peer, buf, int.MaxValue));
        props.CommitExport(f.World, f.Peer, 5);
        f.World.RegisterSentNodeForTests(f.PeerId, 5, node.Network);
        Assert.Equal(0b11, props.PendingDirtyByteForTests(f.PeerId, 0));

        f.World.PeerAcknowledge(f.Peer, 5);

        Assert.Equal(0, props.PendingDirtyByteForTests(f.PeerId, 0));
        Assert.Equal(new[] { 5 }, recorder.AckedTicks);
    }

    // 5. Acks for ticks the server has not produced are rejected before any routing.
    [NebulaUnitTest]
    public void Ack_BeyondCurrentTick_IsRejected()
    {
        using var f = new Fixture();
        var (node, rec) = f.RecordingNode();
        f.World.CurrentTick = 5;
        f.World.RegisterSentNodeForTests(f.PeerId, 5, node.Network);

        f.World.PeerAcknowledge(f.Peer, 6);

        Assert.Empty(rec.AckedTicks);
    }
}
