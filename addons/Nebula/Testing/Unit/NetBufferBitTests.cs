using System;
using Nebula;
using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// NetBuffer's bit cursors. The contract that matters most is the silent auto-align: a byte
/// call on a mid-byte cursor pads, the mirrored reader skips the same pad, and nobody ever
/// aligns by hand. The second is that a rewound partial byte carries no stale bits, because
/// byte-level comparisons (memo verify, checksums) read whole bytes.
/// </summary>
[NebulaUnitTest]
public class NetBufferBitTests
{
    private static NetBuffer Buffer(int capacity = 256) => new(capacity, usePool: false);

    private static readonly ulong[] Patterns =
    {
        0, 1, 0xA5, 0x5A5A, 0xDEADBEEF, 0x0123456789ABCDEFUL, ulong.MaxValue, 0x8000000000000001UL,
    };

    // 1. Every width at every start phase round-trips, with a neighbour on each side.
    [NebulaUnitTest]
    public void RoundTrip_EveryWidth_EveryPhase()
    {
        for (int phase = 0; phase < 8; phase++)
        {
            for (int width = 1; width <= 64; width++)
            {
                foreach (var pattern in Patterns)
                {
                    ulong expected = width == 64 ? pattern : pattern & ((1UL << width) - 1);
                    var buf = Buffer();
                    if (phase > 0) buf.WriteBits(0x55, phase);
                    buf.WriteBits(pattern, width);
                    buf.WriteBits(0x3, 2);
                    Assert.Equal(phase + width + 2, buf.WrittenBits);

                    buf.ResetRead();
                    if (phase > 0) Assert.Equal(0x55UL & ((1UL << phase) - 1), buf.ReadBits(phase));
                    Assert.Equal(expected, buf.ReadBits(width));
                    Assert.Equal(3UL, buf.ReadBits(2));
                    Assert.True(buf.IsReadComplete);
                }
            }
        }
    }

    // 2. The user-facing promise: bits and bytes interleave with NO align calls, the reader
    //    mirrors the writer, and the pad is exactly the bits to the next boundary.
    [NebulaUnitTest]
    public void MixedBitsAndBytes_NoManualAlign_ReaderMirrors()
    {
        var buf = Buffer();
        NetWriter.WriteBit(buf, true);
        NetWriter.WriteBits(buf, 0x1AB, 9);              // cursor at bit 10
        NetWriter.WriteInt16(buf, -1234);                // pads 6 bits, writes at byte 2
        NetWriter.WriteBits(buf, 5, 3);                  // bit 35
        NetWriter.WriteByte(buf, 0xEE);                  // pads 5, byte 5
        NetWriter.WriteBit(buf, false);
        Assert.Equal(49, buf.WrittenBits);
        Assert.Equal(7, buf.Length);

        buf.ResetRead();
        Assert.True(NetReader.ReadBit(buf));
        Assert.Equal(0x1ABUL, NetReader.ReadBits(buf, 9));
        Assert.Equal(-1234, NetReader.ReadInt16(buf));
        Assert.Equal(5UL, NetReader.ReadBits(buf, 3));
        Assert.Equal(0xEE, NetReader.ReadByte(buf));
        Assert.False(NetReader.ReadBit(buf));
        Assert.True(buf.IsReadComplete);

        // The pad bits are zero on the wire.
        Assert.Equal(0, buf.WrittenSpan[1] >> 2);
        Assert.Equal(0, buf.WrittenSpan[4] >> 3);
    }

