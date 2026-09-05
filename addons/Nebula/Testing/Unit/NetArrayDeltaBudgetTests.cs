using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// A NetArray delta respects the byte budget the serializer hands it, and what it cannot fit is
/// deferred -- never dropped, never left in a pending set an ack would erase.
///
/// The failure this fences: WriteDeltaSync wrote every pending element regardless of budget. A few
/// hundred dirty elements overran the 1536-byte packet buffer every tick ("Buffer overflow: cannot
/// write 2 bytes at position 1536"), and the property never shipped at all.
/// </summary>
[NebulaUnitTest]
public class NetArrayDeltaBudgetTests
{
    private const int Length = 4096;

    /// <summary>A server array with <paramref name="dirtyCount"/> changed slots, and a peer whose
    /// initial sync is already complete so every change goes out as a delta.</summary>
    private static (NetArray<short> server, UUID peerId) DirtyServer(int dirtyCount)
    {
        var server = new NetArray<short>(Length, Length);
        for (int i = 0; i < dirtyCount; i++) server[i * 3] = (short)(i + 1);

        var peerId = UUID.NewUUID();
        ref var state = ref server.GetOrCreatePeerState(peerId);
        state.InitialSyncComplete = true;
        state.LastSyncedLength = Length;
        server.MergeDirtyIntoPending(ref state, 1);
        // What the world runner does at the end of the tick: the global dirty mask has been absorbed
        // into every peer's pending set and is cleared. Without this every later merge would re-add
        // all 1000 bits and the peer would receive the first budget's worth forever.
        server.OnExportComplete();
        return (server, peerId);
    }

    private static int CountBits(ulong[] mask)
    {
        if (mask == null) return 0;
        int n = 0;
        foreach (ulong w in mask) n += System.Numerics.BitOperations.PopCount(w);
        return n;
    }

    [NebulaUnitTest]
    public void DeltaStaysWithinBudgetAndDefersTheRest()
    {
        var (server, peerId) = DirtyServer(1000);
        ref var state = ref server.GetOrCreatePeerState(peerId);

        const int budget = 256;
        var buf = new NetBuffer(8192, usePool: false);
        NetArray<short>.WriteDeltaSync(server, buf, ref state, 1, budget);

        Assert.True(buf.WritePosition <= budget, $"delta wrote {buf.WritePosition} bytes against a budget of {budget}");
        int entrySize = 2 + NetArray<short>.ElementSize;
        int expected = (budget - 3) / entrySize;
        Assert.Equal(expected, CountBits(state.PendingDirty));
        Assert.Equal(1000 - expected, CountBits(state.DeferredDirty));
    }

    [NebulaUnitTest]
    public void EveryElementReachesThePeerAcrossTicks()
    {
        var (server, peerId) = DirtyServer(1000);
        var client = new NetArray<short>(Length, Length);

        const int budget = 256;
        int ticks = 0;
        while (ticks < 200)
        {
            ticks++;
            var buf = new NetBuffer(8192, usePool: false);
            {
                ref var state = ref server.GetOrCreatePeerState(peerId);
                NetArray<short>.WriteDeltaSync(server, buf, ref state, ticks, budget);
            }
            Assert.True(buf.WritePosition <= budget);

            buf.ResetRead();
            client = NetArray<short>.NetworkDeserialize(null, default, buf, client);

            // The peer acks this tick: everything written this tick is cleared, deferred survives.
            NetArray<short>.OnPeerAcknowledge(server, peerId, ticks);

            // Next tick's merge brings the deferred elements back.
            ref var next = ref server.GetOrCreatePeerState(peerId);
            server.MergeDirtyIntoPending(ref next, ticks + 1);
            if (CountBits(next.PendingDirty) == 0) break;
        }

        Assert.True(ticks > 1, "a 1000-element delta fit in one 256-byte packet, which cannot be");
        for (int i = 0; i < 1000; i++)
            Assert.Equal((short)(i + 1), client[i * 3]);
    }

    [NebulaUnitTest]
    public void BoolDeltaIsBudgetedByWord()
    {
        var server = new NetArray<bool>(Length, Length);
        for (int i = 0; i < Length; i += 7) server[i] = true;

        var peerId = UUID.NewUUID();
        ref var state = ref server.GetOrCreatePeerState(peerId);
        state.InitialSyncComplete = true;
        state.LastSyncedLength = Length;
        server.MergeDirtyIntoPending(ref state, 1);
        server.OnExportComplete();

        const int budget = 64;
        var buf = new NetBuffer(8192, usePool: false);
        NetArray<bool>.WriteDeltaSyncBool(server, buf, ref state, 1, budget);
        Assert.True(buf.WritePosition <= budget, $"bool delta wrote {buf.WritePosition} bytes");
        Assert.True(CountBits(state.DeferredDirty) > 0, "nothing was deferred from a 64-word delta at 64 bytes");
    }
}
