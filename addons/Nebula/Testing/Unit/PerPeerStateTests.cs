using System.Collections.Generic;
using System.ComponentModel;
using Nebula;
using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Unit tests for PerPeerState property storage: the thread-static ForPeer scope, the
/// TryReadPerPeer/TryWritePerPeer accessors the generated partial properties call, and
/// disconnect cleanup. These drive NetworkController's internal seams directly (no live
/// NetRunner/WorldRunner); export-level behavior is covered by manual multi-client runs.
/// </summary>
[NebulaUnitTest]
public class PerPeerStateTests
{
    /// <summary>Creates a node whose controller has one per-peer int prop (index 0) and one per-peer bool prop (index 1).</summary>
    private static NetNode CreateNodeWithPerPeerStorage()
    {
        var node = new NetNode();
        node.Network.InitializePerPeerStorageForTests(
            [true, true],
            [SerialVariantType.Int, SerialVariantType.Bool]);
        return node;
    }

    // 1. Scopes nest and Dispose restores the OUTER peer, not default (regression: the old
    //    PeerScope reset to default unconditionally).
    [NebulaUnitTest]
    public void ScopeNesting_RestoresOuterPeer()
    {
        var node = new NetNode();
        var a = UUID.NewUUID();
        var b = UUID.NewUUID();

        Assert.Equal(default, NetworkController.CurrentContextPeer);
        using (node.Network.ForPeer(a))
        {
            Assert.Equal(a, NetworkController.CurrentContextPeer);
            using (node.Network.ForPeer(b))
            {
                Assert.Equal(b, NetworkController.CurrentContextPeer);
            }
            Assert.Equal(a, NetworkController.CurrentContextPeer);
        }
        Assert.Equal(default, NetworkController.CurrentContextPeer);

        node.Free();
    }

    // 2. Two peers hold distinct values in the same property slot; reads resolve per peer and
    //    a no-scope read finds no override.
    [NebulaUnitTest]
    public void WriteRead_RoundTrip_TwoPeersDistinct()
    {
        var node = CreateNodeWithPerPeerStorage();
        var net = node.Network;
        var a = UUID.NewUUID();
        var b = UUID.NewUUID();
        NetworkController.ForcePerPeerServerContextForTests = true;
        try
        {
            int idx = 0;
            NetworkController root = null;

            using (net.ForPeer(a))
                Assert.True(net.TryWritePerPeer(node, "P", 11, ref idx, ref root));
            using (net.ForPeer(b))
                Assert.True(net.TryWritePerPeer(node, "P", 22, ref idx, ref root));

            using (net.ForPeer(a))
            {
                Assert.True(net.TryReadPerPeer(node, "P", ref idx, ref root, out var cacheA));
                Assert.Equal(11, cacheA.IntValue);
            }
            using (net.ForPeer(b))
            {
                Assert.True(net.TryReadPerPeer(node, "P", ref idx, ref root, out var cacheB));
                Assert.Equal(22, cacheB.IntValue);
            }

            // No scope open: no override is readable (generated getter falls back to base field)
            Assert.False(net.TryReadPerPeer(node, "P", ref idx, ref root, out _));
        }
        finally
        {
            NetworkController.ForcePerPeerServerContextForTests = false;
        }
        node.Free();
    }

    // 3. THE original bug: peer B writing the value peer A already holds must still create B's
    //    override and dirty bit (Fody's woven equality guard used to suppress the whole write).
    [NebulaUnitTest]
    public void EqualValueForSecondPeer_StillStoredAndDirty()
    {
        var node = CreateNodeWithPerPeerStorage();
        var net = node.Network;
        var a = UUID.NewUUID();
        var b = UUID.NewUUID();
        NetworkController.ForcePerPeerServerContextForTests = true;
        try
        {
            int idx = 0;
            NetworkController root = null;

            using (net.ForPeer(a))
                net.TryWritePerPeer(node, "P", 5, ref idx, ref root);
            using (net.ForPeer(b))
                net.TryWritePerPeer(node, "P", 5, ref idx, ref root);

            Assert.True(net.PerPeerValues[0].ContainsKey(a));
            Assert.True(net.PerPeerValues[0].ContainsKey(b));
            Assert.Equal(1L << 0, net.PerPeerDirtyMask[a]);
            Assert.Equal(1L << 0, net.PerPeerDirtyMask[b]);
        }
        finally
        {
            NetworkController.ForcePerPeerServerContextForTests = false;
        }
        node.Free();
    }

