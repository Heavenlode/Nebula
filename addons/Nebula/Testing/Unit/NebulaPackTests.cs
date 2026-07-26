using System;
using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Round-trip, ratio and hardening tests for the NebulaPack lockstep byte-delta codec.
///
/// The codec is pure and stateless, so unlike the NetArray sync tests these need no peer state or
/// tick plumbing - they drive the public statics directly. Two properties matter most and are
/// asserted throughout:
///   1. MeasureDelta must return exactly what EncodeDelta writes, because baseline selection picks
///      the cheapest candidate using Measure and then encodes once. A mismatch would silently pick
///      the wrong baseline or overflow the destination.
///   2. DecodeDelta runs on untrusted bytes and must never throw for any input at all.
/// </summary>
[NebulaUnitTest]
public class NebulaPackTests
{
    /// <summary>Encodes, decodes and asserts the result is byte-identical to the input.</summary>
    private static int AssertRoundTrip(byte[] input, byte[] window)
    {
        int measured = NebulaPack.MeasureDelta(input, window);
        Assert.True(measured > 0, "MeasureDelta must return a positive size");

        var encoded = new byte[measured];
        int written = NebulaPack.EncodeDelta(input, window, encoded);
        Assert.Equal(measured, written);

        var decoded = new byte[input.Length];
        int produced = NebulaPack.DecodeDelta(encoded, window, decoded);
        Assert.Equal(input.Length, produced);
        Assert.Equal(input, decoded);

        return measured;
    }

    // ---------------------------------------------------------------- round trips

    [NebulaUnitTest]
    public void RoundTrip_RandomPairs()
    {
        var rng = new Random(20260726);
        for (int trial = 0; trial < 2000; trial++)
        {
            int inLen = rng.Next(0, 200);
            int winLen = rng.Next(0, 200);
            var input = new byte[inLen];
            var window = new byte[winLen];
            rng.NextBytes(input);
            rng.NextBytes(window);

            // Make them share structure some of the time, so both the "mostly matching" and
            // "mostly differing" paths get exercised.
            if (trial % 2 == 0)
            {
                int shared = Math.Min(inLen, winLen);
                for (int i = 0; i < shared; i++)
                {
                    if (rng.Next(4) != 0) input[i] = window[i];
                }
            }

            AssertRoundTrip(input, window);
        }
    }

    [NebulaUnitTest]
    public void RoundTrip_EmptyInput()
    {
        AssertRoundTrip(Array.Empty<byte>(), new byte[] { 1, 2, 3 });
    }

    [NebulaUnitTest]
    public void RoundTrip_EmptyWindow()
    {
        AssertRoundTrip(new byte[] { 1, 2, 3, 4 }, Array.Empty<byte>());
    }

    [NebulaUnitTest]
    public void RoundTrip_BothEmpty()
    {
        AssertRoundTrip(Array.Empty<byte>(), Array.Empty<byte>());
    }

    [NebulaUnitTest]
    public void RoundTrip_SingleByte()
    {
        AssertRoundTrip(new byte[] { 0x42 }, new byte[] { 0x42 });
        AssertRoundTrip(new byte[] { 0x42 }, new byte[] { 0x43 });
        AssertRoundTrip(new byte[] { 0x42 }, Array.Empty<byte>());
    }

    [NebulaUnitTest]
    public void RoundTrip_IdenticalToWindow_CostsAlmostNothing()
    {
        var payload = new byte[69];
        new Random(1).NextBytes(payload);

        int size = AssertRoundTrip(payload, (byte[])payload.Clone());

        // rawLen varint + a single (skip=69, diff=0) run.
        Assert.True(size <= 5, $"identical payload should encode to a few bytes, got {size}");
    }

    [NebulaUnitTest]
    public void RoundTrip_NothingInCommon()
    {
        var input = new byte[100];
        var window = new byte[100];
        for (int i = 0; i < 100; i++) { input[i] = 0xFF; window[i] = 0x00; }

        int size = AssertRoundTrip(input, window);

        // No match anywhere, so the delta cannot beat raw - the caller is expected to notice this
        // via MeasureDelta and send the payload raw instead.
        Assert.True(size > input.Length, "an incompressible delta should measure larger than raw");
    }

    [NebulaUnitTest]
    public void RoundTrip_EndsOnSkipRun()
    {
        // Differs only at the very front, so the encoding ends with a long trailing skip.
        var window = new byte[80];
        new Random(2).NextBytes(window);
        var input = (byte[])window.Clone();
        input[0] ^= 0xFF;

        int size = AssertRoundTrip(input, window);
        Assert.True(size < 12, $"single leading change should stay tiny, got {size}");
    }

