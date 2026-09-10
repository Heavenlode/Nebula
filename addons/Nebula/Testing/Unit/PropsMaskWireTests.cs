using System;
using Nebula;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// The props section with a WIDE presence mask (three bytes and up), which every other props
/// fixture in the suite never reaches (they declare one to four properties). Covers the
/// two-level mask on the wire, the header layout in front of it, its interplay with the
/// section memo and the self-limiting budget, and the mask-reuse bit.
///
/// Section header on the wire: [maskMode:1][age:5][mask unless maskMode]. Value decoding
/// needs the Protocol registry only for object properties; the Int fixtures here decode
/// fully through the Protocol-free constructor.
/// </summary>
[NebulaUnitTest]
public class PropsMaskWireTests
{
    private const int MaskModeBits = 1;
    private const int AgeBits = 5;

    private sealed class Fixture : IDisposable
    {
        public WorldRunner World;
        public NetPeer Peer;      // default(NetPeer): ID 0, mapped in PeerIds below
        public UUID PeerId;
        public NetNode Node;
        public NetPropertiesSerializer Serializer;
        public int PropertyCount;

        public Fixture(int intProps)
        {
            var propTypes = Ints(intProps);
            PropertyCount = intProps;
            World = new WorldRunner();
            Peer = default;
            PeerId = UUID.NewUUID();
            NetRunner.Instance.PeerIds[0] = PeerId;
            World.CreatePeerStateForTests(Peer, PeerId);

            Node = new NetNode();
            Node.Network.InterestLayers[PeerId] = 1;
            Node.Network.CurrentWorld = World;
            for (var i = 0; i < propTypes.Length; i++)
            {
                Node.Network.CachedProperties[i] = new PropertyCache { Type = propTypes[i], IntValue = 40 + i };
            }
            Serializer = new NetPropertiesSerializer(Node.Network, propTypes)
            {
                // Baselines (and so mask reuse and deltas) need the server-side value ring,
                // which Begin() captures only on a server or under this flag.
                ForceRingCaptureForTests = true,
            };
            World.SetClientSpawnState(Node.Network.NetId, Peer, WorldRunner.ClientSpawnState.Spawning);
        }

        /// <summary>One export at the given tick with exactly these property indices dirty.</summary>
        public ExportResult Export(int tick, NetBuffer buf, int maxBits, params int[] dirtyIndices)
        {
            long dirty = 0;
            foreach (var i in dirtyIndices) dirty |= 1L << i;
            World.CurrentTick = tick;
            Node.Network.DirtyMask = dirty;
            Serializer.Begin();
            return Serializer.Export(World, Peer, buf, maxBits);
        }

        public void Dispose()
        {
            NetRunner.Instance.PeerIds.Remove(0);
            Node.Free();
            World.Free();
        }
    }

    private static SerialVariantType[] Ints(int n)
    {
        var types = new SerialVariantType[n];
        Array.Fill(types, SerialVariantType.Int);
        return types;
    }

    private static NetBuffer Buffer() => new(1024, usePool: false);

    /// <summary>Parses the section header: mask mode, age, and the two-level header word.</summary>
    private static (bool reuse, int age, ulong header) Header(NetBuffer buf, int maskBytes)
    {
        buf.ResetRead();
        bool reuse = buf.ReadBool();
        int age = (int)buf.ReadBits(AgeBits);
        ulong header = reuse ? 0 : buf.ReadBits(maskBytes);
        return (reuse, age, header);
    }

    // 1. Three mask bytes, one dirty prop in the third: header names byte 2, then that byte.
    [NebulaUnitTest]
    public void OneDirtyByte_ShipsHeaderPlusOneMaskByte()
    {
        using var f = new Fixture(24);
        var buf = Buffer();

        Assert.Equal(ExportResult.Written, f.Export(1, buf, int.MaxValue, 20));

        var (reuse, age, header) = Header(buf, 3);
        Assert.False(reuse);
        Assert.Equal(0, age);
        Assert.Equal(0b100UL, header);            // only mask byte 2 is nonzero
        Assert.Equal(0x10UL, buf.ReadBits(8));    // index 20 = byte 2, bit 4
        Assert.Equal(1 + 5 + 3 + 8, buf.ReadBitPosition);
        Assert.True(buf.UnreadBits > 0);          // a value follows
    }

