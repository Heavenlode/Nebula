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

    // ------------------------------------------------------------------------------------------------
    // NetArray<bool>: bit-packed specialization. Elements are bits; the wire carries 64-bit WORDS with
    // an 8-word presence mask. 1024 bools -> ~128 B on the wire instead of ~1 KB. These reuse the same
    // generic harness (SyncToFreshClient / DrainInitialSync / AssertArraysEqual) since WriteChunkedSync
    // and NetworkDeserialize redirect to the bool path internally.
    // ------------------------------------------------------------------------------------------------

    // Bool chunked header: 1(flags)+4(totalLength)+4(startWord)+4(endWord) = 13, plus the per-8-word mask
    // bytes. A 1024-bit array is 16 words -> 2 mask bytes.
    private const int BoolChunkHeaderBytes = 13;

    // B1. All-false 1024: one chunk, header + 2 mask bytes, ZERO word payload (the whole point).
    [NebulaUnitTest]
    public void Bool_FreshAllFalse_HeaderPlusMaskOnly()
    {
        var server = new NetArray<bool>(1024, 1024); // 16 words, all zero
        var client = SyncToFreshClient(server, out int chunks, out int firstBytes);

        AssertArraysEqual(server, client);
        Assert.Equal(1024, client.Length);
        Assert.Equal(1, chunks);
        Assert.Equal(BoolChunkHeaderBytes + 2, firstBytes); // 13 + 2 mask bytes, no words
    }

    // B2. Lightly populated: only the touched words are sent. Bits 10 (w0), 500 (w7), 900 (w14) -> 3 words.
    [NebulaUnitTest]
    public void Bool_LightlyPopulated_SendsOnlyTouchedWords()
    {
        var server = new NetArray<bool>(1024, 1024);
        server[10] = true;
        server[500] = true;
        server[900] = true;

        var client = SyncToFreshClient(server, out int chunks, out int firstBytes);

        AssertArraysEqual(server, client);
        Assert.True(client[10] && client[500] && client[900]);
        Assert.Equal(1, chunks);
        Assert.Equal(BoolChunkHeaderBytes + 2 + 3 * 8, firstBytes); // header + 2 masks + 3 words
    }

    // B3. Fully populated 1024: caps at ~128 B of word payload (16 words) instead of ~1 KB.
    [NebulaUnitTest]
    public void Bool_FullyPopulated_CapsNear128Bytes()
    {
        var server = new NetArray<bool>(1024, 1024);
        for (int i = 0; i < 1024; i++) server[i] = true;

        var client = SyncToFreshClient(server, out int chunks, out int firstBytes);

        AssertArraysEqual(server, client);
        Assert.Equal(1, chunks);
        Assert.Equal(BoolChunkHeaderBytes + 2 + 16 * 8, firstBytes); // 13 + 2 + 128 = 143
        Assert.True(firstBytes < 1024); // the headline: far below the dense 1 KB byte encoding
    }

    // B4. Scattered pattern reconstructs exactly across MULTIPLE chunks (tiny budget forces word chunking).
    [NebulaUnitTest]
    public void Bool_MultiChunk_ExactReconstruction()
    {
        var server = new NetArray<bool>(4096, 4096); // 64 words
        for (int i = 0; i < 4096; i += 3) server[i] = true; // every word non-zero

        var client = SyncToFreshClient(server, out int chunks, out _, budget: 40);

        AssertArraysEqual(server, client);
        Assert.True(chunks > 1); // 40-byte budget can't fit 64 words in one chunk
    }

    // B5. Delta (post-initial) sends only changed WORDS and round-trips + reports changed indices.
    [NebulaUnitTest]
    public void Bool_Delta_SendsOnlyChangedWords()
    {
        var server = new NetArray<bool>(1024, 1024);
        server[100] = true; // word 1
        server[900] = true; // word 14

        // Hand-build the peer's pending set exactly as MergeDirtyIntoPending would after those writes.
        var st = PeerSyncState.Create();
        st.PendingDirty = new ulong[(server.Capacity + 63) / 64];
        st.PendingDirty[100 / 64] |= 1UL << (100 % 64);
        st.PendingDirty[900 / 64] |= 1UL << (900 % 64);

        var buf = new NetBuffer(8192, usePool: false);
        NetArray<bool>.WriteDeltaSyncBool(server, buf, ref st, 1);
        // flags(1) + count(2) + 2 words * (wordIndex 2 + word 8) = 23
        Assert.Equal(1 + 2 + 2 * (2 + 8), buf.Length);

        var client = new NetArray<bool>(1024, 1024); // all false
        buf.ResetRead();
        var result = NetArray<bool>.NetworkDeserialize(null, default, buf, client);

        Assert.True(result[100] && result[900]);
        Assert.Equal(2, result.LastChangeInfo.ChangedIndices.Length);
    }

    // B6. A resync window that omits a previously-set bit must clear it (zero-fill correctness for bool).
    [NebulaUnitTest]
    public void Bool_ResyncWindow_ClearsRevertedBit()
    {
        var server = new NetArray<bool>(1024, 1024); // all false now
        var peerId = UUID.NewUUID();
        var client = new NetArray<bool>(1024, 1024);
        client[42] = true; // stale set bit on the client

        int tick = 1;
        DrainInitialSync(server, ref client, peerId, ref tick);
        Assert.False(client[42]); // window covering word 0 with mask bit clear resets it
        AssertArraysEqual(server, client);
    }

    // B7. Restart after resize/full-dirty re-runs the sparse initial sync and reconstructs new bits.
    [NebulaUnitTest]
    public void Bool_RestartAfterChange_ResyncsPopulatedBits()
    {
        var server = new NetArray<bool>(1024, 1024);
        server[7] = true;

        var peerId = UUID.NewUUID();
        var client = new NetArray<bool>(1024);
        int tick = 1;
        DrainInitialSync(server, ref client, peerId, ref tick);
        AssertArraysEqual(server, client);

        server[500] = true;
        server[900] = true;
        {
            ref var stt = ref server.GetOrCreatePeerState(peerId);
            stt.InitialSyncComplete = false;
            stt.AckedUpToIndex = 0;
            stt.PendingSyncIndex = 0;
            stt.HasPendingChunk = false;
            if (stt.PendingDirty != null) System.Array.Clear(stt.PendingDirty, 0, stt.PendingDirty.Length);
        }

        DrainInitialSync(server, ref client, peerId, ref tick);
        AssertArraysEqual(server, client);
    }

    // B8. Shrinking resize is reported via DeletedValues and clears trailing bits.
    [NebulaUnitTest]
    public void Bool_Shrink_ReportsDeletedAndClearsTail()
    {
        var server = new NetArray<bool>(1024, 1024);
        var peerId = UUID.NewUUID();
        var client = new NetArray<bool>(1024);
        int tick = 1;

        server[900] = true;
        DrainInitialSync(server, ref client, peerId, ref tick);
        Assert.True(client[900]);

        // Shrink below the set bit and resync.
        server.SetLength(512);
        {
            ref var stt = ref server.GetOrCreatePeerState(peerId);
            stt.InitialSyncComplete = false;
            stt.AckedUpToIndex = 0;
            stt.PendingSyncIndex = 0;
            stt.HasPendingChunk = false;
            if (stt.PendingDirty != null) System.Array.Clear(stt.PendingDirty, 0, stt.PendingDirty.Length);
        }
        DrainInitialSync(server, ref client, peerId, ref tick);

        Assert.Equal(512, client.Length);
        AssertArraysEqual(server, client);
    }

    // ------------------------------------------------------------------------------------------------
    // Resend/ack settling: a NetArray that changes once and then goes quiet must STOP resending its
    // delta once acked, even when the client's ack lags by several ticks. Regression for the bug where
    // the send-tick stamp advanced on every resend so the lagging ack never caught up -> perpetual
    // per-tick resend. These replay NetworkSerialize's sequence (merge -> resend -> end-of-tick clear)
    // against a hand-built peer with a lagging ack.
    // ------------------------------------------------------------------------------------------------

    // Runs a "changed once, then quiet" array through per-tick export + a lagging ack; returns the tick
    // the pending set cleared (or -1 if it never settled within maxTicks).
    private static int SettleTick<T>(NetArray<T> server, UUID peerId, int ackLatency, int maxTicks = 40) where T : struct
    {
        static bool AnyPending(in PeerSyncState s)
        {
            if (s.PendingDirty == null) return false;
            for (int i = 0; i < s.PendingDirty.Length; i++) if (s.PendingDirty[i] != 0) return true;
            return false;
        }

        for (int t = 1; t <= maxTicks; t++)
        {
            {
                ref var st = ref server.GetOrCreatePeerState(peerId);
                server.MergeDirtyIntoPending(ref st, t);            // stamps LastSendTick only on the dirty tick
                if (AnyPending(st))
                    NetArray<T>.WriteDeltaSync(server, new NetBuffer(512, usePool: false), ref st, t); // resend
            }
            server.ClearDirty();                                    // OnExportComplete end-of-tick
            int ackTick = t - ackLatency;
            if (ackTick >= 1) NetArray<T>.OnPeerAcknowledge(server, peerId, ackTick);

            if (!AnyPending(server.GetOrCreatePeerState(peerId))) return t;
        }
        return -1;
    }

    // Puts `server` in a completed-initial-sync state for `peerId` (so exports take the delta path).
    private static void CompleteInitialSync<T>(NetArray<T> server, UUID peerId) where T : struct
    {
        ref var st = ref server.GetOrCreatePeerState(peerId);
        st.InitialSyncComplete = true;
        st.LastSyncedLength = server.Length;
        st.AckedUpToIndex = (server.Length + 63) >> 6;
    }

    [NebulaUnitTest]
    public void Delta_ChangedOnce_SettlesUnderAckLatency_Byte()
    {
        var server = new NetArray<byte>(1024, 1024);
        var peerId = UUID.NewUUID();
        CompleteInitialSync(server, peerId);

        server[10] = 42; // one harvest-like change
        int settledAt = SettleTick(server, peerId, ackLatency: 2);

        Assert.True(settledAt > 0, "byte delta never settled under ack latency -> perpetual resend");
    }

    [NebulaUnitTest]
    public void Delta_ChangedOnce_SettlesUnderAckLatency_Bool()
    {
        var server = new NetArray<bool>(1024, 1024);
        var peerId = UUID.NewUUID();
        CompleteInitialSync(server, peerId);

        server[10] = true;
        server[11] = true; // second change in the SAME word -> must not perpetually resend
        int settledAt = SettleTick(server, peerId, ackLatency: 2);

        Assert.True(settledAt > 0, "bool delta never settled under ack latency -> perpetual resend");
    }

    // Initial-sync chunk progress must also complete under a lagging ack (the ChunkSentTick twin).
    [NebulaUnitTest]
    public void InitialSync_CompletesUnderAckLatency()
    {
        var server = new NetArray<byte>(1024, 1024);
        for (int i = 0; i < 1024; i++) server[i] = (byte)((i % 250) + 1); // multi-chunk under tiny budget

        var peerId = UUID.NewUUID();
        var client = new NetArray<byte>(1024);
        const int latency = 2;
        int tick = 1;
        var sentTicks = new System.Collections.Generic.Queue<int>();

        bool completed = false;
        for (int guard = 0; guard < 100000 && !completed; guard++)
        {
            {
                ref var st = ref server.GetOrCreatePeerState(peerId);
                var buf = new NetBuffer(8192, usePool: false);
                bool wrote = NetArray<byte>.WriteChunkedSync(server, buf, ref st, 64, tick); // 64-byte budget
                if (wrote)
                {
                    buf.ResetRead();
                    client = NetArray<byte>.NetworkDeserialize(null, default, buf, client);
                    sentTicks.Enqueue(tick);
                }
                completed = st.InitialSyncComplete;
            }
            // Deliver the ack that is `latency` ticks old.
            if (sentTicks.Count > latency) NetArray<byte>.OnPeerAcknowledge(server, peerId, sentTicks.Dequeue());
            tick++;
        }
        // Flush remaining in-flight acks.
        while (!completed && sentTicks.Count > 0)
        {
            NetArray<byte>.OnPeerAcknowledge(server, peerId, sentTicks.Dequeue());
            completed = server.GetOrCreatePeerState(peerId).InitialSyncComplete;
        }

        Assert.True(completed, "initial sync never completed under ack latency -> chunk progress stalled");
        AssertArraysEqual(server, client);
    }

    // B9. BSON persistence round-trips bool arrays (save/reload path).
    [NebulaUnitTest]
    public void Bool_BsonRoundTrip()
    {
        var arr = new NetArray<bool>(200, 200);
        arr[0] = true; arr[63] = true; arr[64] = true; arr[199] = true;

        var bson = BsonTypeHelper.ToBson(arr);
        var restored = BsonTypeHelper.ToNetArray<bool>(bson);

        Assert.Equal(arr.Length, restored.Length);
        for (int i = 0; i < arr.Length; i++)
            Assert.Equal(arr[i], restored[i]);
    }

    // ------------------------------------------------------------------------------------------------
    // Per-peer forking (NetProperty.PerPeerState on a NetArray). The server keeps one instance per
    // diverged peer; ForkForPeer clones the contents AND MOVES that peer's PeerSyncState across, so a
    // peer that forks without mutating is already fully synced and sends nothing. Skipping the move
    // would leave the fork with InitialSyncComplete == false and re-chunk the entire array to a client
    // that already holds identical bytes.
    // ------------------------------------------------------------------------------------------------

    private static bool AnyPendingBits(in PeerSyncState s)
    {
        if (s.PendingDirty == null) return false;
        for (int i = 0; i < s.PendingDirty.Length; i++) if (s.PendingDirty[i] != 0) return true;
        return false;
    }

    // F1. THE regression test for the sync-state transplant: fork after a completed sync, change
    //     nothing, and the fork must have nothing to say.
    [NebulaUnitTest]
    public void Fork_AfterInitialSync_SendsNothing()
    {
        var server = new NetArray<byte>(1024, 1024);
        for (int i = 0; i < 1024; i++) server[i] = (byte)((i % 250) + 1);

        var peerId = UUID.NewUUID();
        var client = new NetArray<byte>(1024);
        int tick = 1;
        DrainInitialSync(server, ref client, peerId, ref tick);
        server.OnExportComplete(); // end-of-tick global clear
        AssertArraysEqual(server, client);

        var fork = NetArray<byte>.ForkForPeer(server, peerId);

        ref var forkState = ref fork.GetOrCreatePeerState(peerId);
        Assert.True(forkState.InitialSyncComplete,
            "fork lost the peer's sync state -> the whole array would be re-chunked to a client that already has it");
        Assert.False(AnyPendingBits(forkState), "fork has pending elements despite no mutation");

        // And the base no longer tracks a peer it no longer serves.
        Assert.False(server.HasPeerStateForTests(peerId));
    }

    // F2. Fork then mutate one element -> an ordinary delta carrying that element, NOT a full resync,
    //     and the client reconstructs it exactly.
    [NebulaUnitTest]
    public void Fork_ThenMutate_SendsDeltaNotResync()
    {
        var server = new NetArray<byte>(1024, 1024);
        for (int i = 0; i < 1024; i++) server[i] = (byte)((i % 250) + 1);

        var peerId = UUID.NewUUID();
        var client = new NetArray<byte>(1024);
        int tick = 1;
        DrainInitialSync(server, ref client, peerId, ref tick);
        server.OnExportComplete();

        var fork = NetArray<byte>.ForkForPeer(server, peerId);
        fork[7] = 200; // this peer's secret

        var buf = new NetBuffer(8192, usePool: false);
        {
            ref var st = ref fork.GetOrCreatePeerState(peerId);
            fork.MergeDirtyIntoPending(ref st, tick);
            Assert.True(st.InitialSyncComplete, "fork fell back to chunked sync");
            NetArray<byte>.WriteDeltaSync(fork, buf, ref st, tick);
        }

        // A full 1024-element resync could not fit in a delta this small.
        Assert.True(buf.Length < 64, $"expected a single-element delta, got {buf.Length} bytes");

        buf.ResetRead();
        client = NetArray<byte>.NetworkDeserialize(null, default, buf, client);
        Assert.Equal((byte)200, client[7]);
        AssertArraysEqual(fork, client);
    }

    // F3. Two peers forked from the same base diverge independently and each client reconstructs only
    //     its own view.
    [NebulaUnitTest]
    public void Fork_TwoPeers_ReconstructDifferentArrays()
    {
        var server = new NetArray<byte>(256, 256);
        for (int i = 0; i < 256; i++) server[i] = 1;

        var peerA = UUID.NewUUID();
        var peerB = UUID.NewUUID();
        var clientA = new NetArray<byte>(256);
        var clientB = new NetArray<byte>(256);
        int tickA = 1, tickB = 1;
        DrainInitialSync(server, ref clientA, peerA, ref tickA);
        DrainInitialSync(server, ref clientB, peerB, ref tickB);
        server.OnExportComplete();

        var forkA = NetArray<byte>.ForkForPeer(server, peerA);
        var forkB = NetArray<byte>.ForkForPeer(server, peerB);
        forkA[3] = 111;
        forkB[3] = 222;

        SendDelta(forkA, peerA, ref clientA, tickA);
        SendDelta(forkB, peerB, ref clientB, tickB);

        Assert.Equal((byte)111, clientA[3]);
        Assert.Equal((byte)222, clientB[3]);
        Assert.Equal((byte)1, server[3]); // base untouched by either fork
    }

    // F4. Acks must land on the fork: routed to the base instead, the fork's pending mask never clears
    //     and it resends the same delta forever (the NetPropertiesSerializer.Acknowledge routing).
    [NebulaUnitTest]
    public void Fork_AckClearsForkPendingMask()
    {
        var server = new NetArray<byte>(256, 256);
        var peerId = UUID.NewUUID();
        CompleteInitialSync(server, peerId);

        var fork = NetArray<byte>.ForkForPeer(server, peerId);
        fork[9] = 77;

        {
            ref var st = ref fork.GetOrCreatePeerState(peerId);
            fork.MergeDirtyIntoPending(ref st, 5);
            NetArray<byte>.WriteDeltaSync(fork, new NetBuffer(512, usePool: false), ref st, 5);
            Assert.True(AnyPendingBits(st));
        }
        fork.OnExportComplete();

        NetArray<byte>.OnPeerAcknowledge(fork, peerId, 5);

        Assert.False(AnyPendingBits(fork.GetOrCreatePeerState(peerId)),
            "ack did not clear the fork's pending mask -> perpetual delta resend");
    }

    // F5. Bool takes the separate bit-packed path (_bits, not _data), so the clone must carry it.
    [NebulaUnitTest]
    public void Fork_Bool_ClonesBitPackedContents()
    {
        var server = new NetArray<bool>(256, 256);
        server[0] = true; server[63] = true; server[64] = true; server[255] = true;

        var peerId = UUID.NewUUID();
        var fork = NetArray<bool>.ForkForPeer(server, peerId);

        Assert.Equal(server.Length, fork.Length);
        for (int i = 0; i < server.Length; i++)
            Assert.Equal(server[i], fork[i]);

        // Diverging the fork leaves the base alone.
        fork[1] = true;
        Assert.True(fork[1]);
        Assert.False(server[1]);
    }

    // F6. A base mutation made BEFORE the fork (same tick, not yet exported) must survive the fork -
    //     it is in the copied contents but the peer has not received it, so its dirty bit has to come
    //     along. (Base mutations AFTER the fork deliberately never reach this peer - see F7.)
    [NebulaUnitTest]
    public void Fork_CarriesUnexportedBaseMutation()
    {
        var server = new NetArray<byte>(256, 256);
        var peerId = UUID.NewUUID();
        CompleteInitialSync(server, peerId);
        var client = new NetArray<byte>(256, 256);

        server[12] = 55;                 // dirtied this tick, not exported yet
        var fork = NetArray<byte>.ForkForPeer(server, peerId);

        SendDelta(fork, peerId, ref client, 3);
        Assert.Equal((byte)55, client[12]);
    }

    // F7. Once forked, the peer is divorced from the base: later base mutations never reach it.
    //     This pins the documented contract that differs from per-peer primitives.
    [NebulaUnitTest]
    public void Fork_IsDivorcedFromLaterBaseMutations()
    {
        var server = new NetArray<byte>(256, 256);
        var peerId = UUID.NewUUID();
        CompleteInitialSync(server, peerId);
        var client = new NetArray<byte>(256, 256);

        var fork = NetArray<byte>.ForkForPeer(server, peerId);
        server[20] = 99; // base changes AFTER the fork

        SendDelta(fork, peerId, ref client, 3);
        Assert.Equal((byte)0, client[20]);
        Assert.Equal((byte)0, fork[20]);
    }

    // Exports one delta from `arr` for `peerId` into `client`.
    private static void SendDelta<T>(NetArray<T> arr, UUID peerId, ref NetArray<T> client, int tick) where T : struct
    {
        var buf = new NetBuffer(8192, usePool: false);
        {
            ref var st = ref arr.GetOrCreatePeerState(peerId);
            arr.MergeDirtyIntoPending(ref st, tick);
            NetArray<T>.WriteDeltaSync(arr, buf, ref st, tick);
        }
        arr.OnExportComplete();
        buf.ResetRead();
        client = NetArray<T>.NetworkDeserialize(null, default, buf, client);
    }
}
