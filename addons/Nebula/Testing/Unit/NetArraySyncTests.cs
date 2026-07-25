using Nebula;
using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Round-trip + protocol tests for NetArray's chunked (initial) network sync, focused on the sparse
/// encoding: initial sync sends the array length plus only non-default (index, value) pairs; the
/// client zero-fills the covered window. These drive the internal statics directly (same-assembly
/// seam) with a hand-built PeerSyncState + standalone NetBuffer -- no live NetRunner/WorldRunner.
/// </summary>
[NebulaUnitTest]
public class NetArraySyncTests
{
    // Drives a complete initial sync from `server` into a fresh (length-0) client: write one chunk,
    // deserialize it, ack it, repeat until the serializer reports nothing left. Mirrors the real
    // per-peer dict + tick-gated ack flow. Reports chunk count and the first chunk's byte size.
    private static NetArray<T> SyncToFreshClient<T>(NetArray<T> server, out int chunkCount, out int firstChunkBytes, int budget = 256) where T : struct
    {
        var peerId = UUID.NewUUID();
        var client = new NetArray<T>(server.Capacity);
        int tick = 1;
        chunkCount = 0;
        firstChunkBytes = -1;

        for (int guard = 0; guard < 100000; guard++)
        {
            var buf = new NetBuffer(8192, usePool: false);
            bool wrote;
            {
                ref var state = ref server.GetOrCreatePeerState(peerId);
                wrote = NetArray<T>.WriteChunkedSync(server, buf, ref state, budget, tick);
            }
            if (!wrote) break;

            if (chunkCount == 0) firstChunkBytes = buf.Length;
            chunkCount++;

            buf.ResetRead();
            client = NetArray<T>.NetworkDeserialize(null, default, buf, client);
            NetArray<T>.OnPeerAcknowledge(server, peerId, tick);
            tick++;
        }
        return client;
    }

