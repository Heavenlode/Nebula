using System;
using System.Collections.Generic;
using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Differential tests for the word-at-a-time scan inside NebulaPack's encoder.
///
/// The encoder's inner loop was rewritten to compare eight bytes at a time -- XOR plus a
/// trailing-zero count to locate the first differing byte, and a has-zero-byte test to step over
/// runs that differ throughout. That is a hand-vectorization of wire-format code, where a mistake
/// does not throw: it produces a subtly different delta that still decodes, and desynchronizes a
/// client somewhere downstream.
///
/// So passing the existing round-trip tests is not sufficient evidence. These compare the encoder
/// against a straightforward scalar model of the SAME format, byte for byte, over the shapes most
/// likely to break a word-based scan: runs that straddle the eight-byte stride, windows shorter and
/// longer than the input, lengths either side of the stride, and the has-zero-byte helper's known
/// false positives (borrow propagation can flag a byte the borrow manufactured, which must only
/// cost a slower path and never end a run early).
/// </summary>
[NebulaUnitTest]
public class NebulaPackWordScanTests
{
    /// <summary>
    /// The format, written the obvious way: alternate a run of bytes matching the window with a run
    /// that differs, and price each as varint(skip) + varint(literals) + literals. This mirrors the
    /// encoder's contract without sharing any of its scanning logic, which is the point.
    /// </summary>
    private static int ReferenceMeasure(ReadOnlySpan<byte> input, ReadOnlySpan<byte> window)
    {
        static int VarintSize(uint v)
            => v < 0x80u ? 1 : v < 0x4000u ? 2 : v < 0x200000u ? 3 : v < 0x10000000u ? 4 : 5;

        int size = VarintSize((uint)input.Length);
        int i = 0;
        while (i < input.Length)
        {
            int skipStart = i;
            while (i < input.Length && i < window.Length && input[i] == window[i]) i++;
            int skip = i - skipStart;

            int literalStart = i;
            while (i < input.Length && (i >= window.Length || input[i] != window[i])) i++;
            int literals = i - literalStart;

            size += VarintSize((uint)skip) + VarintSize((uint)literals) + literals;
        }
        return size;
    }

    /// <summary>Asserts the encoder agrees with the model, and that the bytes still round-trip.</summary>
    private static void AssertMatchesReference(byte[] input, byte[] window)
    {
        int reference = ReferenceMeasure(input, window);
        int measured = NebulaPack.MeasureDelta(input, window);
        Assert.True(reference == measured,
            $"MeasureDelta disagreed with the reference model: {measured} vs {reference} "
            + $"(input {input.Length} B, window {window.Length} B)");

        var encoded = new byte[Math.Max(measured, 1)];
        int written = NebulaPack.EncodeDelta(input, window, encoded);
        Assert.True(written == measured,
            $"EncodeDelta wrote {written} bytes where MeasureDelta promised {measured}");

        var decoded = new byte[input.Length];
        int produced = NebulaPack.DecodeDelta(encoded.AsSpan(0, written), window, decoded);
        Assert.Equal(input.Length, produced);
        Assert.Equal(input, decoded);
    }

    [NebulaUnitTest]
    public void TestRunsStraddlingTheEightByteStride()
    {
        // A matching run ending at every offset within the stride, so the trailing-zero path is
        // exercised at each byte position rather than only where a run happens to align.
        for (int matchLength = 0; matchLength <= 24; matchLength++)
        {
            var input = new byte[48];
            var window = new byte[48];
            for (int i = 0; i < input.Length; i++)
            {
                input[i] = (byte)(i + 1);
                window[i] = i < matchLength ? (byte)(i + 1) : (byte)0xFF;
            }
            AssertMatchesReference(input, window);
        }
    }

    [NebulaUnitTest]
    public void TestDifferingRunsStraddlingTheStride()
    {
        // The inverse: a differing run of every length, which drives the has-zero-byte stepping.
        for (int diffLength = 0; diffLength <= 24; diffLength++)
        {
            var input = new byte[48];
            var window = new byte[48];
            for (int i = 0; i < input.Length; i++)
            {
                input[i] = 0x11;
                window[i] = i < diffLength ? (byte)0x22 : (byte)0x11;
            }
            AssertMatchesReference(input, window);
        }
    }

    [NebulaUnitTest]
    public void TestLengthsAroundTheStride()
    {
        // Payloads shorter than one word, exactly one word, and one either side -- the boundaries
        // where an off-by-one in the `i + sizeof(ulong) <= limit` guard would show up.
        foreach (int length in new[] { 0, 1, 2, 7, 8, 9, 15, 16, 17, 31, 32, 33 })
        {
            var input = new byte[length];
            var window = new byte[length];
            for (int i = 0; i < length; i++)
            {
                input[i] = (byte)i;
                window[i] = (byte)(i % 3 == 0 ? i : i ^ 0x5A);
            }
            AssertMatchesReference(input, window);
        }
    }

