using System;
using System.Threading;
using Nebula;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Export lanes: a second thread exporting as lane 1 works in its own scratch and its own
/// memo run. Its bytes equal lane 0's for the same node and peer, it never serves a blob
/// lane 0 captured (a lane's memo is private for the tick), and it captures and hits within
/// its own run exactly as lane 0 does.
/// </summary>
[NebulaUnitTest]
public class ExportLaneTests
{
    private sealed class Fixture : IDisposable
    {
        public WorldRunner World;
        public NetPeer Peer;
        public UUID PeerId;
        public NetNode Node;
        public NetPropertiesSerializer Serializer;
        private readonly int _savedLaneCount;

        public Fixture(int intProps)
        {
            _savedLaneCount = ExportContext.LaneCount;
            ExportContext.LaneCount = 2;
            var propTypes = new SerialVariantType[intProps];
            Array.Fill(propTypes, SerialVariantType.Int);
            World = new WorldRunner();
            Peer = default;
            PeerId = UUID.NewUUID();
            NetRunner.Instance.PeerIds[0] = PeerId;
            World.CreatePeerStateForTests(Peer, PeerId);

            Node = new NetNode();
            Node.Network.InterestLayers[PeerId] = 1;
            Node.Network.CurrentWorld = World;
            for (var i = 0; i < propTypes.Length; i++)
            {
                Node.Network.CachedProperties[i] = new PropertyCache { Type = propTypes[i], IntValue = 40 + i };
            }
            Serializer = new NetPropertiesSerializer(Node.Network, propTypes)
            {
                ForceRingCaptureForTests = true,
            };
            World.SetClientSpawnState(Node.Network.NetId, Peer, WorldRunner.ClientSpawnState.Spawning);
        }

        public void Dispose()
        {
            NetRunner.Instance.PeerIds.Remove(0);
            Node.Free();
            World.Free();
            ExportContext.LaneCount = _savedLaneCount;
        }
    }

    private static NetBuffer Buffer() => new(1024, usePool: false);

    /// <summary>Runs one Export on a fresh thread bound to the given lane; rethrows its failure.</summary>
    private static ExportResult ExportOnLane(Fixture f, int lane, NetBuffer buf)
    {
        ExportResult result = ExportResult.None;
        Exception failure = null;
        var thread = new Thread(() =>
        {
            var ctx = new ExportContext(lane);
            ctx.MakeCurrent();
            try
            {
                result = f.Serializer.Export(f.World, f.Peer, buf, int.MaxValue);
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                ExportContext.ClearCurrent();
            }
        });
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
        return result;
    }

    [NebulaUnitTest]
    public void SecondLane_SameBytes_OwnMemo()
    {
        using var f = new Fixture(24);
        f.World.CurrentTick = 1;
        f.Node.Network.DirtyMask = (1L << 3) | (1L << 20);
        f.Serializer.Begin();

        // Lane 0 (this thread, no context): captures into run 0.
        var lane0 = Buffer();
        Assert.Equal(ExportResult.Written, f.Serializer.Export(f.World, f.Peer, lane0, int.MaxValue));
        Assert.Equal(0, f.Serializer.MemoHitsForTests);
        Assert.Equal(1, f.Serializer.MemoEntriesForTests(0));
        Assert.Equal(0, f.Serializer.MemoEntriesForTests(1));

        // Lane 1 on its own thread: same bytes, but a miss - it cannot see run 0.
        var lane1 = Buffer();
        Assert.Equal(ExportResult.Written, ExportOnLane(f, 1, lane1));
        Assert.True(lane0.WrittenSpan.SequenceEqual(lane1.WrittenSpan));
        Assert.Equal(0, f.Serializer.MemoHitsForTests);
        Assert.Equal(1, f.Serializer.MemoEntriesForTests(1));

        // A second export on lane 1 hits lane 1's own capture, byte-identical.
        var lane1Again = Buffer();
        Assert.Equal(ExportResult.Written, ExportOnLane(f, 1, lane1Again));
        Assert.Equal(1, f.Serializer.MemoHitsForTests);
        Assert.True(lane0.WrittenSpan.SequenceEqual(lane1Again.WrittenSpan));
        Assert.Equal(1, f.Serializer.MemoEntriesForTests(0));
        Assert.Equal(1, f.Serializer.MemoEntriesForTests(1));

        // Begin resets every lane's run, not just the calling thread's.
        f.Serializer.Begin();
        Assert.Equal(0, f.Serializer.MemoEntriesForTests(0));
        Assert.Equal(0, f.Serializer.MemoEntriesForTests(1));
    }
}
