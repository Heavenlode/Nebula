using System;
using Nebula;
using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// The self-delimiting integer forms of the bit stream: exactness first, then the widths the
/// plan's byte table was computed from.
/// </summary>
[NebulaUnitTest]
public class BitCodecTests
{
    private static NetBuffer Buffer() => new(64, usePool: false);

    [NebulaUnitTest]
    public void Magnitude_RoundTrips_WithExpectedWidths()
    {
        (int value, int bits)[] cases =
        {
            (0, 1), (1, 7), (-1, 7), (2, 8), (3, 8), (-4, 9), (255, 14), (256, 15),
            (60123, 22), (-60123, 22), (1 << 20, 27), (int.MaxValue, 37), (int.MinValue, 38),
        };
        foreach (var (value, bits) in cases)
        {
            var buf = Buffer();
            BitCodec.WriteMagnitude(buf, value);
            Assert.Equal(bits, buf.WrittenBits);
            Assert.Equal(bits, BitCodec.MagnitudeBits(value));
            buf.ResetRead();
            Assert.Equal(value, BitCodec.ReadMagnitude(buf));
            Assert.True(buf.IsReadComplete);
        }
    }

    [NebulaUnitTest]
    public void Magnitude_RandomSweep_Exact_AtAnyPhase()
    {
        var rng = new Random(3);
        for (int i = 0; i < 5000; i++)
        {
            int value = rng.Next(int.MinValue, int.MaxValue);
            if (i % 50 == 0) value = rng.Next(-100, 100); // dense on the small values the deltas use
            int phase = rng.Next(0, 8);
            var buf = Buffer();
            if (phase > 0) buf.WriteBits(0xFF, phase);
            BitCodec.WriteMagnitude(buf, value);
            buf.ResetRead();
            if (phase > 0) buf.ReadBits(phase);
            Assert.Equal(value, BitCodec.ReadMagnitude(buf));
        }
    }

    [NebulaUnitTest]
    public void Gamma_RoundTrips_WithExpectedWidths()
    {
        (uint value, int bits)[] cases =
        {
            (1, 1), (2, 3), (3, 3), (4, 5), (7, 5), (8, 7), (15, 7), (16, 9), (63, 11), (64, 13), (511, 17),
        };
        foreach (var (value, bits) in cases)
        {
            var buf = Buffer();
            BitCodec.WriteGamma(buf, value);
            Assert.Equal(bits, buf.WrittenBits);
            Assert.Equal(bits, BitCodec.GammaBits(value));
            buf.ResetRead();
            Assert.Equal(value, BitCodec.ReadGamma(buf));
            Assert.True(buf.IsReadComplete);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => BitCodec.WriteGamma(Buffer(), 0));
    }

    // The packet's sparse group form: 20 players' nodes are allocated in runs (gap 1 costs
    // one bit), so the 20-peer shape must beat the dense 64-bit mask by a wide margin, while
    // 20 nodes spread every third id must NOT - which is why the writer picks per group.
    [NebulaUnitTest]
    public void Gamma_GroupShape_SparseBeatsDenseOnlyWhenClustered()
    {
        int Cost(int[] indices)
        {
            int bits = 6 + BitCodec.GammaBits((uint)indices[0] + 1);
            for (int i = 1; i < indices.Length; i++) bits += BitCodec.GammaBits((uint)(indices[i] - indices[i - 1]));
            return bits;
        }
        // Five players x four contiguous nodes, players 3 ids apart.
        var clustered = new int[20];
        for (int p = 0; p < 5; p++) for (int n = 0; n < 4; n++) clustered[p * 4 + n] = p * 7 + n;
        var spread = new int[20]; for (int i = 0; i < 20; i++) spread[i] = i * 3;
        Assert.True(Cost(clustered) < 48, $"clustered 20 nodes cost {Cost(clustered)} bits");
        Assert.True(Cost(spread) >= 64, $"spread 20 nodes cost {Cost(spread)} bits; dense must win here");
    }
}