    private static void AssertArraysEqual<T>(NetArray<T> expected, NetArray<T> actual) where T : struct
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actual[i]);
    }

    // Header sizes for the sparse chunked format: 1(flags)+4(totalLength)+4(windowStart)+4(windowEnd)+2(entryCount).
    private const int SparseHeaderBytes = 15;

    // 1. Fresh all-default array: one header-only window covering the whole array, no element payload.
    [NebulaUnitTest]
    public void FreshAllDefault_SingleHeaderOnlyChunk()
    {
        var server = new NetArray<byte>(1024, 1024); // length 1024, all zero
        var client = SyncToFreshClient(server, out int chunks, out int firstBytes);

        AssertArraysEqual(server, client);
        Assert.Equal(1024, client.Length);
        Assert.Equal(1, chunks);
        Assert.Equal(SparseHeaderBytes, firstBytes); // no per-element bytes -- the whole point
    }

    // 2. Densely populated array reconstructs exactly across multiple sparse chunks.
    [NebulaUnitTest]
    public void PartiallyPopulated_ExactReconstruction()
    {
        var server = new NetArray<byte>(1024, 1024);
        for (int i = 0; i < 1024; i += 5)
            server[i] = (byte)((i % 250) + 1); // ~205 non-default entries -> several chunks

        var client = SyncToFreshClient(server, out int chunks, out _);

        AssertArraysEqual(server, client);
        Assert.True(chunks >= 1);
    }

    // 3. Lightly populated (a few harvested) -> single tiny chunk, bandwidth proportional to entries.
    [NebulaUnitTest]
    public void LightlyPopulated_LowBandwidth()
    {
        var server = new NetArray<byte>(1024, 1024);
        server[10] = 1;
        server[500] = 1;
        server[900] = 1;

        var client = SyncToFreshClient(server, out int chunks, out int firstBytes);

        AssertArraysEqual(server, client);
        Assert.Equal(1, chunks);
        Assert.Equal(SparseHeaderBytes + 3 * (2 + 1), firstBytes); // header + 3 entries (uint16 index + byte)
    }

    // 7. Non-byte element type round-trips sparse, proving the ElementSize-generic encoding.
    [NebulaUnitTest]
    public void NonByteElementType_RoundTrips()
    {
        var server = new NetArray<int>(300, 300);
        server[5] = 12345;
        server[100] = -9999;
        server[299] = int.MaxValue;

        var client = SyncToFreshClient(server, out _, out _);
        AssertArraysEqual(server, client);
    }

    // 6. Tick-gated ack: a stale (older-tick) ack must not advance the frontier; a covering ack must.
    [NebulaUnitTest]
    public void StaleAck_DoesNotAdvanceFrontier()
    {
        var server = new NetArray<byte>(1024, 1024);
        for (int i = 0; i < 1024; i++)
            server[i] = (byte)((i % 250) + 1); // fully populated -> a real pending chunk

        var peerId = UUID.NewUUID();
        var buf = new NetBuffer(8192, usePool: false);
        {
            ref var st = ref server.GetOrCreatePeerState(peerId);
            NetArray<byte>.WriteChunkedSync(server, buf, ref st, 256, 5); // ChunkSentTick = 5
        }

        NetArray<byte>.OnPeerAcknowledge(server, peerId, 3); // stale: 3 < 5
        Assert.Equal(0, server.GetOrCreatePeerState(peerId).AckedUpToIndex);

        NetArray<byte>.OnPeerAcknowledge(server, peerId, 5); // covers the send
        Assert.True(server.GetOrCreatePeerState(peerId).AckedUpToIndex > 0);
    }

    // 4. A value changed after its chunk was sent (below the frontier) is resent via ChunkedWithDelta.
    [NebulaUnitTest]
    public void BelowFrontierResend_DeliversUpdate()
    {
        var server = new NetArray<byte>(1024, 1024);
        for (int i = 0; i < 1024; i++)
            server[i] = (byte)((i % 250) + 1); // fully populated -> multi-chunk, big first window

        var peerId = UUID.NewUUID();
        var client = new NetArray<byte>(1024);

        int firstWindowEnd;
        {
            var buf = new NetBuffer(8192, usePool: false);
            ref var st = ref server.GetOrCreatePeerState(peerId);
            NetArray<byte>.WriteChunkedSync(server, buf, ref st, 256, 1);
            firstWindowEnd = st.PendingSyncIndex;
            buf.ResetRead();
            client = NetArray<byte>.NetworkDeserialize(null, default, buf, client);
        }
        NetArray<byte>.OnPeerAcknowledge(server, peerId, 1);

        int idx = 3; // an already-sent index (< firstWindowEnd)
        Assert.True(idx < firstWindowEnd);
        server[idx] = 200;
        {
            ref var st = ref server.GetOrCreatePeerState(peerId);
            st.PendingDirty ??= new ulong[(server.Capacity + 63) / 64];
            st.PendingDirty[idx / 64] |= (1UL << (idx % 64)); // mark this peer's resend bit
        }

        {
            var buf = new NetBuffer(8192, usePool: false);
            ref var st = ref server.GetOrCreatePeerState(peerId);
            NetArray<byte>.WriteChunkedSync(server, buf, ref st, 256, 2);
            buf.ResetRead();
            client = NetArray<byte>.NetworkDeserialize(null, default, buf, client);
        }

        Assert.Equal((byte)200, client[idx]);
    }

    // 5. Resync zero-fill (read-side, hand-built): a window covering an index with NO entry resets a
    //    previously non-default client slot to default -- the mechanism that keeps resyncs correct.
    [NebulaUnitTest]
    public void ResyncWindow_ZeroFillsRevertedIndex()
    {
        var client = new NetArray<byte>(1024, 1024);
        client[42] = 7; // stale non-default value on the client

        var buf = new NetBuffer(64, usePool: false);
        NetWriter.WriteByte(buf, (byte)NetArraySyncFlags.Chunked);
        NetWriter.WriteInt32(buf, 1024); // totalLength
        NetWriter.WriteInt32(buf, 0);    // windowStart
        NetWriter.WriteInt32(buf, 1024); // windowEnd (covers index 42)
        NetWriter.WriteUInt16(buf, 0);   // entryCount = 0 -> 42 not re-sent
        buf.ResetRead();

        var result = NetArray<byte>.NetworkDeserialize(null, default, buf, client);
        Assert.Equal((byte)0, result[42]);
    }

    // 9. Restart after a resize/full-dirty (as NetPropertiesSerializer's restart block resets the peer)
    //    re-runs the sparse initial sync and reconstructs the NEW populated state -- the proxy for a
    //    late-joiner receiving already-harvested state through a fresh frontier.
    [NebulaUnitTest]
    public void RestartAfterResize_ResyncsPopulatedState()
    {
        var server = new NetArray<byte>(1024, 1024);
        server[7] = 5;

        var peerId = UUID.NewUUID();
        var client = new NetArray<byte>(1024);
        int tick = 1;
        DrainInitialSync(server, ref client, peerId, ref tick);
        AssertArraysEqual(server, client);

        // Server state changes, then the peer's initial-sync frontier is reset exactly as the
        // restart branch in NetworkSerialize (lines 517-526) does on a full-dirty/resize.
        server[500] = 9;
        server[900] = 1;
        {
            ref var st = ref server.GetOrCreatePeerState(peerId);
            st.InitialSyncComplete = false;
            st.AckedUpToIndex = 0;
            st.PendingSyncIndex = 0;
            st.HasPendingChunk = false;
            if (st.PendingDirty != null) System.Array.Clear(st.PendingDirty, 0, st.PendingDirty.Length);
        }

        DrainInitialSync(server, ref client, peerId, ref tick);
        AssertArraysEqual(server, client);
    }

    // Runs the write/deserialize/ack loop for an existing server+client+peer until sync completes.
    private static void DrainInitialSync<T>(NetArray<T> server, ref NetArray<T> client, UUID peerId, ref int tick, int budget = 256) where T : struct
    {
        for (int guard = 0; guard < 100000; guard++)
        {
            var buf = new NetBuffer(8192, usePool: false);
            bool wrote;
            {
                ref var state = ref server.GetOrCreatePeerState(peerId);
                wrote = NetArray<T>.WriteChunkedSync(server, buf, ref state, budget, tick);
            }
            if (!wrote) break;
            buf.ResetRead();
            client = NetArray<T>.NetworkDeserialize(null, default, buf, client);
            NetArray<T>.OnPeerAcknowledge(server, peerId, tick);
            tick++;
        }
    }

    // 8. Corrupt window bounds must not throw -- return the existing array (current validation contract).
    [NebulaUnitTest]
    public void CorruptWindow_ReturnsExistingWithoutThrow()
    {
        var client = new NetArray<byte>(1024, 1024);
        client[5] = 3;

        var buf = new NetBuffer(64, usePool: false);
        NetWriter.WriteByte(buf, (byte)NetArraySyncFlags.Chunked);
        NetWriter.WriteInt32(buf, 1024); // totalLength
        NetWriter.WriteInt32(buf, 0);    // windowStart
        NetWriter.WriteInt32(buf, 5000); // windowEnd > totalLength -> invalid
        NetWriter.WriteUInt16(buf, 0);
        buf.ResetRead();

        var result = NetArray<byte>.NetworkDeserialize(null, default, buf, client);
        Assert.NotNull(result);
        Assert.Equal((byte)3, result[5]); // untouched
    }
}
