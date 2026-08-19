using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Covers the world lifecycle gate on peer admission.
///
/// World creation registers a world in <see cref="NetRunner.Worlds"/> before it has been built --
/// that is what stops two callers racing to create the same world from each building their own.
/// The consequence is that "findable in Worlds" stopped implying "ready for players", and
/// <see cref="WorldRunner.Lifecycle"/> is what distinguishes them. Without the gate, a peer admitted
/// during generation would be streamed a world that is still assembling itself.
///
/// Ticking is gated separately, by the world SubViewport's ProcessMode, which needs a real
/// SceneTree and so belongs to the integration harness rather than here.
/// </summary>
[NebulaUnitTest]
public class WorldLifecycleTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    private static int PeerStateCount(WorldRunner world)
    {
        var states = typeof(WorldRunner).GetField("PeerStates", Hidden).GetValue(world);
        return (int)states.GetType().GetProperty("Count").GetValue(states);
    }

    /// <summary>Registers a world, runs the body, then unregisters it and everything it touched.</summary>
    private static void WithRegisteredWorld(WorldRunner.WorldLifecycle lifecycle, System.Action<WorldRunner, UUID> body)
    {
        var worldId = new UUID();
        var world = new WorldRunner { WorldId = worldId, Lifecycle = lifecycle };
        NetRunner.Instance.Worlds[worldId] = world;
        try
        {
            body(world, worldId);
        }
        finally
        {
            NetRunner.Instance.Worlds.Remove(worldId);
            foreach (var peerId in new List<UUID>(NetRunner.Instance.PeerWorldMap.Keys))
            {
                if (NetRunner.Instance.PeerWorldMap[peerId] == world)
                {
                    NetRunner.Instance.PeerWorldMap.Remove(peerId);
                }
            }
            world.Free();
        }
    }

    [NebulaUnitTest]
    public void TestAWorldStartsOutGenerating()
    {
        var world = new WorldRunner();
        try
        {
            // The default matters: a world is reachable through Worlds from the instant creation
            // begins, so the safe assumption for a world nobody has finished building is "not yet".
            Assert.Equal(WorldRunner.WorldLifecycle.Generating, world.Lifecycle);
        }
        finally { world.Free(); }
    }

    [NebulaUnitTest]
    public void TestJoinPeerRefusesAGeneratingWorld()
    {
        WithRegisteredWorld(WorldRunner.WorldLifecycle.Generating, (world, _) =>
        {
            world.JoinPeer(default, "token");
            Assert.Equal(0, PeerStateCount(world));
        });
    }

    [NebulaUnitTest]
    public void TestJoinPeerRefusesAFailedWorld()
    {
        WithRegisteredWorld(WorldRunner.WorldLifecycle.Failed, (world, _) =>
        {
            world.JoinPeer(default, "token");
            Assert.Equal(0, PeerStateCount(world));
        });
    }

    [NebulaUnitTest]
    public void TestJoinPeerAdmitsALiveWorld()
    {
        // The counterpart to the refusals above: without this, a gate that rejected everything
        // would look just as green.
        WithRegisteredWorld(WorldRunner.WorldLifecycle.Live, (world, _) =>
        {
            world.JoinPeer(default, "token");
            Assert.Equal(1, PeerStateCount(world));
        });
    }

    [NebulaUnitTest]
    public void TestPeerJoinWorldRefusesAGeneratingWorld()
    {
        int peersBefore = NetRunner.Instance.Peers.Count;

        WithRegisteredWorld(WorldRunner.WorldLifecycle.Generating, (world, worldId) =>
        {
            NetRunner.Instance.PeerJoinWorld(default, worldId, "token");

            // Refused before the peer identity is minted, so nothing is left half-registered.
            Assert.Equal(peersBefore, NetRunner.Instance.Peers.Count);
            Assert.Equal(0, PeerStateCount(world));
        });
    }

    [NebulaUnitTest]
    public void TestPeerJoinWorldHandlesAnUnknownWorld()
    {
        // Previously an unknown id indexed straight into Worlds and threw a KeyNotFoundException
        // out of the ENet pump.
        int peersBefore = NetRunner.Instance.Peers.Count;
        NetRunner.Instance.PeerJoinWorld(default, new UUID(), "token");
        Assert.Equal(peersBefore, NetRunner.Instance.Peers.Count);
    }
}
