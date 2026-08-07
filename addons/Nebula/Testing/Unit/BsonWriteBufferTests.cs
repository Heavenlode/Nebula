using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Nebula.Serialization;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// BsonWriteBuffer replaces `doc.ToBson()` + `ByteString.CopyFrom` on the periodic world save,
/// which allocated ~3.5 MB of large-object garbage every 5 seconds (a doubling growth run, a
/// right-sized copy, then a protobuf copy of that) and was the sole driver of the server's gen2
/// collections.
///
/// The properties worth pinning are: the bytes must be identical to what ToBson produced, a
/// shorter document must not leak the previous one's tail, and the backing array must actually be
/// REUSED after it reaches high water — that last one is the whole point of the change.
/// </summary>
[NebulaUnitTest]
public class BsonWriteBufferTests
{
    private static BsonDocument SmallDoc() => new()
    {
        ["name"] = "Zara",
        ["level"] = 42,
        ["health"] = 93.5,
        ["alive"] = true,
    };

    /// <summary>A document comfortably larger than BsonWriteBuffer.DefaultCapacity (64 KB).</summary>
    private static BsonDocument BigDoc(int entries = 8000)
    {
        var doc = new BsonDocument();
        for (var i = 0; i < entries; i++)
        {
            doc[$"key_{i}"] = $"value_{i}_padding_padding";
        }
        return doc;
    }

    private static BsonDocument RoundTrip(BsonWriteBuffer buffer)
        => BsonSerializer.Deserialize<BsonDocument>(buffer.WrittenMemory.ToArray());

    // 1. The bytes must match what the replaced ToBson() call produced. This is what pins the
    //    nominal-type choice (BsonValue, matching the BsonValue-typed variables at the call sites)
    //    and the ambient BsonDefaults settings - a divergence here silently changes what is
    //    persisted, which no runtime assertion would ever catch.
    [NebulaUnitTest]
    public void Write_ProducesBytesIdenticalToToBson()
    {
        var doc = SmallDoc();
        var buffer = BsonWriteBuffer.Rent();
        try
        {
            buffer.Write(doc);

            Assert.Equal(doc.ToBson(), buffer.WrittenMemory.ToArray());
            Assert.Equal(doc, RoundTrip(buffer));
        }
        finally
        {
            BsonWriteBuffer.Return(buffer);
        }
    }

    // 2. A shorter document after a longer one must not expose the tail of the previous write.
    //    Correctness here comes from the LENGTH SLICE, not from clearing the buffer - the test
    //    asserts the stale bytes are still physically present so that nobody later "fixes" this
    //    with an ~800 KB memset per save.
    [NebulaUnitTest]
    public void Write_ShorterDocument_DoesNotLeakPreviousTail()
    {
        var buffer = BsonWriteBuffer.Rent();
        try
        {
            buffer.Write(BigDoc());
            int bigLength = buffer.Length;

            var small = SmallDoc();
            buffer.Write(small);

            Assert.True(buffer.Length < bigLength);
            Assert.Equal(small, RoundTrip(buffer));
            // BSON opens with its own total length; the slice and the document must agree.
            Assert.Equal(buffer.Length, BinaryPrimitives.ReadInt32LittleEndian(buffer.WrittenMemory.Span));

            // The old document's bytes are still in the array beyond Length - unreachable, not erased.
            Assert.True(MemoryMarshal.TryGetArray<byte>(buffer.WrittenMemory, out var seg));
            bool tailStillDirty = false;
            for (var i = buffer.Length; i < bigLength; i++)
            {
                if (seg.Array[i] != 0) { tailStillDirty = true; break; }
            }
            Assert.True(tailStillDirty);
        }
        finally
        {
            BsonWriteBuffer.Return(buffer);
        }
    }

