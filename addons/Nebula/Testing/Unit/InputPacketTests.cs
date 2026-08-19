using System;
using System.Collections.Generic;
using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Tests for the redundant-input record encoding shared by WorldRunner.SendInput and
/// WorldRunner.ReceiveInput.
///
/// The encoding sends the input size once for the whole packet and each record's tick as a one-byte
/// offset from the newest, instead of repeating a 4-byte tick and a 4-byte length on all 8 redundant
/// copies. That saves ~50 bytes per input packet, but it means the writer can no longer represent
/// every record it is handed - a gap in the input ring can push an older tick out of one-byte range.
/// These tests pin down what happens then, and that reading mirrors writing exactly.
/// </summary>
[NebulaUnitTest]
public class InputPacketTests
{
    private const int InputSize = 26;   // PlayerShipInput: 14 bools + a Vector3, Pack = 1

    private static byte[] Payload(byte seed)
    {
        var bytes = new byte[InputSize];
        for (int i = 0; i < InputSize; i++) bytes[i] = (byte)(seed + i);
        return bytes;
    }

    /// <summary>Newest-first run of consecutive ticks, as GetRecentInputs produces.</summary>
    private static List<(Tick, byte[])> Consecutive(Tick newest, int count)
    {
        var records = new List<(Tick, byte[])>();
        for (int i = 0; i < count; i++) records.Add((newest - i, Payload((byte)i)));
        return records;
    }

    /// <summary>Mirrors ReceiveInput's parse of the record section.</summary>
    private static List<(Tick, byte[])> ReadRecords(NetBuffer buffer, int inputSize)
    {
        var read = new List<(Tick, byte[])>();
        var count = NetReader.ReadByte(buffer);
        var baseTick = NetReader.ReadInt32(buffer);
        for (int i = 0; i < count; i++)
        {
            var tick = baseTick - NetReader.ReadByte(buffer);
            read.Add((tick, NetReader.ReadBytes(buffer, inputSize)));
        }
        return read;
    }

    private static List<(Tick, byte[])> RoundTrip(List<(Tick, byte[])> records, out byte written)
    {
        var buffer = new NetBuffer(4096, usePool: false);
        written = WorldRunner.WriteInputRecords(buffer, records, InputSize);
        buffer.ResetRead();
        var read = ReadRecords(buffer, InputSize);

        // Nothing may be left over: a trailing byte would mean writer and reader disagree, which in
        // the real packet would misalign everything after it.
        Assert.True(buffer.IsReadComplete, "reader did not consume exactly what the writer produced");
        return read;
    }

