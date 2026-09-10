using System;
using Nebula;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// The props-section memo: encode a primitive segment once per signature per node per
/// tick, share the bytes, replay the stamps. The one failure mode that matters is
/// silently WRONG bytes for a signature-matched peer, so every test here decodes or
/// byte-compares — a passing memo test asserts equality of output, never just counters.
///
/// The runner has no distinct NetPeers (the ENet ctor is internal and P/Invokes on the
/// handle), so "two peers with equal state" is modeled as two Exports of the same peer in
/// one tick — the memo keys on the signature, not the peer, so this exercises the same
/// paths. Delta-path signatures need <c>ForceRingCaptureForTests</c>, because Begin()
/// captures the baseline ring only on a server.
/// </summary>
[NebulaUnitTest]
public class PropsMemoTests
{
    private sealed class Fixture : IDisposable
    {
        public WorldRunner World;
        public NetPeer Peer;
        public UUID PeerId;
        public NetNode Node;
        public NetPropertiesSerializer Serializer;

        /// <summary>
        /// All fixtures in one test share a single peer UUID: the runner can only mint
        /// ENet peer id 0 (the struct ctor is internal), so a second fixture minting its
        /// own UUID would hijack PeerIds[0] and break the first fixture's interest
        /// lookup. Worlds and serializers stay per-fixture; only the identity is shared.
        /// </summary>
        public Fixture(bool memoOn, UUID sharedPeerId, params SerialVariantType[] propTypes)
            : this(memoOn, sharedPeerId, propTypes, null) { }

        public Fixture(bool memoOn, UUID sharedPeerId, SerialVariantType[] propTypes, float[] quantizeSteps)
        {
            World = new WorldRunner();
            Peer = default;
            PeerId = sharedPeerId;
            NetRunner.Instance.PeerIds[0] = PeerId;
            World.CreatePeerStateForTests(Peer, PeerId);

            Node = new NetNode();
            Node.Network.InterestLayers[PeerId] = 1;
            Node.Network.CurrentWorld = World;
            for (var i = 0; i < propTypes.Length; i++)
            {
                Node.Network.CachedProperties[i] = new PropertyCache { Type = propTypes[i], IntValue = 40 + i };
            }
            Serializer = new NetPropertiesSerializer(Node.Network, propTypes, null, quantizeSteps)
            {
                MemoOverrideForTests = memoOn,
                ForceRingCaptureForTests = true,
            };
            World.SetClientSpawnState(Node.Network.NetId, Peer, WorldRunner.ClientSpawnState.Spawning);
        }

        public void SetInt(int propIndex, int value)
        {
            var cache = Node.Network.CachedProperties[propIndex];
            cache.IntValue = value;
            Node.Network.CachedProperties[propIndex] = cache;
        }

        public void Dispose()
        {
            NetRunner.Instance.PeerIds.Remove(0);
            Node.Free();
            World.Free();
        }
    }

    private static NetBuffer Buffer() => new(512, usePool: false);

    // 1. The core promise: a signature-matched export is served from the memo and its
    //    bytes are IDENTICAL to the freshly encoded ones.
    [NebulaUnitTest]
    public void SameSignature_IsHit_AndByteIdentical()
    {
        var peerId = UUID.NewUUID();
        using var f = new Fixture(memoOn: true, peerId, SerialVariantType.Int, SerialVariantType.Int);

        f.World.CurrentTick = 1;
        f.Node.Network.DirtyMask = 0b11;
        f.Serializer.Begin();

        var first = Buffer();
        Assert.Equal(ExportResult.Written, f.Serializer.Export(f.World, f.Peer, first, int.MaxValue));
        Assert.Equal(0, f.Serializer.MemoHitsForTests);

        var second = Buffer();
        Assert.Equal(ExportResult.Written, f.Serializer.Export(f.World, f.Peer, second, int.MaxValue));
        Assert.Equal(1, f.Serializer.MemoHitsForTests);
        Assert.True(first.WrittenSpan.SequenceEqual(second.WrittenSpan));
    }