    // 3. The delegate idiom: snapshot WritePosition at an aligned point, write, hash the
    //    RawBuffer slice, rewind with the byte setter, keep writing bits. Nothing leaks.
    [NebulaUnitTest]
    public void DelegateSnapshotRestore_ByteSetter_ClearsAbandonedBits()
    {
        var buf = Buffer();
        buf.WriteBits(0x7, 3);
        NetWriter.WriteByte(buf, 0x11);                  // aligned at byte 1 -> byte 2 now
        int start = buf.WritePosition;
        Assert.Equal(2, start);
        NetWriter.WriteByte(buf, 0xFF);
        NetWriter.WriteByte(buf, 0xFF);
        buf.WriteBits(0x1F, 5);                          // partial byte 4 = 0x1F
        Assert.Equal(5, buf.WritePosition);
        Assert.Equal(0xFF, buf.RawBuffer[start]);

        buf.WritePosition = start;                       // the CargoState "send nothing" rewind
        Assert.Equal(16, buf.WriteBitPosition);
        buf.WriteBits(0x1, 1);                           // byte 2 must now be exactly 0x01
        Assert.Equal(0x01, buf.WrittenSpan[2]);
        Assert.Equal(3, buf.Length);
    }

    // 4. A bit-level rewind clears the abandoned bits above the cursor so WrittenSpan is
    //    byte-deterministic even when nothing is written after the rewind.
    [NebulaUnitTest]
    public void BitRewind_ClearsStaleHighBits()
    {
        var buf = Buffer();
        buf.WriteBits(0xFFF, 12);
        buf.WriteBitPosition = 10;
        Assert.Equal(0x03, buf.WrittenSpan[1]);
        Assert.Equal(2, buf.Length);

        buf.WriteBits(0x1, 1);
        Assert.Equal(0x07, buf.WrittenSpan[1]);
    }

    // 5. AppendBits / ExtractBits: shifting copies at every phase equal a bit-by-bit reference.
    [NebulaUnitTest]
    public void AppendAndExtract_AllPhases_MatchReference()
    {
        var rng = new Random(9);
        for (int trial = 0; trial < 200; trial++)
        {
            int srcBits = rng.Next(1, 300);
            var src = Buffer(64);
            var expected = new bool[srcBits];
            for (int i = 0; i < srcBits; i++) { expected[i] = rng.Next(2) == 1; src.WriteBool(expected[i]); }

            int phase = rng.Next(0, 8);
            var dst = Buffer(128);
            if (phase > 0) dst.WriteBits(0xAA, phase);
            dst.AppendBits(src);
            Assert.Equal(phase + srcBits, dst.WrittenBits);

            dst.ResetRead();
            if (phase > 0) dst.ReadBits(phase);
            for (int i = 0; i < srcBits; i++) Assert.Equal(expected[i], dst.ReadBool());

            // Extract back to phase 0 and compare bytes with the original source.
            Span<byte> outBytes = stackalloc byte[64];
            dst.ExtractBits(phase, srcBits, outBytes);
            int bytes = (srcBits + 7) / 8;
            Assert.True(outBytes.Slice(0, bytes).SequenceEqual(src.WrittenSpan), $"trial {trial} phase {phase} bits {srcBits}");
        }
    }

    // 5b. Align marks: a scratch marks a byte-boundary requirement; an intermediate buffer
    //     carries it re-based; the final stream pads at it, whatever phase the section lands
    //     at. A rewind below a mark drops it.
    [NebulaUnitTest]
    public void AlignMarks_CarriedThroughIntermediate_AppliedInFinalStream()
    {
        for (int phase = 0; phase < 8; phase++)
        {
            var section = Buffer();
            section.WriteBits(0x2A, 6);          // header of odd width
            section.MarkAlign();
            NetWriter.WriteInt16(section, -321);  // byte-coded body (aligned at phase 0 in the scratch)
            section.WriteBits(0x5, 3);

            var node = Buffer();
            node.WriteBits(0x1, 3);               // an earlier section of the same node
            node.AppendBits(section);             // carries the mark, re-based to bit 9
            Assert.Equal(1, node.AlignMarkCount);
            Assert.Equal(3 + 6, node.AlignMarkAt(0));

            var packet = Buffer();
            if (phase > 0) packet.WriteBits(0x7F, phase);
            packet.AppendBitsApplyingMarks(node);

            packet.ResetRead();
            if (phase > 0) packet.ReadBits(phase);
            Assert.Equal(0x1UL, packet.ReadBits(3));
            Assert.Equal(0x2AUL, packet.ReadBits(6));
            Assert.Equal(-321, NetReader.ReadInt16(packet));   // the reader aligns in the stream
            Assert.Equal(0x5UL, packet.ReadBits(3));
            Assert.True(packet.IsReadComplete);
        }

        var rewound = Buffer();
        rewound.WriteBits(1, 4);
        rewound.MarkAlign();
        rewound.WriteBits(1, 4);
        rewound.WriteBitPosition = 2;
        Assert.Equal(0, rewound.AlignMarkCount);
    }