    // 2. Dirty props in two different mask bytes: both listed, ascending.
    [NebulaUnitTest]
    public void TwoDirtyBytes_HeaderListsBothAscending()
    {
        using var f = new Fixture(24);
        var buf = Buffer();

        Assert.Equal(ExportResult.Written, f.Export(1, buf, int.MaxValue, 0, 20));

        var (_, _, header) = Header(buf, 3);
        Assert.Equal(0b101UL, header);
        Assert.Equal(0x01UL, buf.ReadBits(8));
        Assert.Equal(0x10UL, buf.ReadBits(8));
    }

    // 3. The player's real shape: 60 props, ship transform in byte 5 and character movement
    //    in byte 6. Indices 46 and 48 dirty -> header names bytes 5 and 6.
    [NebulaUnitTest]
    public void PlayerShape_ShipAndCharacterBytes()
    {
        using var f = new Fixture(60);
        var buf = Buffer();

        Assert.Equal(ExportResult.Written, f.Export(1, buf, int.MaxValue, 46, 48));

        var (_, _, header) = Header(buf, 8);
        Assert.Equal(0b0110_0000UL, header);
        Assert.Equal(0x40UL, buf.ReadBits(8));   // byte 5: index 46 = bit 6
        Assert.Equal(0x01UL, buf.ReadBits(8));   // byte 6: index 48 = bit 0
    }

    // 4. The section is exactly [header][mask][values], no padding: compare two exports of
    //    the same single value whose only difference is which mask byte it lives in, then
    //    one with both. Byte 0 vs byte 2 must cost the same; both together must cost exactly
    //    one more mask byte plus one more value.
    [NebulaUnitTest]
    public void SectionLength_TracksCompactMaskExactly()
    {
        int single0, single20, both;
        using (var f = new Fixture(24))
        {
            var a = Buffer();
            f.Export(1, a, int.MaxValue, 0);
            single0 = a.WrittenBits;
        }
        using (var f = new Fixture(24))
        {
            var b = Buffer();
            f.Export(1, b, int.MaxValue, 20);
            single20 = b.WrittenBits;
        }
        using (var f = new Fixture(24))
        {
            var c = Buffer();
            f.Export(1, c, int.MaxValue, 0, 20);
            both = c.WrittenBits;
        }

        Assert.Equal(single0, single20);
        // An Int64 absolute is 2 + 64 bits, the header 1 + 5 + 3 + 8.
        const int valueBits = 2 + 64;
        const int oneByteHeader = 1 + 5 + 3 + 8;
        Assert.Equal(oneByteHeader + valueBits, single0);
        Assert.Equal(oneByteHeader + 8 + 2 * valueBits, both);
    }

    // 5. Memo interplay: a signature-matched second export (same tick, same dirty set) is a
    //    memo hit and lands byte-identical.
    [NebulaUnitTest]
    public void MemoHit_IsByteIdentical_AtWideMask()
    {
        using var f = new Fixture(24);
        f.World.CurrentTick = 1;
        f.Node.Network.DirtyMask = (1L << 3) | (1L << 20);
        f.Serializer.Begin();

        var first = Buffer();
        Assert.Equal(ExportResult.Written, f.Serializer.Export(f.World, f.Peer, first, int.MaxValue));
        Assert.Equal(0, f.Serializer.MemoHitsForTests);

        var second = Buffer();
        Assert.Equal(ExportResult.Written, f.Serializer.Export(f.World, f.Peer, second, int.MaxValue));
        Assert.Equal(1, f.Serializer.MemoHitsForTests);
        Assert.True(first.WrittenSpan.SequenceEqual(second.WrittenSpan));
        var (_, _, header) = Header(second, 3);
        Assert.Equal(0b101UL, header);
    }

