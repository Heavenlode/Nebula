using System.Collections.Generic;
using System.Reflection;
using Nebula.Serialization.Serializers;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Who may ride a parent's spawn table.
///
/// A table entry is a RECONCILIATION message: "the node you already built from the .tscn is this
/// NetId". The client matches it against a local instance, which is why the entry carries no
/// parent field -- for an authored scene the parent is implied, and the client rebuilds the whole
/// subtree from the parent's .tscn anyway.
///
/// A runtime spawn has no local instance to match, so the client has to CONSTRUCT it, and with no
/// parent field the only place it can go is the record's own root. For a direct child that is
/// accidentally correct; for a grandchild it silently reparents the node up the tree -- which is
/// the bug these tests exist to keep fixed. Runtime spawns ship their own record instead, and that
/// one does carry an explicit parent id.
/// </summary>
[NebulaUnitTest]
public class SpawnNestedTableMembershipTests
{
    private sealed class Fixture : System.IDisposable
    {
        public WorldRunner World;
        public NetPeer Peer;      // default(NetPeer): ID 0, mapped in PeerIds below
        public UUID PeerId;

        private readonly List<NetNode> _nodes = new();

        public Fixture()
        {
            World = new WorldRunner();
            Peer = default;
            PeerId = UUID.NewUUID();
            NetRunner.Instance.PeerIds[0] = PeerId;
            World.CreatePeerStateForTests(Peer, PeerId);
        }

        /// <summary>A scene baked into its parent's .tscn -- the client builds it for free.</summary>
        public NetworkController Authored()
        {
            var node = NewNode();
            node.Network.ExistsInParentScene = true;
            return node.Network;
        }

        /// <summary>A scene created by WorldRunner.Spawn -- the client has never seen it.</summary>
        public NetworkController RuntimeSpawned() => NewNode().Network;

        private NetNode NewNode()
        {
            var node = new NetNode();
            // Every real node has one by the time it can be collected -- WorldRunner.Spawn assigns
            // it, and IsQueuedForDespawn reads through it.
            node.Network.CurrentWorld = World;
            _nodes.Add(node);
            return node;
        }

        public List<NetworkController> Collect(NetworkController parent)
        {
            var results = new List<NetworkController>();
            var method = typeof(SpawnSerializer).GetMethod(
                "CollectNestedNetScenesRecursive",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            try
            {
                method.Invoke(null, new object[] { World, Peer, parent, results });
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                // Surface the real failure instead of "Exception has been thrown by the target of
                // an invocation", which says nothing about what actually broke.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(e.InnerException).Throw();
            }
            return results;
        }

        public void Dispose()
        {
            NetRunner.Instance.PeerIds.Remove(0);
            foreach (var node in _nodes) node.Free();
            World.Free();
        }
    }

    private static void Attach(NetworkController parent, NetworkController child)
        => parent.DynamicNetworkChildren.Add(child);

    [NebulaUnitTest]
    public void AuthoredChild_RidesTheTable()
    {
        using var f = new Fixture();
        var parent = f.Authored();
        var child = f.Authored();
        Attach(parent, child);

        Assert.Equal(new[] { child }, f.Collect(parent));
    }

    [NebulaUnitTest]
    public void RuntimeSpawnedChild_DoesNotRideTheTable()
    {
        using var f = new Fixture();
        var parent = f.Authored();
        Attach(parent, f.RuntimeSpawned());

        // It spawns through its own record, which carries a parent id the table has no room for.
        Assert.Empty(f.Collect(parent));
    }

    [NebulaUnitTest]
    public void AuthoredNestingRidesTheTableAtAnyDepth()
    {
        // The client rebuilds this whole shape from the outermost .tscn, so every level is only
        // reconciling ids and the flat table is sound however deep it goes.
        using var f = new Fixture();
        var parent = f.Authored();
        var child = f.Authored();
        var grandchild = f.Authored();
        Attach(parent, child);
        Attach(child, grandchild);

        var collected = f.Collect(parent);
        Assert.Equal(2, collected.Count);
        Assert.Contains(child, collected);
        Assert.Contains(grandchild, collected);
    }

    [NebulaUnitTest]
    public void RuntimeSpawnedChild_HidesItsWholeSubtree()
    {
        // The regression that produced the original bug report, in miniature: this authored scene
        // is real and the client WILL build it -- but from the runtime spawn's .tscn, not from this
        // record's. Letting it into this table would attach it to the wrong node entirely.
        using var f = new Fixture();
        var parent = f.Authored();
        var runtime = f.RuntimeSpawned();
        var authoredUnderRuntime = f.Authored();
        Attach(parent, runtime);
        Attach(runtime, authoredUnderRuntime);

        Assert.Empty(f.Collect(parent));
    }

    [NebulaUnitTest]
    public void GrandchildOfARuntimeSpawn_NeverReachesTheRootRecord()
    {
        // The exact shape that broke: root scene -> runtime-spawned planet -> runtime-spawned NPC.
        // The NPC used to land in the ROOT's table, whose entries are attached relative to the
        // record being imported, so the client parented it to the world root at the origin.
        using var f = new Fixture();
        var rootScene = f.Authored();
        var planet = f.RuntimeSpawned();
        var npc = f.RuntimeSpawned();
        Attach(rootScene, planet);
        Attach(planet, npc);

        var collected = f.Collect(rootScene);
        Assert.DoesNotContain(npc, collected);
        Assert.DoesNotContain(planet, collected);
    }

    [NebulaUnitTest]
    public void DespawningAuthoredChild_IsPruned()
    {
        // Pre-existing rule, re-asserted because the new membership check sits right beside it:
        // re-including a despawning scene would flip it back to Spawning mid-despawn and reopen
        // the props exporters the despawn cascade just silenced.
        using var f = new Fixture();
        var parent = f.Authored();
        var child = f.Authored();
        Attach(parent, child);
        f.World.SetClientSpawnState(child.NetId, f.Peer, WorldRunner.ClientSpawnState.Despawning);

        Assert.Empty(f.Collect(parent));
    }
}