    [NebulaUnitTest]
    public void RoundTrip_InputLongerAndShorterThanWindow()
    {
        var window = new byte[50];
        new Random(3).NextBytes(window);

        var longer = new byte[80];
        window.CopyTo(longer, 0);
        for (int i = 50; i < 80; i++) longer[i] = 0xAB;
        AssertRoundTrip(longer, window);

        var shorter = new byte[20];
        Array.Copy(window, shorter, 20);
        AssertRoundTrip(shorter, window);
    }

    [NebulaUnitTest]
    public void RoundTrip_MtuSized()
    {
        var window = new byte[1400];
        new Random(4).NextBytes(window);
        var input = (byte[])window.Clone();
        for (int i = 0; i < 1400; i += 97) input[i] ^= 0x5A;

        AssertRoundTrip(input, window);
    }

    // ---------------------------------------------------------------- ratio

    [NebulaUnitTest]
    public void Ratio_MatchesRealPayloadShape()
    {
        // Mirrors the structure decoded from a real capture: a long constant prefix (framing,
        // masks, flag bytes), then two Vector3s whose low mantissa bytes churn in a 3-vary/1-same
        // pattern, then a constant tail.
        var window = new byte[69];
        new Random(5).NextBytes(window);
        var input = (byte[])window.Clone();
        foreach (int start in new[] { 42, 46, 50, 55, 59, 63 })
        {
            for (int k = 0; k < 3; k++) input[start + k] ^= 0x37;
        }

        int size = AssertRoundTrip(input, window);
        Assert.True(size < 40, $"expected well under half of 69 bytes, got {size}");
    }

    // ---------------------------------------------------------------- encode limits

    [NebulaUnitTest]
    public void Encode_ReturnsMinusOneWhenDestinationTooSmall()
    {
        var input = new byte[64];
        new Random(6).NextBytes(input);
        var window = new byte[64];

        int needed = NebulaPack.MeasureDelta(input, window);
        Assert.True(needed > 1);

        Assert.Equal(-1, NebulaPack.EncodeDelta(input, window, new byte[needed - 1]));
        Assert.Equal(-1, NebulaPack.EncodeDelta(input, window, Array.Empty<byte>()));
        Assert.Equal(needed, NebulaPack.EncodeDelta(input, window, new byte[needed]));
    }

    // ---------------------------------------------------------------- hardening

    [NebulaUnitTest]
    public void Decode_RejectsEmptyAndTruncatedBodies()
    {
        var window = new byte[40];
        var input = new byte[40];
        new Random(7).NextBytes(input);

        var encoded = new byte[NebulaPack.MeasureDelta(input, window)];
        NebulaPack.EncodeDelta(input, window, encoded);

        Assert.Equal(-1, NebulaPack.DecodeDelta(Array.Empty<byte>(), window, new byte[64]));

        for (int cut = 1; cut < encoded.Length; cut++)
        {
            var truncated = encoded.AsSpan(0, cut).ToArray();
            Assert.Equal(-1, NebulaPack.DecodeDelta(truncated, window, new byte[64]));
        }
    }

    [NebulaUnitTest]
    public void Decode_RejectsRawLenLargerThanOutput()
    {
        var window = new byte[100];
        var input = new byte[100];
        new Random(8).NextBytes(input);

        var encoded = new byte[NebulaPack.MeasureDelta(input, window)];
        NebulaPack.EncodeDelta(input, window, encoded);

        Assert.Equal(-1, NebulaPack.DecodeDelta(encoded, window, new byte[99]));
        Assert.Equal(100, NebulaPack.DecodeDelta(encoded, window, new byte[100]));
    }

    [NebulaUnitTest]
    public void Decode_RejectsSkipRunLongerThanWindow()
    {
        // rawLen = 10, then a single (skip = 10, diff = 0) run, but the window holds only 4 bytes.
        var body = new byte[] { 10, 10, 0 };
        Assert.Equal(-1, NebulaPack.DecodeDelta(body, new byte[4], new byte[10]));
        // Same body against a large enough window is fine.
        Assert.Equal(10, NebulaPack.DecodeDelta(body, new byte[10], new byte[10]));
    }

    [NebulaUnitTest]
    public void Decode_RejectsNonProgressingRun()
    {
        // rawLen = 5 then (skip = 0, diff = 0) would spin forever if it were not rejected.
        var body = new byte[] { 5, 0, 0 };
        Assert.Equal(-1, NebulaPack.DecodeDelta(body, new byte[16], new byte[16]));
    }