    // 4. Writes without a scope (or without storage) refuse and leave routing to the broadcast
    //    path; dirty bits accumulate per property.
    [NebulaUnitTest]
    public void WriteGates_And_DirtyBitAccumulation()
    {
        var node = CreateNodeWithPerPeerStorage();
        var net = node.Network;
        var a = UUID.NewUUID();
        NetworkController.ForcePerPeerServerContextForTests = true;
        try
        {
            int idx0 = 0;
            int idx1 = 1;
            NetworkController root = null;

            // No scope open -> refused
            Assert.False(net.TryWritePerPeer(node, "P", 1, ref idx0, ref root));

            // Storage absent (plain node) -> refused even inside a scope
            var bare = new NetNode();
            int bareIdx = 0;
            NetworkController bareRoot = null;
            using (net.ForPeer(a))
                Assert.False(bare.Network.TryWritePerPeer(bare, "P", 1, ref bareIdx, ref bareRoot));
            bare.Free();

            // Both per-peer props written for one peer -> both bits set
            using (net.ForPeer(a))
            {
                Assert.True(net.TryWritePerPeer(node, "P0", 7, ref idx0, ref root));
                Assert.True(net.TryWritePerPeer(node, "P1", true, ref idx1, ref root));
            }
            Assert.Equal((1L << 0) | (1L << 1), net.PerPeerDirtyMask[a]);
            Assert.True(net.PerPeerValues[1][a].BoolValue);
        }
        finally
        {
            NetworkController.ForcePerPeerServerContextForTests = false;
        }
        node.Free();
    }

    // 5. Disconnect cleanup evicts exactly the departing peer from values and dirty masks.
    [NebulaUnitTest]
    public void CleanupPeerState_EvictsOnlyThatPeer()
    {
        var node = CreateNodeWithPerPeerStorage();
        var net = node.Network;
        var a = UUID.NewUUID();
        var b = UUID.NewUUID();
        NetworkController.ForcePerPeerServerContextForTests = true;
        try
        {
            int idx = 0;
            NetworkController root = null;
            using (net.ForPeer(a))
                net.TryWritePerPeer(node, "P", 1, ref idx, ref root);
            using (net.ForPeer(b))
                net.TryWritePerPeer(node, "P", 2, ref idx, ref root);

            net.CleanupPeerState(a);

            Assert.False(net.PerPeerValues[0].ContainsKey(a));
            Assert.False(net.PerPeerDirtyMask.ContainsKey(a));
            Assert.True(net.PerPeerValues[0].ContainsKey(b));
            Assert.Equal(2, net.PerPeerValues[0][b].IntValue);
        }
        finally
        {
            NetworkController.ForcePerPeerServerContextForTests = false;
        }
        node.Free();
    }

    // 6. PropertyChanged.Fody must NOT weave the generated per-peer setters ([DoNotNotify]):
    //    its woven equality guard was the original bug (second peer writing an equal value was
    //    silently dropped). Woven setters raise PropertyChanged; per-peer setters must not.
    //    BroadcastValue (a plain woven property on the same fixture) is the positive control.
    [NebulaUnitTest]
    public void Fody_DoesNotWeave_PerPeerSetters()
    {
        var node = new PerPeerTestNode();
        var fired = new List<string>();
        ((INotifyPropertyChanged)node).PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        node.BroadcastValue = 123;
        Assert.Contains("BroadcastValue", fired);

        node.PerPeerValue = 99;
        Assert.DoesNotContain("PerPeerValue", fired);

        node.Free();
    }

    // 7. Regression: a constructor-time per-peer default routes through the generated setter
    //    -> MarkDirty -> IsNetScene() while SceneFilePath is still empty (exactly what
    //    PackedScene.Instantiate does before assigning the path). That probe must NOT cache,
    //    or a real NetScene is permanently demoted - no serializers, no property sync.
    [NebulaUnitTest]
    public void CtorWrite_DoesNotPoison_IsNetSceneCache()
    {
        // Fixture ctor writes PerPeerValue = -1, firing the probe.
        var node = new PerPeerTestNode();
        Assert.False(node.Network.IsNetSceneCacheForTests.HasValue);
        Assert.False(node.Network.IsNetScene());
        Assert.False(node.Network.IsNetSceneCacheForTests.HasValue);

        // Once a non-empty path exists (instantiation done), the answer is derived from the
        // protocol registry and cached.
        node.SceneFilePath = "res://addons/Nebula/Testing/Unit/PerPeerTestNode.tscn";
        Assert.False(node.Network.IsNetScene());
        Assert.True(node.Network.IsNetSceneCacheForTests.HasValue);

        node.Free();
    }

