using System;
using System.Collections.Generic;
using Godot;
using Nebula;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// What must happen when a peer leaves a world, by disconnect or by migration.
///
/// <para>All of this was untested until a player who flew off on an expedition was found still
/// standing in the hub on everybody else's screen. Teardown freed their character with
/// <c>QueueNodeForDeletion()</c> -- a local <c>QueueFree</c> -- rather than putting it in
/// <c>QueueDespawnedNodes</c>, which is the ONLY thing <c>SpawnSerializer</c> keys a wire despawn
/// off. The server dropped the node, no client was told, and each remaining peer also leaked the
/// 9-bit local node id it had bound to it (512 per connection).</para>
///
/// <para>Three of these fail against the code as it was, which is the point of writing them.</para>
/// </summary>
[NebulaUnitTest]
public class PeerTeardownTests
{
    /// <summary>
    /// A peer that reads as connected without an ENet host behind it.
    ///
    /// <para>`default(NetPeer)` is NOT enough: `IsSet` is `nativePeer != IntPtr.Zero`, and
    /// `SetInputAuthority` takes ownership only for a peer that is set -- so a default peer silently
    /// owns nothing and every assertion below would pass vacuously. Assigning `NativeData` gives a
    /// non-zero handle while leaving the cached `nativeID` at 0, which is what `PeerIds` is keyed on
    /// here. Nothing in teardown dereferences the handle; the real `Peer(IntPtr)` constructor is
    /// avoided precisely because it would call into native ENet with this fake pointer.</para>
    /// </summary>
    private static NetPeer FakePeer(int handle)
    {
        var peer = default(NetPeer);
        peer.NativeData = new IntPtr(handle);
        return peer;
    }

    private sealed class Fixture : IDisposable
    {
        public readonly WorldRunner World;
        public readonly NetPeer Peer;
        public readonly UUID PeerId;
        public readonly List<NetNode> Nodes = new();
        private readonly bool _wasServer;

        public Fixture()
        {
            // Teardown is server-only work, and the unit process never calls StartServer.
            _wasServer = NetRunner.IsServer;
            NetRunner.ForceRoleForTests(true);

            World = new WorldRunner();
            Peer = FakePeer(1);
            PeerId = UUID.NewUUID();
            NetRunner.Instance.PeerIds[0] = PeerId;
            NetRunner.Instance.Peers[PeerId] = Peer;
            World.CreatePeerStateForTests(Peer, PeerId);
        }

        /// <summary>A node owned by <see cref="Peer"/>, optionally passing as a NetScene.</summary>
        public NetNode OwnedNode(bool despawnOnUnowned, bool isNetScene = true)
        {
            var node = new NetNode();
            node.Network.CurrentWorld = World;
            node.Network.SetIsNetSceneForTests(isNetScene);
            node.Network.DespawnOnUnowned = despawnOnUnowned;
            node.Network.SetInputAuthority(Peer);
            Nodes.Add(node);
            return node;
        }

        public bool HasPeerState(UUID peerId) => World.GetPeerWorldState(peerId).HasValue;

        public void Dispose()
        {
            NetRunner.Instance.PeerIds.Remove(0);
            NetRunner.Instance.Peers.Remove(PeerId);
            foreach (var node in Nodes)
            {
                if (GodotObject.IsInstanceValid(node)) node.Free();
            }
            World.Free();
            NetRunner.ForceRoleForTests(_wasServer);
        }
    }

    /// <summary>
    /// Teardown runs to completion on the unowned branch, where clearing input authority removes
    /// each node from the very HashSet the loop was walking.
    ///
    /// <para>This was expected to be a crash and measurably is not: on .NET Core
    /// <c>HashSet.Remove</c> does not bump the enumerator version, so the old in-place loop survived
    /// it. Kept because the completion it asserts -- peer gone from PeerStates, OnPlayerCleanup
    /// raised -- is what everything downstream of teardown depends on, and because mutating a
    /// collection mid-enumeration is not something the BCL promises to keep tolerating.</para>
    /// </summary>
    [NebulaUnitTest]
    public void TeardownWithUnownedNodesDoesNotThrow()
    {
        using var f = new Fixture();
        f.OwnedNode(despawnOnUnowned: false);
        f.OwnedNode(despawnOnUnowned: false);

        UUID cleanedUp = default;
        f.World.OnPlayerCleanup += (_, peerId) => cleanedUp = peerId;

        f.World.TeardownPeer(f.Peer, f.PeerId, forgetIdentity: true, despawnOwnedNodes: false);

        Assert.False(f.HasPeerState(f.PeerId));
        Assert.Equal(f.PeerId, cleanedUp);
    }

