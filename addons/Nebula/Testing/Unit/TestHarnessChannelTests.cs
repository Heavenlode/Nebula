using System;
using System.Collections.Generic;
using Nebula.Diagnostics;
using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// The test-harness side channel: its frame layout, and the fingerprint that decides when a peer's
/// input map is worth re-sending.
///
/// <para>Two of these carry most of the weight. <c>Signature_IsOrderIndependent</c> pins the
/// property that stops the map re-sending on every tick, and <c>Signature_CoversEveryField</c> pins
/// the one that stops a real change going unnoticed — a fingerprint over the node count or the id
/// set would pass a naive test while being wrong, because one player yields two entries sharing a
/// single local node id. <c>TruncatedFrame_*</c> and <c>UnknownOpcode_*</c> protect the rule that
/// nothing on this channel may throw: the pump turns any exception into a malformed-packet
/// disconnect, so an unreadable frame has to degrade to a no-op rather than kill the peer.</para>
/// </summary>
[NebulaUnitTest]
public class TestHarnessChannelTests
{
    private static NetBuffer Buffer() => new(256, usePool: false);

    private static TestHarnessChannel.InputMapEntry Entry(
        ushort localNodeId = 1, byte staticChildId = 0, byte sceneId = 7, ushort inputSize = 14)
        => new(localNodeId, staticChildId, sceneId, inputSize);

    /// <summary>Builds a whole OpInputMap frame the way MaybeSendInputMap does.</summary>
    private static byte[] InputMapFrame(params TestHarnessChannel.InputMapEntry[] entries)
    {
        var buffer = Buffer();
        NetWriter.WriteByte(buffer, TestHarnessChannel.OpInputMap);
        NetWriter.WriteByte(buffer, (byte)entries.Length);
        foreach (var entry in entries) TestHarnessChannel.WriteInputMapEntry(buffer, in entry);
        return buffer.WrittenSpan.ToArray();
    }

    private static TestHarnessChannel.InputMapSignature SignatureOf(
        IEnumerable<TestHarnessChannel.InputMapEntry> entries)
    {
        var signature = default(TestHarnessChannel.InputMapSignature);
        foreach (var entry in entries) signature.Add(in entry);
        return signature;
    }

    [NebulaUnitTest]
    public void InputMap_RoundTripsEveryField()
    {
        var written = new[]
        {
            Entry(localNodeId: 1, staticChildId: 3, sceneId: 12, inputSize: 14),
            Entry(localNodeId: 1, staticChildId: 4, sceneId: 12, inputSize: 6),
            Entry(localNodeId: 511, staticChildId: 0, sceneId: 255, inputSize: ushort.MaxValue),
        };

        var destination = new TestHarnessChannel.InputMapEntry[8];
        Assert.True(TestHarnessChannel.TryReadInputMap(InputMapFrame(written), destination, out int count));
        Assert.Equal(written.Length, count);

        for (int i = 0; i < count; i++)
        {
            Assert.Equal(written[i].LocalNodeId, destination[i].LocalNodeId);
            Assert.Equal(written[i].StaticChildId, destination[i].StaticChildId);
            Assert.Equal(written[i].SceneId, destination[i].SceneId);
            Assert.Equal(written[i].InputSize, destination[i].InputSize);
        }
    }

    [NebulaUnitTest]
    public void InputMap_LengthIsExactlyWhatTheConstantsSay()
    {
        var frame = InputMapFrame(Entry(), Entry(localNodeId: 2), Entry(localNodeId: 3));
        Assert.Equal(
            TestHarnessChannel.OpcodeBytes
                + TestHarnessChannel.InputMapCountBytes
                + 3 * TestHarnessChannel.InputMapEntryBytes,
            frame.Length);
    }

    [NebulaUnitTest]
    public void InputMap_EmptyIsHeaderOnly()
    {
        var frame = InputMapFrame();
        Assert.Equal(TestHarnessChannel.OpcodeBytes + TestHarnessChannel.InputMapCountBytes, frame.Length);

        var destination = new TestHarnessChannel.InputMapEntry[4];
        Assert.True(TestHarnessChannel.TryReadInputMap(frame, destination, out int count));
        Assert.Equal(0, count);
    }