    // 8. The peer context is process-wide (thread-static): a scope opened through one node's
    //    controller applies to per-peer writes on a DIFFERENT node. This is what lets static
    //    children work - their MarkDirty forwarding used to lose the instance-bound context.
    [NebulaUnitTest]
    public void Context_IsProcessWide_AcrossControllers()
    {
        var nodeA = CreateNodeWithPerPeerStorage();
        var nodeB = CreateNodeWithPerPeerStorage();
        var peer = UUID.NewUUID();
        NetworkController.ForcePerPeerServerContextForTests = true;
        try
        {
            int idx = 0;
            NetworkController root = null;

            // Scope opened via nodeA's controller; write lands through nodeB's controller
            using (nodeA.Network.ForPeer(peer))
                Assert.True(nodeB.Network.TryWritePerPeer(nodeB, "P", 42, ref idx, ref root));

            Assert.Equal(42, nodeB.Network.PerPeerValues[0][peer].IntValue);
            Assert.False(nodeA.Network.PerPeerValues[0].ContainsKey(peer));
        }
        finally
        {
            NetworkController.ForcePerPeerServerContextForTests = false;
        }
        nodeA.Free();
        nodeB.Free();
    }

    // ----------------------------------------------------------------------------------------
    // Per-peer NetArray properties. Unlike a primitive, an array is mutated in place, so the
    // generated getter routes through TryGetPerPeerArray and forks on any scoped access. All of
    // this is server-side: the client holds one plain NetArray written by the normal import path.
    // ----------------------------------------------------------------------------------------

    /// <summary>Creates a node whose controller has one per-peer object (NetArray) prop at index 0.</summary>
    private static NetNode CreateNodeWithPerPeerArrayStorage()
    {
        var node = new NetNode();
        node.Network.InitializePerPeerStorageForTests(
            [true],
            [SerialVariantType.Object]);
        return node;
    }

    // A1. Two peers fork from the same base and diverge; the base is untouched by either.
    [NebulaUnitTest]
    public void PerPeerArray_TwoPeersDiverge()
    {
        var node = CreateNodeWithPerPeerArrayStorage();
        var net = node.Network;
        var a = UUID.NewUUID();
        var b = UUID.NewUUID();
        var baseArray = new NetArray<byte>(64, 8);
        for (int i = 0; i < 8; i++) baseArray[i] = 1;

        NetworkController.ForcePerPeerServerContextForTests = true;
        try
        {
            int idx = 0;
            NetworkController root = null;

            using (net.ForPeer(a))
                net.TryGetPerPeerArray(node, "Arr", baseArray, ref idx, ref root)[2] = 111;
            using (net.ForPeer(b))
                net.TryGetPerPeerArray(node, "Arr", baseArray, ref idx, ref root)[2] = 222;

            using (net.ForPeer(a))
                Assert.Equal((byte)111, net.TryGetPerPeerArray(node, "Arr", baseArray, ref idx, ref root)[2]);
            using (net.ForPeer(b))
                Assert.Equal((byte)222, net.TryGetPerPeerArray(node, "Arr", baseArray, ref idx, ref root)[2]);

            Assert.Equal((byte)1, baseArray[2]);
            Assert.True(net.PerPeerValues[0].ContainsKey(a));
            Assert.True(net.PerPeerValues[0].ContainsKey(b));
        }
        finally
        {
            NetworkController.ForcePerPeerServerContextForTests = false;
        }
        node.Free();
    }

