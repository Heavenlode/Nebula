using System;
using Nebula;
using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// The tick payload's bit-level framing: node sets (dense or gap-coded, writer picks the
/// shorter), the serializers-run word with its props-only shortcut, and the ledger's
/// worst-case charges being true upper bounds.
/// </summary>
[NebulaUnitTest]
public class PacketFramingTests
{
    private static NetBuffer Buffer() => new(256, usePool: false);

    [NebulaUnitTest]
    public void NodeSet_RoundTrips_DenseAndSparse_AtEveryPhase()
    {
        var rng = new Random(31);
        long[] masks =
        {
            1, 1L << 63, 0b1111, unchecked((long)0xFFFFFFFFFFFFFFFF), 0x0F0F0F0F0F0F0F0F,
            (1L << 5) | (1L << 6) | (1L << 7) | (1L << 8), 1L << 40 | 1L << 41 | 1L << 42 | 1L << 60,
        };
        for (int trial = 0; trial < 300; trial++)
        {
            long mask = trial < masks.Length ? masks[trial] : (long)((ulong)rng.Next() << 32 | (uint)rng.Next()) & (trial % 2 == 0 ? 0x00000000_0000FFFFL : -1L);
            if (mask == 0) mask = 1;
            int phase = rng.Next(0, 8);
            var buf = Buffer();
            if (phase > 0) buf.WriteBits(0x3, phase);
            PacketFraming.WriteNodeSet(buf, mask);
            Assert.Equal(phase + PacketFraming.NodeSetBits(mask), buf.WrittenBits);
            Assert.True(PacketFraming.NodeSetBits(mask) <= PacketFraming.NodeSetWorstBits);

            buf.ResetRead();
            if (phase > 0) buf.ReadBits(phase);
            Assert.Equal(mask, PacketFraming.ReadNodeSet(buf));
            Assert.True(buf.IsReadComplete);
        }
    }

    // The 20-peer shape: five players x four contiguous nodes is far cheaper sparse; a full
    // group is dense; twenty nodes spread every third id fall back to dense.
    [NebulaUnitTest]
    public void NodeSet_PicksTheShorterForm()
    {
        long clustered = 0;
        for (int p = 0; p < 5; p++) for (int n = 0; n < 4; n++) clustered |= 1L << (p * 7 + n);
        Assert.True(PacketFraming.NodeSetBits(clustered) < 48, $"clustered costs {PacketFraming.NodeSetBits(clustered)} bits");

        long spread = 0;
        for (int i = 0; i < 20; i++) spread |= 1L << (i * 3);
        Assert.Equal(PacketFraming.NodeSetWorstBits, PacketFraming.NodeSetBits(spread));

        Assert.Equal(PacketFraming.NodeSetWorstBits, PacketFraming.NodeSetBits(-1L));
        Assert.Equal(1 + 6 + 1, PacketFraming.NodeSetBits(1L));   // one node at index 0: gamma(1) = 1 bit
    }

    [NebulaUnitTest]
    public void NodeSet_RejectsEmpty()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PacketFraming.WriteNodeSet(Buffer(), 0));
    }

    [NebulaUnitTest]
    public void SerializersRun_PropsOnlyIsOneBit_OthersFourBits()
    {
        const int propsIndex = 1;
        foreach (byte run in new byte[] { 0b010, 0b001, 0b011, 0b100, 0b110, 0b111 })
        {
            var buf = Buffer();
            PacketFraming.WriteSerializersRun(buf, run, propsIndex);
            Assert.Equal(run == 0b010 ? 1 : PacketFraming.SerializersRunWorstBits, buf.WrittenBits);
            buf.ResetRead();
            Assert.Equal(run, PacketFraming.ReadSerializersRun(buf, propsIndex));
        }
    }
}