    /// <summary>
    /// THE GHOST. A departing peer's node must be QUEUED for a replicated despawn, not freed on the
    /// spot: only a node in QueueDespawnedNodes is ever written to the wire, and only the ack of
    /// that record releases the remaining peers' local node ids.
    /// </summary>
    [NebulaUnitTest]
    public void TeardownQueuesAWireDespawnRatherThanFreeingLocally()
    {
        using var f = new Fixture();
        var node = f.OwnedNode(despawnOnUnowned: true);

        f.World.TeardownPeer(f.Peer, f.PeerId, forgetIdentity: true, despawnOwnedNodes: false);
        f.World.DrainTeardownDespawns();

        Assert.Contains(node.Network, f.World.QueueDespawnedNodes);
        // Still alive: it has to survive long enough to be exported and acknowledged.
        Assert.False(node.Network.IsMarkedForDeletion);
    }

    /// <summary>Migration forces the despawn even for a node that opted out of it.</summary>
    [NebulaUnitTest]
    public void MigrationDespawnsEvenWhenDespawnOnUnownedIsFalse()
    {
        using var f = new Fixture();
        var node = f.OwnedNode(despawnOnUnowned: false);

        f.World.TeardownPeer(f.Peer, f.PeerId, forgetIdentity: false, despawnOwnedNodes: true);
        f.World.DrainTeardownDespawns();

        Assert.Contains(node.Network, f.World.QueueDespawnedNodes);
    }

    /// <summary>
    /// Ownership is dropped on BOTH branches. For a despawning node that is not tidiness: the node
    /// lives on until every peer acks, and game code decides whether to keep acting on it by asking
    /// whether it still has an input authority -- Player.UpdateVisibilityInterest being the one that
    /// otherwise walks every NetScene in the world each tick on behalf of a peer that has left.
    /// </summary>
    [NebulaUnitTest]
    public void TeardownClearsInputAuthorityOnBothBranches()
    {
        using var f = new Fixture();
        var despawning = f.OwnedNode(despawnOnUnowned: true);
        var surviving = f.OwnedNode(despawnOnUnowned: false);

        f.World.TeardownPeer(f.Peer, f.PeerId, forgetIdentity: true, despawnOwnedNodes: false);

        Assert.False(despawning.Network.InputAuthority.IsSet);
        Assert.False(surviving.Network.InputAuthority.IsSet);
    }

    /// <summary>
    /// A pending despawn the departing peer was the last holdout on completes as they go.
    ///
    /// The departing peer's PeerState.Id is overwritten with a character id first, which is exactly
    /// what PlayerAdmission does to every admitted player. The old code identified the departing
    /// peer by `otherPeerState.Id == peerId` to skip it; with the id overwritten that test never
    /// matched, so the peer on its way out was polled about its own despawn and answered "not yet".
    /// </summary>
    [NebulaUnitTest]
    public void TeardownPromotesAPendingDespawnDespiteAnOverwrittenPeerStateId()
    {
        using var f = new Fixture();
        var node = f.OwnedNode(despawnOnUnowned: false);

        // A second peer that stays, and has already acknowledged the despawn.
        var remainingPeerId = UUID.NewUUID();
        var remainingPeer = FakePeer(2);
        f.World.CreatePeerStateForTests(remainingPeer, remainingPeerId);

        f.World.QueueDespawnedNodes.Add(node.Network);
        f.World.SetClientSpawnState(node.Network.NetId, remainingPeer, WorldRunner.ClientSpawnState.Despawned);
        f.World.SetClientSpawnState(node.Network.NetId, f.Peer, WorldRunner.ClientSpawnState.Spawned);

        // What PlayerAdmission does: the character id, not the peer id.
        var departing = f.World.GetPeerWorldState(f.PeerId).Value;
        departing.Id = UUID.NewUUID();
        f.World.SetPeerState(f.PeerId, departing);

        f.World.TeardownPeer(f.Peer, f.PeerId, forgetIdentity: true, despawnOwnedNodes: false);

        Assert.Contains(node.Network, f.World._pendingDeletion);
    }

    /// <summary>With nobody left to tell, the despawn completes immediately rather than waiting.</summary>
    [NebulaUnitTest]
    public void TeardownOfTheLastPeerPromotesImmediately()
    {
        using var f = new Fixture();
        var node = f.OwnedNode(despawnOnUnowned: false);

        f.World.QueueDespawnedNodes.Add(node.Network);
        f.World.SetClientSpawnState(node.Network.NetId, f.Peer, WorldRunner.ClientSpawnState.Spawned);

        f.World.TeardownPeer(f.Peer, f.PeerId, forgetIdentity: true, despawnOwnedNodes: false);

        Assert.Contains(node.Network, f.World._pendingDeletion);
    }

    /// <summary>
    /// Deleting a despawned node clears its per-peer spawn bookkeeping.
    ///
    /// SetClientSpawnState is the only writer of SpawnState and, until this, there was no eraser at
    /// all -- so every node ever despawned left one entry per peer behind for the life of the
    /// connection, in a dictionary the export prologue walks every tick.
    /// </summary>
    [NebulaUnitTest]
    public void DeletingADespawnedNodeClearsItsPerPeerSpawnState()
    {
        using var f = new Fixture();
        var node = f.OwnedNode(despawnOnUnowned: false);

        f.World.SetClientSpawnState(node.Network.NetId, f.Peer, WorldRunner.ClientSpawnState.Despawned);
        Assert.Equal(WorldRunner.ClientSpawnState.Despawned,
            f.World.GetClientSpawnState(node.Network.NetId, f.Peer));

        f.World._pendingDeletion.Add(node.Network);
        f.World.ProcessPendingDeletions();

        // NotSpawned is what an absent entry reads as.
        Assert.Equal(WorldRunner.ClientSpawnState.NotSpawned,
            f.World.GetClientSpawnState(node.Network.NetId, f.Peer));
    }