    // 6. Derived byte positions and remaining counts.
    [NebulaUnitTest]
    public void BytePositions_AreCeilOfBits()
    {
        var buf = Buffer();
        Assert.Equal(0, buf.WritePosition);
        buf.WriteBits(1, 1);
        Assert.Equal(1, buf.WritePosition);
        buf.WriteBits(1, 7);
        Assert.Equal(1, buf.WritePosition);
        buf.WriteBits(1, 1);
        Assert.Equal(2, buf.WritePosition);
        Assert.Equal(2, buf.Length);
        Assert.Equal(9, buf.UnreadBits);
        Assert.Equal(2, buf.Remaining);
        buf.ReadBits(3);
        Assert.Equal(1, buf.ReadPosition);
        Assert.Equal(6, buf.UnreadBits);
        buf.ReadBits(6);
        Assert.True(buf.IsReadComplete);
    }

    // 7. Bounds: a bit read past the written bits throws; a bit write past capacity throws;
    //    a byte read after bits respects the padded position.
    [NebulaUnitTest]
    public void Bounds_AreEnforced()
    {
        var buf = Buffer(2);
        buf.WriteBits(0x3, 2);
        Assert.Throws<InvalidOperationException>(() => buf.WriteBits(0, 15));
        buf.WriteBits(0, 14);
        Assert.Equal(16, buf.WrittenBits);
        Assert.Throws<InvalidOperationException>(() => buf.WriteBits(1, 1));
        buf.ResetRead();
        buf.ReadBits(3);
        Assert.Throws<InvalidOperationException>(() => NetReader.ReadInt16(buf)); // needs 2 bytes after the pad
        Assert.Equal(0, NetReader.ReadByte(buf));
        Assert.Throws<InvalidOperationException>(() => buf.ReadBits(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.ReadBits(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.WriteBits(0, 65));
    }

    // 8. The byte API is unchanged when only byte calls are used (the whole existing protocol).
    [NebulaUnitTest]
    public void ByteOnlyUsage_IsIdenticalToBefore()
    {
        var buf = Buffer();
        NetWriter.WriteInt32(buf, 0x01020304);
        NetWriter.WriteFloat(buf, 1.5f);
        NetWriter.WriteString(buf, "abc");
        Assert.Equal(4 + 4 + 4 + 3, buf.Length);
        buf.ResetRead();
        Assert.Equal(0x01020304, NetReader.ReadInt32(buf));
        Assert.Equal(1.5f, NetReader.ReadFloat(buf));
        Assert.Equal("abc", NetReader.ReadString(buf));
        Assert.True(buf.IsReadComplete);
    }

    // 9. No allocation across a long mixed sequence.
    [NebulaUnitTest]
    public void MixedOps_DoNotAllocate()
    {
        var buf = Buffer(4096);
        var src = Buffer(64);
        src.WriteBits(0x123456789UL, 40);
        Span<byte> scratch = stackalloc byte[8];
        // Warm up.
        Drive(buf, src, scratch);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Drive(buf, src, scratch);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(before, after);
    }

    private static void Drive(NetBuffer buf, NetBuffer src, Span<byte> scratch)
    {
        buf.Reset();
        buf.WriteBits(0x5, 3);
        buf.AppendBits(src);
        NetWriter.WriteInt16(buf, 7);
        buf.WriteBool(true);
        buf.ExtractBits(3, 40, scratch);
        buf.ResetRead();
        buf.ReadBits(3);
        buf.ReadBits(40);
        NetReader.ReadInt16(buf);
        buf.ReadBool();
    }
}