    // 2. Memo OFF is the identity baseline: with the flag off, repeated exports never hit
    //    and still produce identical bytes (determinism of the slow path itself).
    [NebulaUnitTest]
    public void MemoOff_NeverHits_SameBytes()
    {
        var peerId = UUID.NewUUID();
        using var f = new Fixture(memoOn: false, peerId, SerialVariantType.Int);

        f.World.CurrentTick = 1;
        f.Node.Network.DirtyMask = 0b1;
        f.Serializer.Begin();

        var a = Buffer(); f.Serializer.Export(f.World, f.Peer, a, int.MaxValue);
        var b = Buffer(); f.Serializer.Export(f.World, f.Peer, b, int.MaxValue);

        Assert.Equal(0, f.Serializer.MemoHitsForTests);
        Assert.True(a.WrittenSpan.SequenceEqual(b.WrittenSpan));
    }

    // 3. Stamp replay parity: a memo-served export must leave the peer's lossy/pending
    //    state exactly as the writer would have. Drive the delta path (ring forced, ack
    //    committed) so DeltaChain/LossyMask actually move, then compare a memo-on fixture
    //    against a memo-off twin across the SAME script.
    [NebulaUnitTest]
    public void StampReplay_MatchesSlowPath()
    {
        var peerId = UUID.NewUUID();
        using var on = new Fixture(memoOn: true, peerId, SerialVariantType.Float);
        using var off = new Fixture(memoOn: false, peerId, SerialVariantType.Float);

        foreach (var f in new[] { on, off })
        {
            // Tick 1: absolute, committed, acked — arms delta eligibility for tick 2.
            f.World.CurrentTick = 1;
            f.Node.Network.CachedProperties[0] = new PropertyCache { Type = SerialVariantType.Float, FloatValue = 1.0f };
            f.Node.Network.DirtyMask = 0b1;
            f.Serializer.Begin();
            var t1 = Buffer();
            Assert.Equal(ExportResult.Written, f.Serializer.Export(f.World, f.Peer, t1, int.MaxValue));
            f.Serializer.CommitExport(f.World, f.Peer, 1);
            f.Serializer.Acknowledge(f.World, f.Peer, 1);

            // Tick 2: a lossy delta (0.01 is not half-exact), exported TWICE — the second
            // is the memo hit on the "on" fixture and a plain re-encode on the "off" one.
            f.World.CurrentTick = 2;
            f.Node.Network.CachedProperties[0] = new PropertyCache { Type = SerialVariantType.Float, FloatValue = 1.01f };
            f.Node.Network.DirtyMask = 0b1;
            f.Serializer.Begin();
            var t2a = Buffer(); f.Serializer.Export(f.World, f.Peer, t2a, int.MaxValue);
            var t2b = Buffer(); f.Serializer.Export(f.World, f.Peer, t2b, int.MaxValue);
            Assert.True(t2a.WrittenSpan.SequenceEqual(t2b.WrittenSpan));
        }

        Assert.Equal(1, on.Serializer.MemoHitsForTests);
        Assert.Equal(0, off.Serializer.MemoHitsForTests);

        // The observable stamps must agree between the fixtures.
        Assert.Equal(off.Serializer.LossyByteForTests(off.PeerId, 0),
                     on.Serializer.LossyByteForTests(on.PeerId, 0));
        Assert.Equal(off.Serializer.PendingDirtyByteForTests(off.PeerId, 0),
                     on.Serializer.PendingDirtyByteForTests(on.PeerId, 0));
    }

    // 4. P2 at the eligibility level: an INetValue-typed primitive in the SCENE does not
    //    poison sections that do not write it — and one that WOULD write it is ineligible.
    //    Actually writing an Object-typed primitive is unsupported in the Protocol-free
    //    harness (its serializer registry is empty), so the write path for P2 is covered
    //    by the NEBULA_VERIFY_MEMO soak against the real protocol, where node references
    //    exercise it every run.
    [NebulaUnitTest]
    public void ObjectTypedPrimitive_DoesNotPoisonOtherSections()
    {
        var peerId = UUID.NewUUID();
        using var f = new Fixture(memoOn: true, peerId, SerialVariantType.Int, SerialVariantType.Object);

        // Dirty ONLY the plain int: the Object prop is outside the written mask, so the
        // section is eligible and the second export hits.
        f.World.CurrentTick = 1;
        f.Node.Network.DirtyMask = 0b01;
        f.Serializer.Begin();

        var a = Buffer(); f.Serializer.Export(f.World, f.Peer, a, int.MaxValue);
        var b = Buffer(); f.Serializer.Export(f.World, f.Peer, b, int.MaxValue);

        Assert.Equal(1, f.Serializer.MemoHitsForTests);
        Assert.True(a.WrittenSpan.SequenceEqual(b.WrittenSpan));
    }

