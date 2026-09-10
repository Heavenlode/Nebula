using System;
using Godot;
using Nebula;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// The quantized wire codec as pure value logic. The invariant deltas rest on is NOT
/// "re-encoding a decoded value gives the same code" (false on the octahedral seams) but
/// "decoding is a deterministic function of the codes, and canonicalising is idempotent" -
/// so the seam tests assert bit-equality of decoded floats, never code equality.
/// </summary>
[NebulaUnitTest]
public class QuantizedCodecTests
{
    private static NetBuffer Buffer() => new(64, usePool: false);

    // ───────────── grid ─────────────

    [NebulaUnitTest]
    public void Grid_CodeRoundTrip_IsExact_AcrossStepsAndMagnitudes()
    {
        float[] steps = { 0.01f, 0.001f, 0.002f, 0.0005f, 0.00002f, 1f };
        float[] magnitudes = { 0f, 0.37f, 1f, 12.5f, 601f, 6000f, 25000f };
        foreach (var step in steps)
        {
            foreach (var m in magnitudes)
            {
                // The declared resolution only holds inside the float32 round-robust range.
                if (m >= 0.5f * step * (1 << 24)) continue;
                foreach (var v in new[] { m, -m, m + step * 0.49f, m - step * 0.49f })
                {
                    int q = QuantizedCodec.Quantize(v, step);
                    float d = QuantizedCodec.Dequantize(q, step);
                    Assert.Equal(q, QuantizedCodec.Quantize(d, step));
                    // The input itself is a float32: near 25,000 its ulp (~0.002) is a
                    // real part of the distance to the grid point.
                    float ulp = MathF.BitIncrement(MathF.Abs(v)) - MathF.Abs(v);
                    Assert.True(MathF.Abs(d - v) <= step * 0.5f + ulp + step * 1e-3f, $"v={v} step={step} d={d}");
                }
            }
        }
    }

    [NebulaUnitTest]
    public void Grid_OutOfRange_ClampsInsteadOfPlatformCast()
    {
        Assert.Equal(int.MaxValue, QuantizedCodec.Quantize(1e30f, 0.01f));
        Assert.Equal(int.MinValue, QuantizedCodec.Quantize(-1e30f, 0.01f));
        Assert.Equal(0, QuantizedCodec.Quantize(float.NaN, 0.01f));
    }

    // ───────────── octahedral ─────────────

