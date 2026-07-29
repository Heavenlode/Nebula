using System;
using Godot;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Tests for the lossiness detection behind the "settle absolute" mechanism.
///
/// A small delta carries half-precision components, so the client's reconstruction
/// (baseline + (float)(Half)delta) can land a few micro-units off the true value. The
/// server flags such sends in the peer's LossyMask and follows up with one absolute once
/// the property stops changing, so at-rest values are bit-exact on clients (a server-side
/// Vector3.Zero must never read as ~6e-6). These tests pin the helper math to the exact
/// reconstruction the client performs in ReadSmallDelta/ReadFullDelta.
/// </summary>
[NebulaUnitTest]
public class DeltaLossinessTests
{
    [NebulaUnitTest]
    public void TestSettleToZeroIsLossy()
    {
        // The reported bug: baseline 0.01, server settles to exactly 0. The half-rounded
        // delta does not cancel the baseline exactly, so the send must be flagged lossy.
        float baseline = 0.01f;
        float current = 0f;
        float delta = current - baseline;

        Assert.False(NetPropertiesSerializer.HalfDeltaIsLossless(baseline, delta, current));

        // And the residue is the ~1e-5-scale drift the user observed, not an exact zero.
        float reconstructed = baseline + (float)(Half)delta;
        Assert.NotEqual(0f, reconstructed);
        Assert.True(MathF.Abs(reconstructed) < 1e-4f);
    }

    [NebulaUnitTest]
    public void TestExactlyRepresentableDeltaIsLossless()
    {
        // 0.5 is exact in half precision, and 1.0 + 0.5 is exact in float.
        Assert.True(NetPropertiesSerializer.HalfDeltaIsLossless(1.0f, 0.5f, 1.5f));

        // Zero delta (resent unchanged value) reconstructs exactly.
        Assert.True(NetPropertiesSerializer.HalfDeltaIsLossless(3.25f, 0f, 3.25f));
    }

    [NebulaUnitTest]
    public void TestFullDeltaCanStillBeLossy()
    {
        // Full deltas carry float32, but the subtraction itself rounds when the delta's
        // exponent exceeds the target's, so reconstruction misses. baseline -1e8, current
        // 100: the true delta 100000100 is not a float (ulp there is 8) and rounds to
        // 100000096, so the client reconstructs 96, off by 4.
        float baseline = -1e8f;
        float current = 100f;
        float delta = current - baseline;

        Assert.Equal(100000096f, delta);
        Assert.Equal(96f, baseline + delta);
        Assert.False(NetPropertiesSerializer.FullDeltaIsLossless(baseline, delta, current));

        // The typical same-sign case reconstructs exactly and must not be flagged.
        Assert.True(NetPropertiesSerializer.FullDeltaIsLossless(5000f, 1500f, 6500f));
    }

    [NebulaUnitTest]
    public void TestVector3SettleToZeroComponentwise()
    {
        // Mirrors WriteDelta's Vector3 small-delta path: each component is checked
        // independently; one drifting component is enough to make the send lossy.
        var baseline = new Vector3(0.01f, 0f, 2f);
        var current = Vector3.Zero;
        var delta = current - baseline;

        bool lossless =
            NetPropertiesSerializer.HalfDeltaIsLossless(baseline.X, delta.X, current.X) &&
            NetPropertiesSerializer.HalfDeltaIsLossless(baseline.Y, delta.Y, current.Y) &&
            NetPropertiesSerializer.HalfDeltaIsLossless(baseline.Z, delta.Z, current.Z);

        Assert.False(lossless);

        // The Y component (0 -> 0) and Z component (2 -> 0, exact in half) are individually
        // fine; X (0.01 -> 0) is the drifter.
        Assert.True(NetPropertiesSerializer.HalfDeltaIsLossless(baseline.Y, delta.Y, current.Y));
        Assert.True(NetPropertiesSerializer.HalfDeltaIsLossless(baseline.Z, delta.Z, current.Z));
        Assert.False(NetPropertiesSerializer.HalfDeltaIsLossless(baseline.X, delta.X, current.X));
    }

    [NebulaUnitTest]
    public void TestHelperMatchesWireRoundTrip()
    {
        // The helper's (float)(Half)delta must be exactly what the client reads back from
        // NetWriter.WriteHalfFloat, for both drifting and exact deltas.
        Span<float> deltas = stackalloc float[] { -0.01f, 0f, 0.5f, -1.75f, 123.4f };

        using var buffer = new NetBuffer();
        foreach (var delta in deltas)
        {
            buffer.Reset();
            NetWriter.WriteHalfFloat(buffer, delta);
            buffer.ResetRead();
            float wire = NetReader.ReadHalfFloat(buffer);

            Assert.Equal(wire, (float)(Half)delta);
        }
    }
}