    private static void AssertRecordsEqual(List<(Tick, byte[])> expected, List<(Tick, byte[])> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Item1, actual[i].Item1);
            Assert.Equal(expected[i].Item2, actual[i].Item2);
        }
    }

    [NebulaUnitTest]
    public void RoundTrip_FullRedundancyWindow()
    {
        var records = Consecutive(1000, 8);
        var read = RoundTrip(records, out byte written);

        Assert.Equal(8, written);
        AssertRecordsEqual(records, read);
    }

    [NebulaUnitTest]
    public void RoundTrip_EveryCountFromZeroToEight()
    {
        for (int count = 0; count <= 8; count++)
        {
            var records = Consecutive(500, count);
            var read = RoundTrip(records, out byte written);

            Assert.Equal(count, written);
            AssertRecordsEqual(records, read);
        }
    }

    [NebulaUnitTest]
    public void RoundTrip_SingleRecord()
    {
        var records = Consecutive(7, 1);
        var read = RoundTrip(records, out byte written);

        Assert.Equal(1, written);
        AssertRecordsEqual(records, read);
    }

    [NebulaUnitTest]
    public void Encoding_IsSmallerThanTheOldPerRecordFraming()
    {
        var buffer = new NetBuffer(4096, usePool: false);
        WorldRunner.WriteInputRecords(buffer, Consecutive(1000, 8), InputSize);

        // Old: 8 x (4 tick + 4 length + payload). New: count + baseTick + 8 x (1 delta + payload).
        int oldSize = 8 * (4 + 4 + InputSize);
        int newSize = buffer.Length;

        Assert.True(newSize < oldSize, $"expected a saving, got {newSize} vs {oldSize}");
        Assert.Equal(1 + 4 + 8 * (1 + InputSize), newSize);
    }

    // ---------------------------------------------------------------- truncation

    [NebulaUnitTest]
    public void Truncates_WhenATickFallsOutsideOneByteRange()
    {
        // A long gap in the input ring: the third record is more than 255 ticks behind the newest,
        // so it and everything after it cannot be expressed and get dropped.
        var records = new List<(Tick, byte[])>
        {
            (1000, Payload(0)),
            (999,  Payload(1)),
            (700,  Payload(2)),   // delta 300 -> out of range
            (699,  Payload(3)),
        };

        var read = RoundTrip(records, out byte written);

        Assert.Equal(2, written);
        AssertRecordsEqual(records.GetRange(0, 2), read);
    }

    [NebulaUnitTest]
    public void Truncates_AtExactlyTheOneByteBoundary()
    {
        // delta 255 is representable; 256 is not.
        var records = new List<(Tick, byte[])>
        {
            (1000, Payload(0)),
            (1000 - 255, Payload(1)),
            (1000 - 256, Payload(2)),
        };

        var read = RoundTrip(records, out byte written);

        Assert.Equal(2, written);
        AssertRecordsEqual(records.GetRange(0, 2), read);
    }

    [NebulaUnitTest]
    public void Truncates_OnAWrongSizedRecord()
    {
        // The size is sent once for the whole packet, so a record of a different length cannot be
        // represented. Dropping it is safe; writing it would misalign every later record.
        var records = new List<(Tick, byte[])>
        {
            (50, Payload(0)),
            (49, new byte[InputSize + 1]),
            (48, Payload(2)),
        };

        var read = RoundTrip(records, out byte written);

        Assert.Equal(1, written);
        AssertRecordsEqual(records.GetRange(0, 1), read);
    }

    [NebulaUnitTest]
    public void Truncates_OnANullRecord()
    {
        var records = new List<(Tick, byte[])>
        {
            (50, Payload(0)),
            (49, null),
            (48, Payload(2)),
        };

        var read = RoundTrip(records, out byte written);

        Assert.Equal(1, written);
        AssertRecordsEqual(records.GetRange(0, 1), read);
    }

    // ---------------------------------------------------------------- edge cases

    [NebulaUnitTest]
    public void Empty_WritesHeaderOnlyAndReadsBackEmpty()
    {
        var read = RoundTrip(new List<(Tick, byte[])>(), out byte written);

        Assert.Equal(0, written);
        Assert.Empty(read);
    }

    [NebulaUnitTest]
    public void HandlesTickZeroAndSmallTicks()
    {
        // Early in a session baseTick is near zero; deltas must not run negative into a wrap.
        var records = new List<(Tick, byte[])> { (2, Payload(0)), (1, Payload(1)), (0, Payload(2)) };
        var read = RoundTrip(records, out byte written);

        Assert.Equal(3, written);
        AssertRecordsEqual(records, read);
    }

    [NebulaUnitTest]
    public void StopsAtAnOutOfOrderRecord()
    {
        // GetRecentInputs is newest-first, so an ascending tick would mean a negative delta.
        // Truncating is correct - encoding it would decode as a wildly wrong tick.
        var records = new List<(Tick, byte[])> { (100, Payload(0)), (101, Payload(1)) };
        var read = RoundTrip(records, out byte written);

        Assert.Equal(1, written);
        AssertRecordsEqual(records.GetRange(0, 1), read);
    }

    [NebulaUnitTest]
    public void BackfilledCountMatchesWhatWasActuallyWritten()
    {
        // The count is reserved before the records and patched afterwards. If that backfill were
        // wrong, the reader would consume the wrong number of records and desync the packet - so
        // assert the declared count against the bytes rather than trusting the return value.
        var records = new List<(Tick, byte[])>
        {
            (1000, Payload(0)),
            (999,  Payload(1)),
            (600,  Payload(2)),   // dropped
        };

        var buffer = new NetBuffer(4096, usePool: false);
        byte written = WorldRunner.WriteInputRecords(buffer, records, InputSize);

        Assert.Equal(2, written);
        Assert.Equal(written, buffer.WrittenSpan[0]);                     // count is the first byte
        Assert.Equal(1 + 4 + written * (1 + InputSize), buffer.Length);   // and the size agrees
    }
}
