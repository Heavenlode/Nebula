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

    // ------------------------------------------------------------------------------ teardown
    //
    // DestroyWorld is the other half of CreateWorld, and until expeditions started closing
    // themselves nothing in the project ever unregistered a world. These pin the refusals, which
    // are the whole reason it is not simply a Worlds.Remove: an occupied world freed out from
    // under PeerWorldMap leaves the ENet pump routing packets into a dead runner.

    /// <summary>The private handoff table, which has no accessor and which teardown must consult.</summary>
    private static System.Collections.IDictionary PendingHandoffs()
        => (System.Collections.IDictionary)typeof(NetRunner)
            .GetField("_pendingHandoffs", Hidden).GetValue(NetRunner.Instance);

    /// <summary>
    /// Runs a body with the server role forced on. DestroyWorld is server-only and the unit
    /// process never calls StartServer, so without this every case below would pass for the
    /// wrong reason -- NotServer looks like a refusal too.
    /// </summary>
    private static void AsServer(System.Action body)
    {
        bool wasServer = NetRunner.IsServer;
        NetRunner.ForceRoleForTests(true);
        try { body(); }
        finally { NetRunner.ForceRoleForTests(wasServer); }
    }

    [NebulaUnitTest]
    public void TestDestroyWorldUnregistersAnEmptyWorld()
    {
        AsServer(() => WithRegisteredWorld(WorldRunner.WorldLifecycle.Live, (world, worldId) =>
        {
            Assert.Equal(NetRunner.WorldDestroyResult.Destroyed, NetRunner.Instance.DestroyWorld(world));
            Assert.False(NetRunner.Instance.Worlds.ContainsKey(worldId));
        }));
    }

    [NebulaUnitTest]
    public void TestDestroyWorldMarksClosingBeforeUnregistering()
    {
        // The ORDER is the point, not the flag. A MigratePeerToWorld already queued to the main
        // thread behind a teardown re-reads Lifecycle; if the removal came first it would find a
        // world that is still Live and about to be freed. Observed through OnWorldDestroying,
        // which fires between the two.
        AsServer(() => WithRegisteredWorld(WorldRunner.WorldLifecycle.Live, (world, worldId) =>
        {
            WorldRunner.WorldLifecycle observed = default;
            bool stillRegistered = true;
            void OnDestroying(WorldRunner w)
            {
                observed = w.Lifecycle;
                stillRegistered = NetRunner.Instance.Worlds.ContainsKey(worldId);
            }

            NetRunner.Instance.OnWorldDestroying += OnDestroying;
            try { NetRunner.Instance.DestroyWorld(world); }
            finally { NetRunner.Instance.OnWorldDestroying -= OnDestroying; }

            Assert.Equal(WorldRunner.WorldLifecycle.Closing, observed);
            Assert.False(stillRegistered);
        }));
    }

    [NebulaUnitTest]
    public void TestDestroyWorldRefusesAnOccupiedWorld()
    {
        AsServer(() => WithRegisteredWorld(WorldRunner.WorldLifecycle.Live, (world, worldId) =>
        {
            world.JoinPeer(default, "token");
            Assert.Equal(1, PeerStateCount(world));

            Assert.Equal(NetRunner.WorldDestroyResult.Occupied, NetRunner.Instance.DestroyWorld(world));
            Assert.True(NetRunner.Instance.Worlds.ContainsKey(worldId));
            Assert.Equal(WorldRunner.WorldLifecycle.Live, world.Lifecycle);
        }));
    }

    [NebulaUnitTest]
    public void TestDestroyWorldRefusesAWorldWithAPendingHandoff()
    {
        // A peer mid-migration is in NO world's PeerStates -- the source dropped them and the
        // target has not taken them yet -- so PeerCount cannot see them. Freeing the target here
        // would leave the client's ready ack driving CompletePeerHandoff into a destroyed world.
        AsServer(() => WithRegisteredWorld(WorldRunner.WorldLifecycle.Live, (world, worldId) =>
        {
            var handoffs = PendingHandoffs();
            var peerId = new UUID();
            var handoffType = typeof(NetRunner).GetNestedType("PendingHandoff", Hidden | BindingFlags.Static);
            handoffs[peerId] = System.Activator.CreateInstance(handoffType, world, "token");
            try
            {
                Assert.Equal(0, PeerCountOf(world));
                Assert.Equal(NetRunner.WorldDestroyResult.Occupied, NetRunner.Instance.DestroyWorld(world));
                Assert.True(NetRunner.Instance.Worlds.ContainsKey(worldId));
            }
            finally { handoffs.Remove(peerId); }
        }));
    }

    private static int PeerCountOf(WorldRunner world) => world.PeerCount;

    [NebulaUnitTest]
    public void TestDestroyWorldRejectsAWorldItDoesNotHold()
    {
        AsServer(() =>
        {
            var stranger = new WorldRunner { WorldId = new UUID(), Lifecycle = WorldRunner.WorldLifecycle.Live };
            try
            {
                Assert.Equal(NetRunner.WorldDestroyResult.NotFound, NetRunner.Instance.DestroyWorld(stranger));
            }
            finally { stranger.Free(); }

            Assert.Equal(NetRunner.WorldDestroyResult.NotFound, NetRunner.Instance.DestroyWorld(null));
        });
    }

    [NebulaUnitTest]
    public void TestDestroyWorldRejectsADifferentRunnerUnderTheSameId()
    {
        // Compared by reference, not by id: an imposter must not be able to take the registered
        // world down with it, and a stale reference to an already-destroyed world must not free
        // whatever now holds its id.
        AsServer(() => WithRegisteredWorld(WorldRunner.WorldLifecycle.Live, (world, worldId) =>
        {
            var impostor = new WorldRunner { WorldId = worldId, Lifecycle = WorldRunner.WorldLifecycle.Live };
            try
            {
                Assert.Equal(NetRunner.WorldDestroyResult.NotFound, NetRunner.Instance.DestroyWorld(impostor));
                Assert.True(NetRunner.Instance.Worlds.ContainsKey(worldId));
            }
            finally { impostor.Free(); }
        }));
    }

    [NebulaUnitTest]
    public void TestDestroyWorldIsServerOnly()
    {
        WithRegisteredWorld(WorldRunner.WorldLifecycle.Live, (world, worldId) =>
        {
            bool wasServer = NetRunner.IsServer;
            NetRunner.ForceRoleForTests(false);
            try
            {
                Assert.Equal(NetRunner.WorldDestroyResult.NotServer, NetRunner.Instance.DestroyWorld(world));
                Assert.True(NetRunner.Instance.Worlds.ContainsKey(worldId));
            }
            finally { NetRunner.ForceRoleForTests(wasServer); }
        });
    }

    [NebulaUnitTest]
    public void TestMigratePeerToWorldRefusesAClosingTarget()
    {
        // Closing joins Generating and Failed at the same gate. The point of setting it before
        // the registry removal is that this refusal is reachable at all.
        AsServer(() => WithRegisteredWorld(WorldRunner.WorldLifecycle.Closing, (world, _) =>
        {
            NetRunner.Instance.MigratePeerToWorld(default, world);
            Assert.Equal(0, PeerStateCount(world));
        }));
    }

    [NebulaUnitTest]
    public void TestPeerJoinWorldRefusesAClosingWorld()
    {
        int peersBefore = NetRunner.Instance.Peers.Count;

        WithRegisteredWorld(WorldRunner.WorldLifecycle.Closing, (world, worldId) =>
        {
            NetRunner.Instance.PeerJoinWorld(default, worldId, "token");
            Assert.Equal(peersBefore, NetRunner.Instance.Peers.Count);
            Assert.Equal(0, PeerStateCount(world));
        });
    }

    [NebulaUnitTest]
    public void TestJoinPeerRefusesAClosingWorld()
    {
        WithRegisteredWorld(WorldRunner.WorldLifecycle.Closing, (world, _) =>
        {
            world.JoinPeer(default, "token");
            Assert.Equal(0, PeerStateCount(world));
        });
    }
}