    // 5. P3: a follower whose budget cannot take the whole blob must not be served from
    //    the memo — it takes the self-limiting writer and banks what did not fit.
    [NebulaUnitTest]
    public void TightBudgetFollower_TakesSlowPath()
    {
        var peerId = UUID.NewUUID();
        using var f = new Fixture(memoOn: true, peerId,
            SerialVariantType.Int, SerialVariantType.Int, SerialVariantType.Int);

        f.World.CurrentTick = 1;
        f.Node.Network.DirtyMask = 0b111;
        f.Serializer.Begin();

        var full = Buffer();
        Assert.Equal(ExportResult.Written, f.Serializer.Export(f.World, f.Peer, full, int.MaxValue));

        // Budget one bit below the full section: the signature matches but P3 fails →
        // no hit, and the writer self-limits exactly as before the memo existed (some
        // props ship, the last rewinds into PendingDirtyMask, result is Partial).
        var tightBudget = full.WrittenBits - 1;
        var tight = Buffer();
        var result = f.Serializer.Export(f.World, f.Peer, tight, tightBudget);
        Assert.Equal(0, f.Serializer.MemoHitsForTests);
        Assert.Equal(ExportResult.Partial, result);
        Assert.True(tight.WrittenBits <= tightBudget);
        Assert.NotEqual(0, f.Serializer.PendingDirtyByteForTests(f.PeerId, 0));
    }

    // 6. Begin() resets the memo: a changed value the next tick must never be served a
    //    stale blob. (Applying a payload needs the real Protocol registry, so freshness
    //    is pinned by bytes: a stale blob would make tick 2's output equal tick 1's.)
    [NebulaUnitTest]
    public void NextTick_NeverServesStaleBlob()
    {
        var peerId = UUID.NewUUID();
        using var f = new Fixture(memoOn: true, peerId, SerialVariantType.Int);

        f.World.CurrentTick = 1;
        f.SetInt(0, 111);
        f.Node.Network.DirtyMask = 0b1;
        f.Serializer.Begin();
        var t1a = Buffer(); f.Serializer.Export(f.World, f.Peer, t1a, int.MaxValue);
        var t1b = Buffer(); f.Serializer.Export(f.World, f.Peer, t1b, int.MaxValue);
        Assert.Equal(1, f.Serializer.MemoHitsForTests);

        f.World.CurrentTick = 2;
        f.SetInt(0, 222);
        f.Node.Network.DirtyMask = 0b1;
        f.Serializer.Begin();
        var t2a = Buffer(); f.Serializer.Export(f.World, f.Peer, t2a, int.MaxValue);
        var t2b = Buffer(); f.Serializer.Export(f.World, f.Peer, t2b, int.MaxValue);

        // Fresh bytes for the new value, and the tick-2 hit serves the NEW blob.
        Assert.False(t2a.WrittenSpan.SequenceEqual(t1a.WrittenSpan));
        Assert.True(t2a.WrittenSpan.SequenceEqual(t2b.WrittenSpan));
        Assert.Equal(2, f.Serializer.MemoHitsForTests);
    }

    // 7. The equivalence matrix — the strongest single check: a deterministic pseudo-random
    //    script of dirty sets, value changes and acks, driven identically through a memo-on
    //    and a memo-off fixture, byte-comparing EVERY export. Any signature hole that the
    //    single-scenario tests miss has to survive 40 randomized rounds to get through.
    [NebulaUnitTest]
    public void OnOffEquivalence_RandomizedSchedules()
    {
        RunOnOffEquivalence(
            SerialVariantType.Int, SerialVariantType.Float, SerialVariantType.Int, SerialVariantType.Float);
    }