    [NebulaUnitTest]
    public void Decode_RejectsOverlongVarint()
    {
        // Six continuation bytes - a uint32 varint is never more than five.
        var body = new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 };
        Assert.Equal(-1, NebulaPack.DecodeDelta(body, new byte[16], new byte[16]));
    }

    [NebulaUnitTest]
    public void Decode_NeverThrowsOnArbitraryGarbage()
    {
        var rng = new Random(9);
        var window = new byte[64];
        rng.NextBytes(window);

        for (int trial = 0; trial < 5000; trial++)
        {
            var garbage = new byte[rng.Next(0, 40)];
            rng.NextBytes(garbage);
            var output = new byte[rng.Next(0, 128)];

            int result = NebulaPack.DecodeDelta(garbage, window, output);

            // Either a clean rejection, or a self-consistent decode of a byte string that happened
            // to be well-formed. Never an exception, never a length outside the destination.
            Assert.True(result == -1 || (result >= 0 && result <= output.Length));
        }
    }

    // ---------------------------------------------------------------- window ring

    [NebulaUnitTest]
    public void Window_RecordsAndRetrievesByTick()
    {
        var win = new NebulaPackWindow();
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        win.Record(5, payload);

        Assert.True(win.TryGet(5, out var got));
        Assert.Equal(payload, got.ToArray());
        Assert.False(win.TryGet(6, out _));
    }

    [NebulaUnitTest]
    public void Window_CopiesRatherThanAliasing()
    {
        var win = new NebulaPackWindow();
        var payload = new byte[] { 1, 2, 3 };
        win.Record(1, payload);

        // The caller's buffer is pooled and reused next tick; the ring must not observe that.
        payload[0] = 0xFF;

        Assert.True(win.TryGet(1, out var got));
        Assert.Equal(new byte[] { 1, 2, 3 }, got.ToArray());
    }

    [NebulaUnitTest]
    public void Window_StaleSlotIsNotMistakenForLive()
    {
        var win = new NebulaPackWindow();
        win.Record(3, new byte[] { 9, 9 });

        // Wraps onto the same slot, which must invalidate the old tick rather than return its bytes.
        win.Record(3 + NebulaPackWindow.RingSize, new byte[] { 7 });

        Assert.False(win.TryGet(3, out _));
        Assert.True(win.TryGet(3 + NebulaPackWindow.RingSize, out var got));
        Assert.Equal(new byte[] { 7 }, got.ToArray());
    }

    [NebulaUnitTest]
    public void Window_IgnoresNegativeTicks()
    {
        // The client sets CurrentTick to -1 on world reset; tick % RingSize would index negatively.
        var win = new NebulaPackWindow();
        win.Record(-1, new byte[] { 1, 2 });
        Assert.False(win.TryGet(-1, out _));
    }

    [NebulaUnitTest]
    public void Window_ResetClearsEverything()
    {
        var win = new NebulaPackWindow();
        for (int t = 0; t < NebulaPackWindow.RingSize; t++) win.Record(t, new byte[] { (byte)t });

        win.Reset();

        for (int t = 0; t < NebulaPackWindow.RingSize; t++) Assert.False(win.TryGet(t, out _));
    }

    [NebulaUnitTest]
    public void Window_MaxPackAgeFitsInRing()
    {
        // The invariant the whole scheme rests on: any baseline the sender may name must still be
        // resolvable, i.e. not yet overwritten.
        Assert.True(NebulaPackWindow.MaxPackAge < NebulaPackWindow.RingSize);
    }

    // ---------------------------------------------------------------- protocol under loss

    /// <summary>
    /// Simulates the real server/client loop - ack-gated baseline selection, best-of-N, a lossy
    /// tick channel and a lossy ack channel - and asserts the client always reconstructs exactly
    /// what the server intended, or refuses the packet outright. Never silent corruption.
    ///
    /// This is the claim the whole design rests on: because the server only ever names a baseline
    /// the peer has acknowledged, dropped and delayed packets can shrink the compression ratio but
    /// can never desync the window.
    /// </summary>
    [NebulaUnitTest]
    public void Protocol_SurvivesPacketAndAckLoss()
    {
        var rng = new Random(11);
        var serverWindow = new NebulaPackWindow();
        var clientWindow = new NebulaPackWindow();

        var inFlightAcks = new System.Collections.Generic.List<(Tick tick, int deliverAt)>();

        var payload = new byte[91];
        rng.NextBytes(payload);

        int applied = 0, rawSent = 0, deltaSent = 0;

        for (Tick tick = 0; tick < 400; tick++)
        {
            // The world evolves a little each tick, like a moving entity.
            for (int k = 0; k < 6; k++) payload[40 + k] = (byte)rng.Next(256);

            // ---- server: pick the cheapest acked baseline in range, exactly as WorldRunner does
            int bestAge = -1, bestSize = -1;
            for (int age = 1; age <= NebulaPackWindow.MaxPackAge; age++)
            {
                Tick candidate = tick - age;
                if (candidate < 0) break;
                // Per-tick, not "<= newest ack": acks are lossy, so a newer ack proves nothing
                // about an older tick. Getting this wrong means deltaing against bytes the client
                // never received, which is precisely what this test exists to catch.
                if (!serverWindow.TryGetAcked(candidate, out var baseline)) continue;

                int size = NebulaPack.MeasureDelta(payload, baseline);
                if (size < payload.Length && (bestSize < 0 || size < bestSize))
                {
                    bestSize = size;
                    bestAge = age;
                }
            }

            byte[] body;
            if (bestAge > 0)
            {
                serverWindow.TryGetAcked(tick - bestAge, out var baseline);
                body = new byte[bestSize];
                Assert.Equal(bestSize, NebulaPack.EncodeDelta(payload, baseline, body));
                deltaSent++;
            }
            else
            {
                body = (byte[])payload.Clone();
                rawSent++;
            }

            var sentPayload = (byte[])payload.Clone();
            serverWindow.Record(tick, sentPayload);

            // ---- lossy tick channel
            bool tickDelivered = rng.Next(100) >= 15;

            if (tickDelivered)
            {
                byte[] reconstructed;
                if (bestAge > 0)
                {
                    // The server named an acked baseline, so the client must be able to resolve it.
                    Assert.True(clientWindow.TryGet(tick - bestAge, out var baseline),
                        $"tick {tick}: client is missing baseline {tick - bestAge} the server named as acked");

                    reconstructed = new byte[sentPayload.Length];
                    int produced = NebulaPack.DecodeDelta(body, baseline, reconstructed);
                    Assert.Equal(sentPayload.Length, produced);
                }
                else
                {
                    reconstructed = body;
                }

                Assert.Equal(sentPayload, reconstructed);
                Assert.Equal(NebulaPack.Checksum(sentPayload), NebulaPack.Checksum(reconstructed));

                clientWindow.Record(tick, reconstructed);
                applied++;

                // Ack travels back over its own lossy path with variable delay.
                if (rng.Next(100) >= 10)
                {
                    inFlightAcks.Add((tick, (int)tick + rng.Next(1, 3)));
                }
            }

            // ---- ack delivery: marks that ONE tick, exactly as PeerAcknowledge does
            for (int i = inFlightAcks.Count - 1; i >= 0; i--)
            {
                if (inFlightAcks[i].deliverAt <= tick)
                {
                    serverWindow.MarkAcked(inFlightAcks[i].tick);
                    inFlightAcks.RemoveAt(i);
                }
            }
        }

        Assert.True(applied > 250, $"expected most ticks to be applied, got {applied}");
        Assert.True(deltaSent > 200, $"expected compression to engage on most ticks, got {deltaSent} (raw {rawSent})");
    }

    /// <summary>
    /// When the client stops acking, every candidate baseline ages out of range and the server has
    /// no choice but raw. This is the self-healing path that recovers from a decode failure without
    /// any explicit renegotiation.
    /// </summary>
    [NebulaUnitTest]
    public void Protocol_BaselineAgesOutWhenAcksStop()
    {
        var window = new NebulaPackWindow();
        var payload = new byte[64];
        new Random(12).NextBytes(payload);

        const Tick lastAcked = 10;
        for (Tick t = 0; t <= lastAcked; t++) window.Record(t, payload);

        // One tick past the last ack a baseline is still available...
        Assert.True(HasCandidate(window, lastAcked + 1, lastAcked));
        // ...but once the gap exceeds MaxPackAge nothing is nameable any more.
        Assert.False(HasCandidate(window, lastAcked + NebulaPackWindow.MaxPackAge + 1, lastAcked));

        static bool HasCandidate(NebulaPackWindow window, Tick now, Tick acked)
        {
            for (int age = 1; age <= NebulaPackWindow.MaxPackAge; age++)
            {
                Tick candidate = now - age;
                if (candidate < 0) break;
                if (candidate > acked) continue;
                if (window.TryGet(candidate, out _)) return true;
            }
            return false;
        }
    }

    [NebulaUnitTest]
    public void Window_RoundTripsThroughCodecAtMaxAge()
    {
        var win = new NebulaPackWindow();
        var rng = new Random(10);

        var baseline = new byte[69];
        rng.NextBytes(baseline);
        const Tick baseTick = 100;
        win.Record(baseTick, baseline);

        var current = (byte[])baseline.Clone();
        current[10] ^= 0xFF;

        Tick nowTick = baseTick + NebulaPackWindow.MaxPackAge;
        Assert.True(win.TryGet(nowTick - NebulaPackWindow.MaxPackAge, out var resolved));

        var encoded = new byte[NebulaPack.MeasureDelta(current, resolved)];
        Assert.Equal(encoded.Length, NebulaPack.EncodeDelta(current, resolved, encoded));

        var decoded = new byte[current.Length];
        Assert.Equal(current.Length, NebulaPack.DecodeDelta(encoded, resolved, decoded));
        Assert.Equal(current, decoded);
    }
}