    // 6. Budget: one bit under the full section still self-limits (never exceeds
    //    maxBits), reports Partial, and banks the leftover. The checks measure against
    //    the worst-case header, so the shipped section is comfortably inside the budget
    //    rather than exactly at it.
    [NebulaUnitTest]
    public void TightBudget_SelfLimits_AtWideMask()
    {
        int fullSize;
        using (var sizing = new Fixture(24))
        {
            var buf = Buffer();
            Assert.Equal(ExportResult.Written, sizing.Export(1, buf, int.MaxValue, 0, 1, 2));
            fullSize = buf.WrittenBits;
        }

        using var f = new Fixture(24);
        var tight = Buffer();
        int maxBits = fullSize - 1;
        var result = f.Export(1, tight, maxBits, 0, 1, 2);

        Assert.Equal(ExportResult.Partial, result);
        Assert.True(tight.WrittenBits <= maxBits);
        var (_, _, header) = Header(tight, 3);
        Assert.Equal(0b001UL, header);          // still byte 0 only
        Assert.NotEqual(0, f.Serializer.PendingDirtyByteForTests(f.PeerId, 0));
    }

    // 7. Nothing dirty writes nothing at all - no header, no age.
    [NebulaUnitTest]
    public void NothingDirty_WritesNothing()
    {
        using var f = new Fixture(24);
        var buf = Buffer();
        Assert.Equal(ExportResult.None, f.Export(1, buf, int.MaxValue));
        Assert.Equal(0, buf.WrittenBits);
    }

    // 8. Read side: an empty two-level mask with age 0 is [0][00000][000] - nine bits - and
    //    the reader consumes exactly those, leaving whatever follows untouched.
    [NebulaUnitTest]
    public void Decode_EmptyHeader_ConsumesExactlyNineBits()
    {
        using var f = new Fixture(24);
        var wire = new NetBuffer(16, usePool: false);
        wire.WriteBool(false);
        wire.WriteBits(0, AgeBits);
        wire.WriteBits(0, 3);
        wire.WriteBits(0x7F, 7);   // the next section's bits
        wire.ResetRead();

        Assert.True(f.Serializer.DeserializeForTests(wire, 1));
        Assert.Equal(9, wire.ReadBitPosition);
        Assert.Equal(0x7FUL, wire.ReadBits(7));
    }

    // 9. Mask reuse: the same wire mask as the peer's acked baseline tick costs one bit; a
    //    different mask does not. Driven end to end against a client fixture so the reused
    //    mask is proven to decode to the right properties.
    [NebulaUnitTest]
    public void MaskReuse_OneBitWhenBaselineMaskMatches_FullOtherwise()
    {
        using var f = new Fixture(24);
        var client = new NetNode();
        try
        {
            var clientSer = new NetPropertiesSerializer(client.Network, Ints(24));

            // Tick 1: absolute, full mask, applied and acked.
            var t1 = Buffer();
            Assert.Equal(ExportResult.Written, f.Export(1, t1, int.MaxValue, 3, 20));
            t1.ResetRead();
            Assert.True(clientSer.DeserializeForTests(t1, 1));
            f.Serializer.CommitExport(f.World, f.Peer, 1);
            f.Serializer.Acknowledge(f.World, f.Peer, 1);

            // Tick 2: same two props dirty -> same wire mask as the baseline -> 1 bit.
            var t2 = Buffer();
            Assert.Equal(ExportResult.Written, f.Export(2, t2, int.MaxValue, 3, 20));
            var (reuse, age, _) = Header(t2, 3);
            Assert.True(reuse);
            Assert.Equal(1, age);
            t2.ResetRead();
            Assert.True(clientSer.DeserializeForTests(t2, 2));
            Assert.Equal(f.Node.Network.CachedProperties[3].LongValue, clientSer.AppliedValueForTests(2, 3).LongValue);
            Assert.Equal(f.Node.Network.CachedProperties[20].LongValue, clientSer.AppliedValueForTests(2, 20).LongValue);
            f.Serializer.CommitExport(f.World, f.Peer, 2);
            f.Serializer.Acknowledge(f.World, f.Peer, 2);

            // Tick 3: a different set -> full mask on the wire.
            var t3 = Buffer();
            Assert.Equal(ExportResult.Written, f.Export(3, t3, int.MaxValue, 3));
            (reuse, age, _) = Header(t3, 3);
            Assert.False(reuse);
            Assert.Equal(1, age);
            t3.ResetRead();
            Assert.True(clientSer.DeserializeForTests(t3, 3));
        }
        finally
        {
            client.Free();
        }
    }