    [NebulaUnitTest]
    public void Hello_RoundTrips()
    {
        var buffer = Buffer();
        TestHarnessChannel.WriteHello(
            buffer, TestHarnessChannel.KindSyntheticPeer, TestHarnessChannel.HelloFlagSubscribeInputMap);

        var bytes = buffer.WrittenSpan.ToArray();
        Assert.Equal(TestHarnessChannel.OpcodeBytes + TestHarnessChannel.HelloPayloadBytes, bytes.Length);
        Assert.Equal(TestHarnessChannel.OpHello, bytes[0]);
        Assert.Equal(TestHarnessChannel.KindSyntheticPeer, bytes[1]);
        Assert.Equal(TestHarnessChannel.HelloFlagSubscribeInputMap, bytes[2]);
    }

    /// <summary>
    /// Every truncation of a valid frame must be refused rather than parsed or thrown on. Feeding
    /// each prefix in turn is what catches an off-by-one in the length check, which is exactly the
    /// bug that would turn a short read into a disconnect for the whole peer.
    /// </summary>
    [NebulaUnitTest]
    public void TruncatedFrame_IsRefusedAtEveryPrefix()
    {
        var frame = InputMapFrame(Entry(), Entry(localNodeId: 2, staticChildId: 1));
        var destination = new TestHarnessChannel.InputMapEntry[8];

        for (int length = 0; length < frame.Length; length++)
        {
            var prefix = new ReadOnlySpan<byte>(frame, 0, length);
            Assert.False(
                TestHarnessChannel.TryReadInputMap(prefix, destination, out _),
                $"prefix of length {length} was accepted");
        }

        Assert.True(TestHarnessChannel.TryReadInputMap(frame, destination, out int count));
        Assert.Equal(2, count);
    }

    [NebulaUnitTest]
    public void InputMap_RefusedWhenItWouldOverrunTheCallersBuffer()
    {
        var frame = InputMapFrame(Entry(), Entry(localNodeId: 2), Entry(localNodeId: 3));
        var tooSmall = new TestHarnessChannel.InputMapEntry[2];
        Assert.False(TestHarnessChannel.TryReadInputMap(frame, tooSmall, out _));
    }

    [NebulaUnitTest]
    public void UnknownOpcode_IsRefusedNotThrown()
    {
        var destination = new TestHarnessChannel.InputMapEntry[4];
        foreach (byte opcode in new byte[] { 0x00, 0x02, 0x7F, 0x80, 0x82, 0xFF })
        {
            var frame = new byte[] { opcode, 0 };
            Assert.False(TestHarnessChannel.TryReadInputMap(frame, destination, out _));
        }
    }

    /// <summary>
    /// The map is derived from a HashSet, whose iteration order shifts as it is mutated. If the
    /// fingerprint depended on order, the server would decide the map had changed on ticks where it
    /// had not and re-send it forever.
    /// </summary>
    [NebulaUnitTest]
    public void Signature_IsOrderIndependent()
    {
        var entries = new List<TestHarnessChannel.InputMapEntry>
        {
            Entry(localNodeId: 1, staticChildId: 3, sceneId: 12, inputSize: 14),
            Entry(localNodeId: 1, staticChildId: 4, sceneId: 12, inputSize: 6),
            Entry(localNodeId: 9, staticChildId: 0, sceneId: 3, inputSize: 20),
        };

        var forwards = SignatureOf(entries);
        entries.Reverse();
        Assert.Equal(forwards, SignatureOf(entries));

        // And under an arbitrary shuffle, not just a reversal.
        var rng = new Random(4242);
        for (int trial = 0; trial < 50; trial++)
        {
            for (int i = entries.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (entries[i], entries[j]) = (entries[j], entries[i]);
            }
            Assert.Equal(forwards, SignatureOf(entries));
        }
    }