    // 3. Growing past the initial capacity has to work and stay correct.
    [NebulaUnitTest]
    public void Write_GrowsBeyondDefaultCapacity()
    {
        var buffer = BsonWriteBuffer.Rent();
        try
        {
            var big = BigDoc();
            buffer.Write(big);

            Assert.True(buffer.Length > BsonWriteBuffer.DefaultCapacity);
            Assert.True(buffer.Capacity >= buffer.Length);
            Assert.Equal(big, RoundTrip(buffer));
        }
        finally
        {
            BsonWriteBuffer.Return(buffer);
        }
    }

    // 4. THE test for the actual fix: once the buffer has reached high water, a second
    //    same-sized write must reuse the very same array. If MemoryStream ever stopped retaining
    //    its buffer across SetLength(0), the LOH churn would silently come back and only this
    //    assertion would notice.
    [NebulaUnitTest]
    public void Write_AtHighWaterMark_ReusesTheSameArray()
    {
        var buffer = BsonWriteBuffer.Rent();
        try
        {
            buffer.Write(BigDoc());
            Assert.True(MemoryMarshal.TryGetArray<byte>(buffer.WrittenMemory, out var first));
            int capacityAfterGrowth = buffer.Capacity;

            buffer.Write(BigDoc());
            Assert.True(MemoryMarshal.TryGetArray<byte>(buffer.WrittenMemory, out var second));

            Assert.Same(first.Array, second.Array);
            Assert.Equal(capacityAfterGrowth, buffer.Capacity);
        }
        finally
        {
            BsonWriteBuffer.Return(buffer);
        }
    }

    // 5. Pool mechanics. The double-return guard matters most: a double return hands one array to
    //    two payloads, and the loser silently ships corrupted bytes.
    [NebulaUnitTest]
    public void Pool_ReusesReturnedBuffers_AndRejectsDoubleReturn()
    {
        BsonWriteBuffer.ClearPoolForTests();

        var a = BsonWriteBuffer.Rent();
        var b = BsonWriteBuffer.Rent();
        Assert.NotSame(a, b);

        BsonWriteBuffer.Return(a);
        Assert.Equal(1, BsonWriteBuffer.PooledCountForTests);
        Assert.Same(a, BsonWriteBuffer.Rent());

        BsonWriteBuffer.Return(a);
        Assert.Throws<InvalidOperationException>(() => BsonWriteBuffer.Return(a));

        BsonWriteBuffer.Return(b);
        BsonWriteBuffer.ClearPoolForTests();
    }

    // 6. Rent/Return happen on different threads in production (save thread vs RPC continuation),
    //    and several worlds can save at once. Each thread must only ever see its own bytes. The
    //    local mock DataBuddy never reads the payload, so this stands in for a hazard that
    //    integration testing cannot reach.
    [NebulaUnitTest]
    public void Pool_IsSafeAcrossThreads()
    {
        const int threads = 4;
        const int iterations = 25;
        var failures = new List<string>();
        var failureLock = new object();
        var workers = new Thread[threads];

        for (var t = 0; t < threads; t++)
        {
            int id = t;
            workers[t] = new Thread(() =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    var doc = new BsonDocument { ["thread"] = id, ["iteration"] = i };
                    var buffer = BsonWriteBuffer.Rent();
                    try
                    {
                        buffer.Write(doc);
                        var read = BsonSerializer.Deserialize<BsonDocument>(buffer.WrittenMemory.ToArray());
                        if (read["thread"].AsInt32 != id || read["iteration"].AsInt32 != i)
                        {
                            lock (failureLock) failures.Add($"thread {id} iteration {i} read back {read.ToJson()}");
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (failureLock) failures.Add($"thread {id} iteration {i} threw {ex.Message}");
                    }
                    finally
                    {
                        BsonWriteBuffer.Return(buffer);
                    }
                }
            });
            workers[t].Start();
        }

        foreach (var worker in workers)
        {
            Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "worker thread did not finish");
        }

        Assert.Empty(failures);
        BsonWriteBuffer.ClearPoolForTests();
    }
}