    // 7b. The same matrix at a WIDE presence mask (24 props = 3 mask bytes, two-level on the
    //     wire): proves the memo blob and the compacting backfill agree byte-for-byte when
    //     the mask is being shifted under the body.
    [NebulaUnitTest]
    public void OnOffEquivalence_RandomizedSchedules_TwoLevelMask()
    {
        var types = new SerialVariantType[24];
        for (int i = 0; i < types.Length; i++)
        {
            types[i] = (i % 3 == 1) ? SerialVariantType.Float : SerialVariantType.Int;
        }
        RunOnOffEquivalence(types);
    }

    // 7c. The wide matrix again with every float QUANTIZED (0.01 step): the integer delta
    //     forms, the canonical ring and the dead-band filter are all node-level, so the
    //     memo signature must still describe the bytes completely.
    [NebulaUnitTest]
    public void OnOffEquivalence_RandomizedSchedules_QuantizedMembers()
    {
        var types = new SerialVariantType[24];
        var steps = new float[24];
        for (int i = 0; i < types.Length; i++)
        {
            types[i] = (i % 3 == 1) ? SerialVariantType.Float : SerialVariantType.Int;
            steps[i] = (i % 3 == 1) ? 0.01f : 0f;
        }
        RunOnOffEquivalence(types, steps);
    }

    private static void RunOnOffEquivalence(params SerialVariantType[] types) => RunOnOffEquivalence(types, null);

    private static void RunOnOffEquivalence(SerialVariantType[] types, float[] quantizeSteps)
    {
        var peerId = UUID.NewUUID();
        using var on = new Fixture(memoOn: true, peerId, types, quantizeSteps);
        using var off = new Fixture(memoOn: false, peerId, types, quantizeSteps);

        long allProps = types.Length >= 64 ? -1L : (1L << types.Length) - 1;
        var rng = new Random(1234);   // fixed seed: failures must reproduce
        for (int tick = 1; tick <= 40; tick++)
        {
            // Sparse-ish dirty sets, like real traffic: each prop dirty with ~1/4 chance,
            // never empty.
            long dirty = 0;
            while (dirty == 0)
            {
                for (int prop = 0; prop < types.Length; prop++)
                {
                    if (rng.Next(4) == 0) dirty |= 1L << prop;
                }
                dirty &= allProps;
            }
            bool ack = rng.Next(3) == 0;
            float bump = (float)rng.NextDouble();

            foreach (var f in new[] { on, off })
            {
                f.World.CurrentTick = tick;
                for (int prop = 0; prop < types.Length; prop++)
                {
                    if ((dirty & (1L << prop)) == 0) continue;
                    var cache = f.Node.Network.CachedProperties[prop];
                    if (cache.Type == SerialVariantType.Float) cache.FloatValue += bump;
                    else cache.IntValue += 1;
                    f.Node.Network.CachedProperties[prop] = cache;
                }
                f.Node.Network.DirtyMask = dirty;
                f.Serializer.Begin();
            }

            // The second export of a peer in one tick is NOT a repeat of the first: a
            // settle absolute in the first export clears the peer's lossy bit, so the
            // second one legitimately omits that property. So the memo-served second
            // export is compared against a slow-path second export from identical
            // state, not against the first.
            var bufOnA = Buffer(); on.Serializer.Export(on.World, on.Peer, bufOnA, int.MaxValue);
            var bufOnB = Buffer(); on.Serializer.Export(on.World, on.Peer, bufOnB, int.MaxValue);
            var bufOffA = Buffer(); off.Serializer.Export(off.World, off.Peer, bufOffA, int.MaxValue);
            var bufOffB = Buffer(); off.Serializer.Export(off.World, off.Peer, bufOffB, int.MaxValue);

            Assert.True(bufOnA.WrittenSpan.SequenceEqual(bufOffA.WrittenSpan),
                $"tick {tick}: memo-on first export diverged from memo-off");
            Assert.True(bufOnB.WrittenSpan.SequenceEqual(bufOffB.WrittenSpan),
                $"tick {tick}: memo-served bytes diverged from fresh encode of the same state");

            foreach (var f in new[] { on, off })
            {
                f.Serializer.CommitExport(f.World, f.Peer, tick);
                if (ack) f.Serializer.Acknowledge(f.World, f.Peer, tick);
            }
        }

        Assert.True(on.Serializer.MemoHitsForTests > 10,
            $"expected frequent hits, got {on.Serializer.MemoHitsForTests} - eligibility broke");
    }
}