    /// <summary>
    /// Changing any single field has to move the fingerprint. The two entries here share a local
    /// node id on purpose: that is the real shape of a player who owns a ship and a character as
    /// static children of one spawned scene, and it is why a fingerprint over ids or counts alone
    /// would be wrong.
    /// </summary>
    [NebulaUnitTest]
    public void Signature_CoversEveryField()
    {
        var baseline = SignatureOf(new[]
        {
            Entry(localNodeId: 1, staticChildId: 3, sceneId: 12, inputSize: 14),
            Entry(localNodeId: 1, staticChildId: 4, sceneId: 12, inputSize: 6),
        });

        Assert.NotEqual(baseline, SignatureOf(new[]
        {
            Entry(localNodeId: 2, staticChildId: 3, sceneId: 12, inputSize: 14),   // localNodeId
            Entry(localNodeId: 1, staticChildId: 4, sceneId: 12, inputSize: 6),
        }));

        Assert.NotEqual(baseline, SignatureOf(new[]
        {
            Entry(localNodeId: 1, staticChildId: 5, sceneId: 12, inputSize: 14),   // staticChildId
            Entry(localNodeId: 1, staticChildId: 4, sceneId: 12, inputSize: 6),
        }));

        Assert.NotEqual(baseline, SignatureOf(new[]
        {
            Entry(localNodeId: 1, staticChildId: 3, sceneId: 13, inputSize: 14),   // sceneId
            Entry(localNodeId: 1, staticChildId: 4, sceneId: 12, inputSize: 6),
        }));

        Assert.NotEqual(baseline, SignatureOf(new[]
        {
            Entry(localNodeId: 1, staticChildId: 3, sceneId: 12, inputSize: 15),   // inputSize
            Entry(localNodeId: 1, staticChildId: 4, sceneId: 12, inputSize: 6),
        }));
    }

    [NebulaUnitTest]
    public void Signature_NoticesAdditionAndRemoval()
    {
        var one = Entry(localNodeId: 1, staticChildId: 3);
        var two = Entry(localNodeId: 1, staticChildId: 4, inputSize: 6);

        var single = SignatureOf(new[] { one });
        var pair = SignatureOf(new[] { one, two });

        Assert.NotEqual(single, pair);
        Assert.NotEqual(default, single);
        Assert.Equal(1, single.Count);
        Assert.Equal(2, pair.Count);

        // Removing the second gets back to exactly the first, so a peer that gains and then loses a
        // node is not left believing it still has it.
        Assert.Equal(single, SignatureOf(new[] { one }));
    }

    /// <summary>
    /// An empty map must be distinguishable from "never computed", or a peer that loses its last
    /// input node would never be told.
    /// </summary>
    [NebulaUnitTest]
    public void Signature_EmptyEqualsDefault()
    {
        Assert.Equal(default, SignatureOf(Array.Empty<TestHarnessChannel.InputMapEntry>()));
        Assert.NotEqual(default, SignatureOf(new[] { Entry() }));
    }

    /// <summary>
    /// Duplicate entries are not collapsed: SUM counts a repeat, XOR alone would cancel it. Two
    /// identical entries can't arise from a HashSet today, but a fingerprint that silently ignored
    /// multiplicity would be a trap for the next message added to this channel.
    /// </summary>
    [NebulaUnitTest]
    public void Signature_DoesNotCancelDuplicates()
    {
        var entry = Entry();
        Assert.NotEqual(SignatureOf(new[] { entry }), SignatureOf(new[] { entry, entry }));
        Assert.NotEqual(default, SignatureOf(new[] { entry, entry }));
    }

    [NebulaUnitTest]
    public void ChannelId_IsClearOfEveryReservedChannel()
    {
        // Blastoff owns 249; Nebula's own channels are the low ENetChannelId ones. 250 is nominally
        // valid but does not work in practice, which is why this is 248.
        Assert.Equal(248, TestHarnessChannel.ChannelId);
        Assert.True(TestHarnessChannel.ChannelId > (byte)NetRunner.ENetChannelId.World);
        Assert.NotEqual(249, TestHarnessChannel.ChannelId);
    }
}