    // 10. Byte-coded values (strings, packed arrays, object properties) keep their byte
    //     codecs inside the bit stream. The body is aligned in the STREAM when the mask
    //     carries one, so a string preceded by an odd number of header and value bits still
    //     decodes exactly - the bug class that took the first bit-stream soak down.
    [NebulaUnitTest]
    public void ByteCodedValue_AfterBitFields_DecodesExactly()
    {
        var types = Ints(24);
        types[5] = SerialVariantType.String;
        types[6] = SerialVariantType.Bool;
        using var f = new Fixture(24);
        var server = new NetNode();
        var client = new NetNode();
        try
        {
            server.Network.InterestLayers[f.PeerId] = 1;
            server.Network.CurrentWorld = f.World;
            for (var i = 0; i < types.Length; i++)
            {
                server.Network.CachedProperties[i] = types[i] switch
                {
                    SerialVariantType.String => new PropertyCache { Type = types[i], StringValue = "ten chars!" },
                    SerialVariantType.Bool => new PropertyCache { Type = types[i], BoolValue = true },
                    _ => new PropertyCache { Type = types[i], IntValue = 40 + i },
                };
            }
            var serverSer = new NetPropertiesSerializer(server.Network, types) { ForceRingCaptureForTests = true };
            f.World.SetClientSpawnState(server.Network.NetId, f.Peer, WorldRunner.ClientSpawnState.Spawning);
            var clientSer = new NetPropertiesSerializer(client.Network, types);

            // Bits before the string: header (1+5+3+8+8) and an Int64 absolute (2+64) and the
            // string's own 2-bit flag - 93 bits, so the string would sit mid-byte without the pad.
            f.World.CurrentTick = 1;
            server.Network.DirtyMask = (1L << 3) | (1L << 5) | (1L << 6) | (1L << 20);
            serverSer.Begin();
            var wire = Buffer();
            Assert.Equal(ExportResult.Written, serverSer.Export(f.World, f.Peer, wire, int.MaxValue));

            Assert.Equal(1, wire.AlignMarkCount);   // the body start asks for a byte boundary

            // The host appends the section at an arbitrary bit phase (here 5) and pads at the
            // mark; the client, reading the final stream, must land exactly.
            var packet = Buffer();
            packet.WriteBits(0x1F, 5);
            packet.AppendBitsApplyingMarks(wire);
            packet.ResetRead();
            packet.ReadBits(5);
            Assert.True(clientSer.DeserializeForTests(packet, 1));
            Assert.Equal("ten chars!", clientSer.AppliedValueForTests(1, 5).StringValue);
            Assert.True(clientSer.AppliedValueForTests(1, 6).BoolValue);
            Assert.Equal(60L, clientSer.AppliedValueForTests(1, 20).LongValue);
            Assert.Equal(43L, clientSer.AppliedValueForTests(1, 3).LongValue);
            Assert.True(packet.IsReadComplete, "the reader must consume exactly the section");
        }
        finally
        {
            server.Free();
            client.Free();
        }
    }

    // 11. Read side: a reused mask whose baseline this client never applied is unparseable
    //     and must throw (ImportState aborts the tick, un-acked), not silently discard.
    [NebulaUnitTest]
    public void Decode_MaskReuseWithoutBaseline_Throws()
    {
        using var f = new Fixture(24);
        var wire = new NetBuffer(16, usePool: false);
        wire.WriteBool(true);
        wire.WriteBits(3, AgeBits);
        wire.ResetRead();

        Assert.Throws<InvalidOperationException>(() => f.Serializer.DeserializeForTests(wire, 10));
    }
}
