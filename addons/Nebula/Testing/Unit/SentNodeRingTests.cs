using System.Collections.Generic;
using Nebula;
using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// The per-peer ack routing ring: a tick's slot must hand back exactly the nodes registered
/// under that tick, never a wrapped older tick's, and must do so without allocating once warm.
/// </summary>
[NebulaUnitTest]
public class SentNodeRingTests
{
    private static NetworkController Controller()
    {
        var node = new NetNode();
        return node.Network;
    }

    [NebulaUnitTest]
    public void BeginAddTryGet_RoundTrips()
    {
        var ring = new SentNodeRing();
        var a = Controller();
        var b = Controller();

        ring.Begin(5);
        ring.Add(a);
        ring.Add(b);

        Assert.True(ring.TryGet(5, out var nodes));
        Assert.Equal(2, nodes.Count);
        Assert.Same(a, nodes[0]);
        Assert.Same(b, nodes[1]);

        a.RawNode.Free();
        b.RawNode.Free();
    }

    [NebulaUnitTest]
    public void UnbegunTick_IsNotFound()
    {
        var ring = new SentNodeRing();
        ring.Begin(5);

        Assert.False(ring.TryGet(4, out _));
        Assert.False(ring.TryGet(6, out _));
        Assert.False(ring.TryGet(-1, out _));
    }

    [NebulaUnitTest]
    public void TickZero_IsAValidSlot()
    {
        // Stamps start at -1 precisely so that tick 0 (the first tick a young world can be
        // acked for) is distinguishable from "never written".
        var ring = new SentNodeRing();
        Assert.False(ring.TryGet(0, out _));

        var a = Controller();
        ring.Begin(0);
        ring.Add(a);
        Assert.True(ring.TryGet(0, out var nodes));
        Assert.Single(nodes);

        a.RawNode.Free();
    }

    [NebulaUnitTest]
    public void WrappedTick_IsNotMistakenForItsSuccessor()
    {
        var ring = new SentNodeRing();
        var a = Controller();
        var b = Controller();

        ring.Begin(5);
        ring.Add(a);
        ring.Begin(5 + SentNodeRing.Depth); // same slot
        ring.Add(b);

        Assert.False(ring.TryGet(5, out _));
        Assert.True(ring.TryGet(5 + SentNodeRing.Depth, out var nodes));
        Assert.Single(nodes);
        Assert.Same(b, nodes[0]);

        a.RawNode.Free();
        b.RawNode.Free();
    }

    [NebulaUnitTest]
    public void Begin_ClearsAReusedSlot_AndReusesItsList()
    {
        var ring = new SentNodeRing();
        var a = Controller();

        ring.Begin(1);
        ring.Add(a);
        Assert.True(ring.TryGet(1, out var first));

        ring.Begin(1 + SentNodeRing.Depth);
        Assert.True(ring.TryGet(1 + SentNodeRing.Depth, out var second));

        Assert.Same(first, second);   // no allocation on reuse
        Assert.Empty(second);         // and nothing carried over

        a.RawNode.Free();
    }

    [NebulaUnitTest]
    public void Reset_ForgetsEveryTick()
    {
        var ring = new SentNodeRing();
        var a = Controller();
        for (int t = 0; t < SentNodeRing.Depth; t++)
        {
            ring.Begin(t);
            ring.Add(a);
        }

        ring.Reset();

        for (int t = 0; t < SentNodeRing.Depth; t++)
        {
            Assert.False(ring.TryGet(t, out _));
        }

        a.RawNode.Free();
    }
}
