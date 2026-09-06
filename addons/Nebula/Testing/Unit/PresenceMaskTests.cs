using System;
using Nebula;
using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// The presence-mask bit encoding as pure bit logic: one bit per declared property below
/// three mask bytes, header bit per byte plus eight per nonzero byte from three up. The
/// failure that matters on the read side is a byte the header skips being left stale, so
/// the decode tests hand in a scratch full of 0xFF.
/// </summary>
[NebulaUnitTest]
public class PresenceMaskTests
{
    private static NetBuffer Buffer() => new(32, usePool: false);

    [NebulaUnitTest]
    public void Rule_FlatBelowThree_TwoLevelFromThree()
    {
        Assert.False(PresenceMask.UsesTwoLevel(1));
        Assert.False(PresenceMask.UsesTwoLevel(2));
        for (int w = 3; w <= PresenceMask.MaxMaskBytes; w++)
        {
            Assert.True(PresenceMask.UsesTwoLevel(w));
            Assert.Equal(w + 8 * w, PresenceMask.WorstCaseBits(w, w * 8));
        }
        Assert.Equal(5, PresenceMask.WorstCaseBits(1, 5));
        Assert.Equal(13, PresenceMask.WorstCaseBits(2, 13));
    }

    // Narrow: exactly propertyCount bits, a 5-property scene costs 5 bits.
    [NebulaUnitTest]
    public void Flat_CostsOneBitPerProperty()
    {
        var buf = Buffer();
        PresenceMask.Write(buf, new byte[] { 0b10101 }, 5);
        Assert.Equal(5, buf.WrittenBits);
        Assert.Equal(5, PresenceMask.Bits(new byte[] { 0b10101 }, 5));

        buf.ResetRead();
        Span<byte> back = stackalloc byte[1];
        back.Fill(0xFF);
        PresenceMask.Read(buf, back, 5);
        Assert.Equal(0b10101, back[0]);
        Assert.True(buf.IsReadComplete);
    }

    // Narrow, two bytes: 13 properties cost 13 bits, split 8 + 5.
    [NebulaUnitTest]
    public void Flat_TwoBytes_SplitsAtThePropertyCount()
    {
        var buf = Buffer();
        var mask = new byte[] { 0xA5, 0b01100 };
        PresenceMask.Write(buf, mask, 13);
        Assert.Equal(13, buf.WrittenBits);
        buf.ResetRead();
        Span<byte> back = stackalloc byte[2];
        back.Fill(0xFF);
        PresenceMask.Read(buf, back, 13);
        Assert.Equal(0xA5, back[0]);
        Assert.Equal(0b01100, back[1]);
    }

    // Wide: header bit per byte + 8 per nonzero byte, ascending.
    [NebulaUnitTest]
    public void TwoLevel_HeaderPlusNonzeroBytes()
    {
        var buf = Buffer();
        var mask = new byte[] { 0, 0x11, 0, 0, 0, 0x40, 0x01, 0 };   // player shape: bytes 1, 5, 6
        PresenceMask.Write(buf, mask, 60);
        Assert.Equal(8 + 3 * 8, buf.WrittenBits);
        Assert.Equal(8 + 24, PresenceMask.Bits(mask, 60));

        buf.ResetRead();
        Assert.Equal(0b0110_0010UL, buf.ReadBits(8));   // header names bytes 1, 5, 6
        Assert.Equal(0x11UL, buf.ReadBits(8));
        Assert.Equal(0x40UL, buf.ReadBits(8));
        Assert.Equal(0x01UL, buf.ReadBits(8));
        Assert.True(buf.IsReadComplete);

        buf.ResetRead();
        Span<byte> back = stackalloc byte[8];
        back.Fill(0xFF);
        PresenceMask.Read(buf, back, 60);
        Assert.True(back.SequenceEqual(mask), "skipped bytes must be zeroed, not left stale");
    }

    // Wide, empty: just the header bits.
    [NebulaUnitTest]
    public void TwoLevel_Empty_IsHeaderOnly()
    {
        var buf = Buffer();
        PresenceMask.Write(buf, new byte[3], 24);
        Assert.Equal(3, buf.WrittenBits);
        buf.ResetRead();
        Span<byte> back = stackalloc byte[3];
        back.Fill(0xFF);
        PresenceMask.Read(buf, back, 24);
        Assert.True(back.SequenceEqual(new byte[3]));
    }

    // Wide, full: header + every byte, the worst case the writer budgets against.
    [NebulaUnitTest]
    public void TwoLevel_Full_IsWorstCase()
    {
        var mask = new byte[8];
        Array.Fill(mask, (byte)0xFF);
        var buf = Buffer();
        PresenceMask.Write(buf, mask, 64);
        Assert.Equal(PresenceMask.WorstCaseBits(8, 64), buf.WrittenBits);
        Assert.Equal(72, buf.WrittenBits);
    }

    // Round trip at every width, at every start phase, including single-bit and all-bits masks.
    [NebulaUnitTest]
    public void RoundTrip_EveryWidth_EveryPhase()
    {
        var rng = new Random(21);
        for (int width = 1; width <= PresenceMask.MaxMaskBytes; width++)
        {
            int propertyCount = width * 8 - rng.Next(0, 8);
            if (propertyCount <= (width - 1) * 8) propertyCount = (width - 1) * 8 + 1;
            for (int trial = 0; trial < 40; trial++)
            {
                var mask = new byte[width];
                for (int i = 0; i < width; i++) mask[i] = trial % 3 == 0 ? (byte)0 : (byte)rng.Next(256);
                // No bit at or above the property count.
                int lastBits = propertyCount - (width - 1) * 8;
                mask[width - 1] &= (byte)((1 << lastBits) - 1);
                if (trial == 1) mask[rng.Next(width)] |= 1;

                int phase = rng.Next(0, 8);
                var buf = Buffer();
                if (phase > 0) buf.WriteBits(0x7F, phase);
                PresenceMask.Write(buf, mask, propertyCount);
                Assert.Equal(phase + PresenceMask.Bits(mask, propertyCount), buf.WrittenBits);

                buf.ResetRead();
                if (phase > 0) buf.ReadBits(phase);
                var back = new byte[width];
                Array.Fill(back, (byte)0xFF);
                PresenceMask.Read(buf, back, propertyCount);
                Assert.Equal(mask, back);
                Assert.True(buf.IsReadComplete);
            }
        }
    }
}