    /// <summary>
    /// Teardown must not allocate in proportion to what the peer owned.
    ///
    /// <para>The first version of the fix snapshotted OwnedNodes into two Lists, which is the
    /// world-lifecycle garbage this codebase does not accept -- and it could not simply reuse a
    /// scratch field, because teardown runs on three threads. It drains the set one element at a
    /// time instead: HashSet's enumerator is a struct, so the foreach/break costs nothing, and each
    /// node is handed to the despawn queue as it is seen rather than collected into a list.</para>
    ///
    /// <para>Measured rather than asserted, following NodeInteropAllocationTests. The peer state is
    /// restored between iterations by re-inserting the SAME struct, so the setup reuses its
    /// collections and what is left in the window is teardown's own cost.</para>
    /// </summary>
    [NebulaUnitTest]
    public void TeardownDoesNotAllocatePerDeparture()
    {
        const int departures = 200;
        const int manyNodes = 16;

        using var f = new Fixture();

        long one = MeasureTeardown(f, ownedNodes: 1, departures);
        long many = MeasureTeardown(f, ownedNodes: manyNodes, departures);
        long marginal = (many - one) / (manyNodes - 1);

        GD.Print($"[measure] TeardownPeer: {one} B/departure with 1 owned node, "
            + $"{many} B with {manyNodes} = {marginal} B per additional node");

        // THE ASSERTION THAT MATTERS. Whatever teardown costs, it must not cost MORE for a peer
        // that owned more -- that is exactly the shape the scratch lists had (a List per departure,
        // regrown as it filled). The remaining fixed cost is the closure TeardownPeer hands to
        // RunOnMainThread, which is pre-existing and independent of ownership.
        Assert.True(marginal <= 8,
            $"teardown allocated {marginal} B per additional owned node; it should not scale with ownership");
    }

    /// <summary>
    /// Bytes allocated per teardown of a peer owning <paramref name="ownedNodes"/> nodes.
    ///
    /// <para>The peer state is restored between iterations by re-inserting the SAME struct, so the
    /// setup reuses its collections -- the add/remove cycle never regrows the HashSet after warmup
    /// -- and what is left inside the window is teardown's own cost.</para>
    /// </summary>
    private static long MeasureTeardown(Fixture f, int ownedNodes, int departures)
    {
        var nodes = new List<NetNode>();
        for (int i = 0; i < ownedNodes; i++) nodes.Add(f.OwnedNode(despawnOnUnowned: true));

        var pristine = f.World.GetPeerWorldState(f.PeerId).Value;

        for (int i = 0; i < 20; i++) TearDownOnce(f, nodes, pristine);

        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < departures; i++) TearDownOnce(f, nodes, pristine);
        long after = System.GC.GetAllocatedBytesForCurrentThread();

        f.World.RestorePeerStateForTests(f.PeerId, pristine);
        return (after - before) / departures;
    }

    private static void TearDownOnce(Fixture f, List<NetNode> nodes, WorldRunner.PeerState pristine)
    {
        f.World.RestorePeerStateForTests(f.PeerId, pristine);
        for (int i = 0; i < nodes.Count; i++)
        {
            pristine.OwnedNodes.Add(nodes[i].Network);
            nodes[i].Network.InputAuthority = f.Peer;
        }

        f.World.TeardownPeer(f.Peer, f.PeerId, forgetIdentity: false, despawnOwnedNodes: true);

        // The drain and the downstream completion are part of the steady state and belong inside
        // the measurement: without them the buffers only ever grow, which measures the test rather
        // than the code. Production empties both the same way, a tick later.
        f.World.DrainTeardownDespawns();
        f.World.QueueDespawnedNodes.Clear();
    }

    /// <summary>
    /// A node with no NetScene anywhere above it cannot be despawned over the wire, and must still
    /// not be left behind -- migration passes despawnOwnedNodes precisely to guarantee removal.
    /// Falling back to the local free is the honest outcome; silently skipping would leak it.
    /// </summary>
    [NebulaUnitTest]
    public void ANodeWithNoNetSceneAncestorFallsBackToALocalFree()
    {
        using var f = new Fixture();
        var node = f.OwnedNode(despawnOnUnowned: true, isNetScene: false);

        f.World.TeardownPeer(f.Peer, f.PeerId, forgetIdentity: true, despawnOwnedNodes: false);
        f.World.DrainTeardownDespawns();

        Assert.DoesNotContain(node.Network, f.World.QueueDespawnedNodes);
        Assert.True(node.Network.IsMarkedForDeletion);
    }
}