    [NebulaUnitTest]
    public void TestMismatchedWindowLengths()
    {
        // Past the end of the window everything is literal by definition, and the word loop must
        // stop at that boundary rather than reading beyond it.
        foreach (int windowLength in new[] { 0, 1, 7, 8, 9, 40, 63, 64, 65 })
        {
            var input = new byte[64];
            var window = new byte[windowLength];
            for (int i = 0; i < input.Length; i++) input[i] = (byte)(i * 7);
            for (int i = 0; i < window.Length; i++) window[i] = (byte)(i * 7);   // identical prefix
            AssertMatchesReference(input, window);
        }
    }

    [NebulaUnitTest]
    public void TestBytesThatProvokeTheHasZeroByteFalsePositive()
    {
        // The has-zero-byte test borrows across byte boundaries, so words containing 0x01 and 0x00
        // are where a manufactured zero can appear. A false positive must only cost a slower path;
        // if it ever ended a run early the reference comparison below would diverge.
        var patterns = new byte[][]
        {
            new byte[] { 0x01, 0x00, 0x01, 0x00, 0x01, 0x00, 0x01, 0x00 },
            new byte[] { 0x00, 0x01, 0x80, 0x7F, 0xFF, 0x01, 0x00, 0x80 },
            new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80 },
            new byte[] { 0xFF, 0x01, 0xFF, 0x01, 0xFF, 0x01, 0xFF, 0x01 },
        };
        foreach (var pattern in patterns)
        {
            foreach (var other in patterns)
            {
                var input = new byte[64];
                var window = new byte[64];
                for (int i = 0; i < 64; i++)
                {
                    input[i] = pattern[i % pattern.Length];
                    window[i] = other[i % other.Length];
                }
                AssertMatchesReference(input, window);
            }
        }
    }

    [NebulaUnitTest]
    public void TestRandomPayloadsAgainstTheReference()
    {
        // Seeded so a failure is reproducible. Densities span "almost everything matches" to
        // "almost nothing does" -- real tick payloads sit near the sparse end, but the encoder has
        // to be right across the range.
        var random = new Random(20260807);
        for (int trial = 0; trial < 400; trial++)
        {
            int length = random.Next(0, 300);
            int windowLength = random.Next(0, 300);
            int matchPercent = random.Next(0, 101);

            var window = new byte[windowLength];
            random.NextBytes(window);

            var input = new byte[length];
            random.NextBytes(input);
            for (int i = 0; i < length && i < windowLength; i++)
            {
                if (random.Next(100) < matchPercent) input[i] = window[i];
            }

            AssertMatchesReference(input, window);
        }
    }

    // ---- the bounded measure -------------------------------------------------------------
    //
    // Baseline selection stops measuring a candidate once its running size reaches the best size
    // found so far. The claim is that this is EXACT -- it finds the same global minimum an
    // unbounded search would -- because the size only ever grows. That claim is what these pin;
    // if it were wrong the server would quietly ship larger deltas than it reports.

    [NebulaUnitTest]
    public void TestBoundedMeasureAgreesWhenTheBoundDoesNotBite()
    {
        var random = new Random(1234);
        for (int trial = 0; trial < 200; trial++)
        {
            var window = new byte[random.Next(1, 400)];
            random.NextBytes(window);
            var input = (byte[])window.Clone();
            for (int i = 0; i < input.Length; i++)
                if (random.Next(100) < 40) input[i] ^= 0x3C;

            int exact = NebulaPack.MeasureDelta(input, window);
            // A bound above the true size must not change the answer.
            Assert.Equal(exact, NebulaPack.MeasureDeltaBounded(input, window, exact + 1));
            Assert.Equal(exact, NebulaPack.MeasureDeltaBounded(input, window, int.MaxValue));
        }
    }

    [NebulaUnitTest]
    public void TestBoundedMeasureNeverUnderreports()
    {
        var random = new Random(5678);
        for (int trial = 0; trial < 200; trial++)
        {
            var window = new byte[random.Next(1, 400)];
            random.NextBytes(window);
            var input = (byte[])window.Clone();
            for (int i = 0; i < input.Length; i++)
                if (random.Next(100) < 60) input[i] ^= 0x77;

            int exact = NebulaPack.MeasureDelta(input, window);
            // Below the true size the call may stop early, but whatever it returns must still be
            // at or above the bound -- never a number that would make a losing candidate look like
            // a winner.
            for (int bound = 1; bound <= exact; bound += Math.Max(1, exact / 8))
            {
                int bounded = NebulaPack.MeasureDeltaBounded(input, window, bound);
                Assert.True(bounded >= bound,
                    $"bounded measure returned {bounded} under its own bound {bound} (exact {exact})");
            }
        }
    }

    [NebulaUnitTest]
    public void TestBoundedSearchFindsTheSameWinnerAsAnUnboundedOne()
    {
        // The property that actually matters: running a field of candidates with a tightening
        // bound must select the same baseline, and report the same size, as measuring them all
        // exactly. This is the whole justification for the early abort.
        var random = new Random(99);
        for (int trial = 0; trial < 120; trial++)
        {
            int length = random.Next(50, 500);
            var input = new byte[length];
            random.NextBytes(input);

            var candidates = new List<byte[]>();
            for (int c = 0; c < 12; c++)
            {
                var candidate = (byte[])input.Clone();
                int churn = random.Next(0, 100);
                for (int i = 0; i < length; i++)
                    if (random.Next(100) < churn) candidate[i] ^= (byte)random.Next(1, 256);
                candidates.Add(candidate);
            }

            int exhaustiveBest = int.MaxValue, exhaustivePick = -1;
            for (int c = 0; c < candidates.Count; c++)
            {
                int size = NebulaPack.MeasureDelta(input, candidates[c]);
                if (size < exhaustiveBest) { exhaustiveBest = size; exhaustivePick = c; }
            }

            int bound = length, boundedBest = int.MaxValue, boundedPick = -1;
            for (int c = 0; c < candidates.Count; c++)
            {
                int size = NebulaPack.MeasureDeltaBounded(input, candidates[c], bound);
                if (size < bound) { bound = size; boundedBest = size; boundedPick = c; }
            }

            // Only compare when a delta was actually usable; otherwise both correctly decline.
            if (exhaustiveBest < length)
            {
                Assert.Equal(exhaustiveBest, boundedBest);
                Assert.Equal(exhaustivePick, boundedPick);
            }
            else
            {
                Assert.True(boundedPick < 0 || boundedBest >= length);
            }
        }
    }

    // ---- block digests -------------------------------------------------------------------

    [NebulaUnitTest]
    public void TestDigestsAreDeterministicAndBlockLocal()
    {
        var random = new Random(31337);
        var payload = new byte[300];
        random.NextBytes(payload);

        var first = new ulong[NebulaPack.BlockCount(payload.Length)];
        var second = new ulong[first.Length];
        NebulaPack.ComputeDigests(payload, first);
        NebulaPack.ComputeDigests(payload, second);
        Assert.Equal(first, second);

        // Changing one byte must move its own block's digest and leave the others alone -- that
        // locality is exactly what makes a differing-block count mean anything.
        var mutated = (byte[])payload.Clone();
        int touched = 100;
        mutated[touched] ^= 0xFF;
        var after = new ulong[first.Length];
        NebulaPack.ComputeDigests(mutated, after);

        int touchedBlock = touched / NebulaPack.DigestBlockBytes;
        for (int b = 0; b < first.Length; b++)
        {
            if (b == touchedBlock) Assert.NotEqual(first[b], after[b]);
            else Assert.Equal(first[b], after[b]);
        }
    }

    [NebulaUnitTest]
    public void TestDigestsSeparateReorderedBytes()
    {
        // A plain XOR fold would collide here: same bytes, different order. Ranking would then
        // treat unrelated baselines as identical and pick badly.
        var a = new byte[NebulaPack.DigestBlockBytes];
        var b = new byte[NebulaPack.DigestBlockBytes];
        for (int i = 0; i < a.Length; i++) { a[i] = (byte)i; b[i] = (byte)(a.Length - 1 - i); }

        var da = new ulong[1];
        var db = new ulong[1];
        NebulaPack.ComputeDigests(a, da);
        NebulaPack.ComputeDigests(b, db);
        Assert.NotEqual(da[0], db[0]);
    }

    [NebulaUnitTest]
    public void TestPayloadsShapedLikeRealTickTraffic()
    {
        // The shape measured off a live 40-peer server: a mostly-matching first tenth, then long
        // differing runs broken by very short matches (~2 bytes). This is the case the rewrite was
        // built for, so it should be the case most obviously correct.
        var random = new Random(4242);
        for (int trial = 0; trial < 200; trial++)
        {
            int length = random.Next(600, 1200);
            var window = new byte[length];
            random.NextBytes(window);
            var input = (byte[])window.Clone();

            int i = length / 10;             // leave the first tenth matching
            while (i < length)
            {
                int differing = random.Next(8, 24);
                for (int d = 0; d < differing && i < length; d++, i++) input[i] = (byte)(window[i] ^ 0xA5);
                i += random.Next(1, 4);      // a short matching run
            }
            AssertMatchesReference(input, window);
        }
    }
}