    private static Vector3[] SphereSample()
    {
        // Dense deterministic sample plus every axis, face edge midpoint and corner: the
        // seams and the eight octant corners (where all three components are equal) are
        // where a fold can round two ways.
        var rng = new Random(77);
        var list = new System.Collections.Generic.List<Vector3>();
        for (int i = 0; i < 4000; i++)
        {
            var v = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1);
            if (v.LengthSquared() < 1e-3f) continue;
            list.Add(v.Normalized());
        }
        float s = 1f / MathF.Sqrt(3f);
        float h = 1f / MathF.Sqrt(2f);
        foreach (var sx in new[] { -1f, 1f })
        foreach (var sy in new[] { -1f, 1f })
        foreach (var sz in new[] { -1f, 1f })
        {
            list.Add(new Vector3(sx * s, sy * s, sz * s));
            list.Add(new Vector3(sx * h, sy * h, 0));
            list.Add(new Vector3(sx * h, 0, sz * h));
            list.Add(new Vector3(0, sy * h, sz * h));
        }
        list.Add(Vector3.Up); list.Add(Vector3.Down); list.Add(Vector3.Left);
        list.Add(Vector3.Right); list.Add(Vector3.Forward); list.Add(Vector3.Back);
        // Points just off the octant centres, where the map stretches most: the worst
        // rounding sits half a step along the (u, w) diagonal from a grid point.
        for (int i = 0; i < 64; i++)
        {
            float du = 0.00002f * (i / 8 - 4) / 4f;
            float dw = 0.00002f * (i % 8 - 4) / 4f;
            list.Add(QuantizedCodec.OctDecode(1f / 3f + du, 1f / 3f + dw));
            list.Add(QuantizedCodec.OctDecode(-1f / 3f + du, 1f / 3f + dw));
            list.Add(QuantizedCodec.OctDecode(2f / 3f + du, 2f / 3f + dw)); // lower hemisphere
        }
        return list.ToArray();
    }

    [NebulaUnitTest]
    public void Octahedral_DecodeEncode_WithinMaxError_AndCanonicalIsIdempotent()
    {
        const float step = 0.00002f;
        float maxError = QuantizedCodec.MaxError(SerialVariantType.Vector3, unitVector: true, step);
        float worst = 0f;
        Span<int> codes = stackalloc int[2];
        foreach (var v in SphereSample())
        {
            var cache = new PropertyCache { Type = SerialVariantType.Vector3, Vec3Value = v };
            QuantizedCodec.Encode(in cache, SerialVariantType.Vector3, true, step, codes);
            var once = cache;
            QuantizedCodec.Decode(codes, SerialVariantType.Vector3, true, step, ref once);

            float err = (once.Vec3Value - v).Length();
            worst = MathF.Max(worst, err);
            Assert.True(err <= maxError, $"{v}: error {err} > MaxError {maxError}");
            Assert.True(MathF.Abs(once.Vec3Value.Length() - 1f) < 1e-5f, "decode must renormalise");

            // The contract: canonicalising an already-canonical value is a bit-identical no-op,
            // even where the re-encoded CODE differs (seams, corners).
            var twice = once;
            QuantizedCodec.Canonicalize(ref twice, SerialVariantType.Vector3, true, step);
            Assert.True(BitEqual(twice.Vec3Value, once.Vec3Value), $"{v}: canonical not idempotent");
        }
        Assert.True(worst > maxError * 0.2f, $"MaxError {maxError} is absurdly loose against observed {worst}");
    }

    [NebulaUnitTest]
    public void Octahedral_SeamCrossing_DeltaChainIsExact()
    {
        // A direction sweeping through the -Y hemisphere across both fold seams (x=0 and
        // z=0 with y<0), replicated as a chain of integer deltas the way the serializer does
        // it: server canonical ring on one side, client applied value on the other. The
        // client must land on the server's canonical value bit for bit every tick, whether
        // or not the seam made the re-encoded baseline code differ from the one sent.
        const float step = 0.00002f;
        Span<int> serverCodes = stackalloc int[2];
        Span<int> baseCodes = stackalloc int[2];
        Span<int> clientCodes = stackalloc int[2];

        PropertyCache serverRing = default, clientApplied = default;
        bool haveBaseline = false;
        int fullDeltas = 0;
        for (int i = 0; i <= 400; i++)
        {
            float t = (i / 400f) * MathF.PI * 2f;
            // Circle in the plane tilted so it dips below y=0 and crosses x=0 and z=0 there.
            var v = new Vector3(MathF.Cos(t), -0.6f + 0.3f * MathF.Sin(3 * t), MathF.Sin(t)).Normalized();
            var current = new PropertyCache { Type = SerialVariantType.Vector3, Vec3Value = v };
            QuantizedCodec.Encode(in current, SerialVariantType.Vector3, true, step, serverCodes);

            if (!haveBaseline)
            {
                // Absolute: client stores Dequantize(codes).
                clientApplied = current;
                QuantizedCodec.Decode(serverCodes, SerialVariantType.Vector3, true, step, ref clientApplied);
                haveBaseline = true;
            }
            else
            {
                // Server: delta = Q(current) - Q(ring).
                QuantizedCodec.Encode(in serverRing, SerialVariantType.Vector3, true, step, baseCodes);
                int d0 = serverCodes[0] - baseCodes[0];
                int d1 = serverCodes[1] - baseCodes[1];
                var buf = Buffer();
                Span<int> wd = stackalloc int[] { d0, d1 };
                bool small = QuantizedCodec.TryWriteSmallDelta(buf, wd, 2);
                if (!small)
                {
                    fullDeltas++;
                    QuantizedCodec.WriteCodes(buf, wd, 2);
                }
                buf.ResetRead();
                Span<int> rd = stackalloc int[2];
                if (small) QuantizedCodec.ReadSmallDelta(buf, rd, 2);
                else QuantizedCodec.ReadCodes(buf, rd, 2);
                Assert.Equal(d0, rd[0]); Assert.Equal(d1, rd[1]);

                // Client: Q(applied) + delta, store Dequantize.
                QuantizedCodec.Encode(in clientApplied, SerialVariantType.Vector3, true, step, clientCodes);
                clientCodes[0] += rd[0];
                clientCodes[1] += rd[1];
                QuantizedCodec.Decode(clientCodes, SerialVariantType.Vector3, true, step, ref clientApplied);
            }

            // Server ring for next tick: canonical value.
            serverRing = current;
            QuantizedCodec.Canonicalize(ref serverRing, SerialVariantType.Vector3, true, step);
            Assert.True(BitEqual(clientApplied.Vec3Value, serverRing.Vec3Value), $"tick {i}: client {clientApplied.Vec3Value} != server canonical {serverRing.Vec3Value}");
        }
        Assert.True(fullDeltas > 0, "sweep never crossed a seam - the test is not exercising the fold");
    }

    [NebulaUnitTest]
    public void Octahedral_Sentinel_EncodesAsUp()
    {
        QuantizedCodec.OctEncode(Vector3.Zero, out float u, out float w);
        Assert.Equal(0f, u); Assert.Equal(0f, w);
        Assert.Equal(Vector3.Up, QuantizedCodec.OctDecode(0f, 0f));
    }

    // ───────────── small-delta forms ─────────────

    // [class:2][N x width]: the smallest class every component fits is chosen; a component
    // beyond the widest class declines (nothing written) and the caller takes the full form.
    [NebulaUnitTest]
    public void SmallDelta_PicksSmallestClass_DeclinesBeyondWidest()
    {
        (int count, int[] widths)[] shapes = { (1, new[] { 4, 6, 9, 16 }), (2, new[] { 5, 7, 9, 12 }), (3, new[] { 4, 6, 8, 10 }) };
        foreach (var (count, widths) in shapes)
        {
            Span<int> d = stackalloc int[3];
            Span<int> rd = stackalloc int[3];
            for (int cls = 0; cls < widths.Length; cls++)
            {
                int max = (1 << (widths[cls] - 1)) - 1;
                foreach (var edge in new[] { max, -max - 1, cls == 0 ? 0 : (1 << (widths[cls - 1] - 1)) })
                {
                    for (int i = 0; i < count; i++) d[i] = i == 0 ? edge : -edge / 3;
                    var buf = Buffer();
                    Assert.True(QuantizedCodec.TryWriteSmallDelta(buf, d, count), $"N={count} class {cls} edge {edge} should fit");
                    Assert.Equal(QuantizedCodec.SmallDeltaBits(count, cls), buf.WrittenBits);
                    buf.ResetRead();
                    QuantizedCodec.ReadSmallDelta(buf, rd, count);
                    for (int i = 0; i < count; i++) Assert.Equal(d[i], rd[i]);
                }
            }
            int widest = QuantizedCodec.SmallDeltaMaxMagnitude(count);
            foreach (var over in new[] { widest + 1, -widest - 2 })
            {
                for (int i = 0; i < count; i++) d[i] = 0;
                d[count - 1] = over;
                var buf = Buffer();
                Assert.False(QuantizedCodec.TryWriteSmallDelta(buf, d, count), $"N={count} {over} must not fit");
                Assert.Equal(0, buf.WrittenBits);
                QuantizedCodec.WriteCodes(buf, d, count);
                buf.ResetRead();
                QuantizedCodec.ReadCodes(buf, rd, count);
                for (int i = 0; i < count; i++) Assert.Equal(d[i], rd[i]);
            }
        }
        // The walking case from the plan: two ~85-step components take the 9-bit class.
        var walk = Buffer();
        Assert.True(QuantizedCodec.TryWriteSmallDelta(walk, stackalloc[] { 85, -60 }, 2));
        Assert.Equal(2 + 2 * 9, walk.WrittenBits);
    }

    // ───────────── quaternion ─────────────

    [NebulaUnitTest]
    public void Quaternion_BitsResolveFromStep()
    {
        Assert.Equal(10, QuantizedCodec.ResolveQuatBits(0.002f));
        Assert.Equal(10, QuantizedCodec.ResolveQuatBits(0.0001f)); // capped
        Assert.Equal(8, QuantizedCodec.ResolveQuatBits(0.006f));
        Assert.Equal(0, QuantizedCodec.ResolveQuatBits(0f));
        for (int bits = 2; bits <= 10; bits++)
        {
            Assert.True(QuantizedCodec.QuatQuantum(bits) <= QuantizedCodec.QuatQuantum(bits - 1 < 2 ? 2 : bits - 1), $"quantum not monotone at {bits}");
        }
    }

    [NebulaUnitTest]
    public void Quaternion_PackUnpack_WithinMaxError_PreservesHemisphere()
    {
        var rng = new Random(5);
        foreach (int bits in new[] { 8, 9, 10 })
        {
            float step = QuantizedCodec.QuatQuantum(bits);
            Assert.Equal(bits, QuantizedCodec.ResolveQuatBits(step));
            float maxError = QuantizedCodec.MaxError(SerialVariantType.Quaternion, false, step);
            float worst = 0f;
            for (int i = 0; i < 3000; i++)
            {
                var axis = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1).Normalized();
                var q = new Quaternion(axis, (float)(rng.NextDouble() * Math.PI * 2 - Math.PI));
                if (i % 7 == 0) q = -q; // the other sign of the same rotation
                var back = QuantizedCodec.UnpackQuat(QuantizedCodec.PackQuat(q, bits), bits);
                Assert.True(MathF.Abs(back.Length() - 1f) < 1e-4f, $"bits={bits} not unit: {back.Length()}");
                float angle = q.AngleTo(back);
                worst = MathF.Max(worst, angle);
                Assert.True(angle <= maxError, $"bits={bits} {q}: angle {angle} > MaxError {maxError}");
                // Largest component positive on the wire: q and -q decode identically.
                var backNeg = QuantizedCodec.UnpackQuat(QuantizedCodec.PackQuat(-q, bits), bits);
                Assert.True(back == backNeg, $"bits={bits} q/-q decoded differently: {back} vs {backNeg}");
            }
            Assert.True(worst > maxError * 0.15f, $"bits={bits}: MaxError {maxError} absurdly loose vs observed {worst}");
        }
        // Identity and axis-aligned rotations are exact-ish at every width.
        var id = QuantizedCodec.UnpackQuat(QuantizedCodec.PackQuat(Quaternion.Identity, 10), 10);
        Assert.True(Quaternion.Identity.AngleTo(id) < 1e-3f, $"identity decoded as {id}");
    }

    // ───────────── MaxError for the grid types ─────────────

    [NebulaUnitTest]
    public void MaxError_GridTypes_BoundBruteForce()
    {
        var rng = new Random(11);
        const float step = 0.01f;
        float worst3 = 0f, worst2 = 0f, worst1 = 0f;
        Span<int> codes = stackalloc int[3];
        for (int i = 0; i < 5000; i++)
        {
            var v = new Vector3((float)rng.NextDouble() * 200 - 100, (float)rng.NextDouble() * 200 - 100, (float)rng.NextDouble() * 200 - 100);
            var c3 = new PropertyCache { Type = SerialVariantType.Vector3, Vec3Value = v };
            QuantizedCodec.Canonicalize(ref c3, SerialVariantType.Vector3, false, step);
            worst3 = MathF.Max(worst3, (c3.Vec3Value - v).Length());
            var c2 = new PropertyCache { Type = SerialVariantType.Vector2, Vec2Value = new Vector2(v.X, v.Y) };
            QuantizedCodec.Canonicalize(ref c2, SerialVariantType.Vector2, false, step);
            worst2 = MathF.Max(worst2, (c2.Vec2Value - new Vector2(v.X, v.Y)).Length());
            var c1 = new PropertyCache { Type = SerialVariantType.Float, FloatValue = v.Z };
            QuantizedCodec.Canonicalize(ref c1, SerialVariantType.Float, false, step);
            worst1 = MathF.Max(worst1, MathF.Abs(c1.FloatValue - v.Z));
        }
        // Float32 representation of the product adds a relative 1e-7 on top of the grid.
        const float slack = 1.001f;
        Assert.True(worst3 <= QuantizedCodec.MaxError(SerialVariantType.Vector3, false, step) * slack);
        Assert.True(worst2 <= QuantizedCodec.MaxError(SerialVariantType.Vector2, false, step) * slack);
        Assert.True(worst1 <= QuantizedCodec.MaxError(SerialVariantType.Float, false, step) * slack);
        Assert.True(worst1 > step * 0.4f, "float sample never approached a half step - test is not exercising the bound");
    }

    private static bool BitEqual(Vector3 a, Vector3 b)
        => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
        && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
        && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);
}