    // A2. With no scope open the getter hands back the base instance itself and allocates nothing -
    //     this is also the client-side path, where per-peer state must never engage.
    [NebulaUnitTest]
    public void PerPeerArray_NoScope_ReturnsBaseAndDoesNotFork()
    {
        var node = CreateNodeWithPerPeerArrayStorage();
        var net = node.Network;
        var baseArray = new NetArray<byte>(64, 8);

        NetworkController.ForcePerPeerServerContextForTests = true;
        try
        {
            int idx = 0;
            NetworkController root = null;

            Assert.Same(baseArray, net.TryGetPerPeerArray(node, "Arr", baseArray, ref idx, ref root));
            Assert.Empty(net.PerPeerValues[0]);

            // Storage absent (plain node) -> base even inside a scope
            var bare = new NetNode();
            int bareIdx = 0;
            NetworkController bareRoot = null;
            using (net.ForPeer(UUID.NewUUID()))
                Assert.Same(baseArray, bare.Network.TryGetPerPeerArray(bare, "Arr", baseArray, ref bareIdx, ref bareRoot));
            bare.Free();
        }
        finally
        {
            NetworkController.ForcePerPeerServerContextForTests = false;
        }
        node.Free();
    }

    // A3. Repeated scoped access returns the SAME forked instance - forking happens once per peer.
    [NebulaUnitTest]
    public void PerPeerArray_ForksOncePerPeer()
    {
        var node = CreateNodeWithPerPeerArrayStorage();
        var net = node.Network;
        var peer = UUID.NewUUID();
        var baseArray = new NetArray<byte>(64, 8);

        NetworkController.ForcePerPeerServerContextForTests = true;
        try
        {
            int idx = 0;
            NetworkController root = null;

            NetArray<byte> first, second;
            using (net.ForPeer(peer))
                first = net.TryGetPerPeerArray(node, "Arr", baseArray, ref idx, ref root);
            using (net.ForPeer(peer))
                second = net.TryGetPerPeerArray(node, "Arr", baseArray, ref idx, ref root);

            Assert.NotSame(baseArray, first);
            Assert.Same(first, second);
        }
        finally
        {
            NetworkController.ForcePerPeerServerContextForTests = false;
        }
        node.Free();
    }

    // A4. Disconnect cleanup drops exactly the departing peer's forked instance.
    [NebulaUnitTest]
    public void PerPeerArray_CleanupPeerState_EvictsOnlyThatPeer()
    {
        var node = CreateNodeWithPerPeerArrayStorage();
        var net = node.Network;
        var a = UUID.NewUUID();
        var b = UUID.NewUUID();
        var baseArray = new NetArray<byte>(64, 8);

        NetworkController.ForcePerPeerServerContextForTests = true;
        try
        {
            int idx = 0;
            NetworkController root = null;
            using (net.ForPeer(a))
                net.TryGetPerPeerArray(node, "Arr", baseArray, ref idx, ref root)[0] = 7;
            using (net.ForPeer(b))
                net.TryGetPerPeerArray(node, "Arr", baseArray, ref idx, ref root)[0] = 8;

            net.CleanupPeerState(a);

            Assert.False(net.PerPeerValues[0].ContainsKey(a));
            Assert.True(net.PerPeerValues[0].ContainsKey(b));
            Assert.Equal((byte)8, ((NetArray<byte>)net.PerPeerValues[0][b].RefValue)[0]);
        }
        finally
        {
            NetworkController.ForcePerPeerServerContextForTests = false;
        }
        node.Free();
    }

    // A5. Wholesale assignment inside a scope replaces only that peer's instance (the
    //     TryWritePerPeerRef path - TryWritePerPeer<T> is constrained to value types).
    [NebulaUnitTest]
    public void PerPeerArray_WholesaleAssignment_TargetsOnlyThatPeer()
    {
        var node = CreateNodeWithPerPeerArrayStorage();
        var net = node.Network;
        var a = UUID.NewUUID();
        var b = UUID.NewUUID();
        var baseArray = new NetArray<byte>(64, 8);
        var replacement = new NetArray<byte>(64, 8);
        replacement[0] = 42;

        NetworkController.ForcePerPeerServerContextForTests = true;
        try
        {
            int idx = 0;
            NetworkController root = null;

            using (net.ForPeer(a))
                Assert.True(net.TryWritePerPeerRef(node, "Arr", replacement, ref idx, ref root));

            using (net.ForPeer(a))
                Assert.Same(replacement, net.TryGetPerPeerArray(node, "Arr", baseArray, ref idx, ref root));
            using (net.ForPeer(b))
                Assert.NotSame(replacement, net.TryGetPerPeerArray(node, "Arr", baseArray, ref idx, ref root));

            // No scope open -> refused, base assignment is the caller's job
            Assert.False(net.TryWritePerPeerRef(node, "Arr", replacement, ref idx, ref root));
        }
        finally
        {
            NetworkController.ForcePerPeerServerContextForTests = false;
        }
        node.Free();
    }
}
