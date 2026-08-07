using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace Nebula.Serialization
{
    /// <summary>
    /// Serializes a <see cref="BsonValue"/> into a buffer that is REUSED across calls, so a
    /// periodic large save stops churning the large object heap.
    ///
    /// <para>The problem this exists for, measured on the server with dotnet-trace: every
    /// <c>BsonDocument.ToBson()</c> grew an internal MemoryStream by doubling
    /// (131 KB → 262 KB → 524 KB → 1 MB of pure garbage), produced a right-sized ~786 KB copy,
    /// and the caller then copied that AGAIN into a protobuf ByteString — 3.5 MB of large-object
    /// traffic to ship 786 KB, every 5 seconds. The LOH is only reclaimed by a gen2 collection,
    /// so that alone drove a gen2 roughly every 10 seconds.</para>
    ///
    /// <para>A MemoryStream never shrinks its backing array, so resetting it between writes and
    /// slicing <see cref="WrittenMemory"/> to the byte count reaches a high-water mark after a
    /// few saves and then allocates nothing at all.</para>
    ///
    /// <para><b>Ownership.</b> <see cref="WrittenMemory"/> ALIASES the internal buffer — nothing
    /// is copied. A consumer that hands it to an async API (protobuf's
    /// <c>UnsafeByteOperations.UnsafeWrap</c>, then a fire-and-forget gRPC call) keeps reading it
    /// after the call returns, so the buffer must not be reused until that consumer is finished.
    /// Rent with <see cref="Rent"/> and <see cref="Return"/> exactly once per rent, from the
    /// point where the consumer is provably done (for an RPC: its response continuation, in a
    /// <c>finally</c>). Note this type is deliberately NOT IDisposable: a <c>using</c> would
    /// release the buffer at the end of the synchronous block, which is precisely the bug.</para>
    ///
    /// <para>Known, accepted leak mode: if the consumer never signals completion (an RPC with no
    /// deadline against a hung server), that buffer never returns to the pool. This degrades
    /// gracefully — <see cref="Rent"/> simply allocates a fresh one — rather than corrupting.</para>
    /// </summary>
    public sealed class BsonWriteBuffer
    {
        /// <summary>
        /// Starting capacity. Deliberately under the 85,000-byte large-object threshold so even a
        /// brand-new buffer starts on the normal heap; it grows to the payload's high-water mark
        /// within the first few writes and stays there.
        /// </summary>
        public const int DefaultCapacity = 64 * 1024;

        /// <summary>Buffers kept for reuse. Beyond this, <see cref="Return"/> drops them for the GC.</summary>
        private const int MaxPooledBuffers = 32;

        /// <summary>
        /// A buffer grown past this is dropped rather than pooled, so one pathological document
        /// cannot permanently pin memory in every pool slot.
        /// </summary>
        private const int MaxRetainedCapacity = 8 * 1024 * 1024;

        // Rent happens on the save thread and Return on an RPC continuation thread, i.e. reliably
        // different threads - the worst case for ConcurrentBag's thread-local bias. A plain locked
        // Stack is both simpler and LIFO, so the warmest buffer is reused. Contention is a handful
        // of operations every few seconds.
        private static readonly Stack<BsonWriteBuffer> _pool = new();
        private static readonly object _poolLock = new();

        private readonly MemoryStream _stream;
        private int _length;

        /// <summary>Guards against a double <see cref="Return"/>. Read/written under <see cref="_poolLock"/>.</summary>
        private bool _pooled;

        private BsonWriteBuffer()
        {
            // The int-capacity constructor produces an expandable, exposable stream, which is what
            // makes GetBuffer() legal below.
            _stream = new MemoryStream(DefaultCapacity);
        }

        /// <summary>Bytes written by the last <see cref="Write"/>.</summary>
        public int Length => _length;

        /// <summary>Current backing-array size. Only grows, and only up to the largest payload seen.</summary>
        public int Capacity => _stream.Capacity;

        /// <summary>
        /// The bytes written by the last <see cref="Write"/>, as a view over the internal buffer —
        /// no copy. The slice is what makes stale bytes from a previous, longer write unreachable;
        /// the buffer is never cleared, because zeroing ~800 KB per save would buy nothing.
        /// Valid until this buffer is reused or returned to the pool.
        /// </summary>
        public ReadOnlyMemory<byte> WrittenMemory => new(_stream.GetBuffer(), 0, _length);

        /// <summary>
        /// Serializes <paramref name="value"/> into the buffer, replacing any previous contents.
        /// </summary>
        public void Write(BsonValue value)
        {
            ArgumentNullException.ThrowIfNull(value);

            _length = 0;
            _stream.Position = 0;
            _stream.SetLength(0);

            // A fresh writer per document: after a top-level WriteEndDocument the writer's state is
            // Done, so it cannot be reused. It is a small short-lived object and the whole point of
            // this class is the large buffer underneath - do not "optimise" this into a cached
            // writer. BsonBinaryWriter does not own or dispose the stream (documented on its ctor).
            //
            // Nominal type BsonValue, NOT BsonDocument: the callers' documents are held in
            // BsonValue-typed variables, so the ToBson() this replaces passed BsonValue. Changing
            // the nominal type risks silently changing the bytes that get persisted.
            using (var writer = new BsonBinaryWriter(_stream))
            {
                BsonSerializer.Serialize(writer, typeof(BsonValue), value);
            }

            // Read the length AFTER the writer is closed, and from Length rather than Position:
            // writing a C-string reserves worst-case UTF-8 bytes, so intermediate positions and the
            // capacity both overstate the payload.
            _length = (int)_stream.Length;

            // A BSON document opens with its own total length as a little-endian int32. Comparing it
            // to what we measured is four bytes of work that turns a stale-trailing-bytes or
            // over-reserve bug into an exception here, instead of a corrupt record discovered later
            // in a database.
            if (value is BsonDocument && _length >= sizeof(int))
            {
                var declared = BinaryPrimitives.ReadInt32LittleEndian(WrittenMemory.Span);
                if (declared != _length)
                {
                    throw new InvalidOperationException(
                        $"BSON length mismatch: document declares {declared} bytes, buffer holds {_length}.");
                }
            }
        }

        /// <summary>
        /// Takes a buffer from the pool, or creates one when the pool is empty. Never blocks and
        /// never throws: a save must not stall waiting for a buffer.
        /// </summary>
        public static BsonWriteBuffer Rent()
        {
            lock (_poolLock)
            {
                if (_pool.Count > 0)
                {
                    var pooled = _pool.Pop();
                    pooled._pooled = false;
                    return pooled;
                }
            }
            return new BsonWriteBuffer();
        }

        /// <summary>
        /// Returns a buffer for reuse. Must be called exactly once per <see cref="Rent"/>, and only
        /// once every consumer of <see cref="WrittenMemory"/> is finished with it.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The buffer was already returned. This is worth throwing over rather than tolerating: a
        /// double return hands the same array to two callers, so two live payloads alias one buffer
        /// and one of them silently ships corrupted bytes.
        /// </exception>
        public static void Return(BsonWriteBuffer buffer)
        {
            if (buffer == null)
            {
                return;
            }

            lock (_poolLock)
            {
                if (buffer._pooled)
                {
                    throw new InvalidOperationException(
                        "BsonWriteBuffer returned twice; its buffer may be aliased by another payload.");
                }

                if (_pool.Count >= MaxPooledBuffers || buffer.Capacity > MaxRetainedCapacity)
                {
                    // Dropped, not pooled. Still mark it so a later double return is caught.
                    buffer._pooled = true;
                    return;
                }

                buffer._pooled = true;
                buffer._length = 0;
                _pool.Push(buffer);
            }
        }

        /// <summary>Test seam: empties the pool so a test starts from a known state.</summary>
        internal static void ClearPoolForTests()
        {
            lock (_poolLock)
            {
                _pool.Clear();
            }
        }

        /// <summary>Test seam: how many buffers are currently pooled.</summary>
        internal static int PooledCountForTests
        {
            get { lock (_poolLock) { return _pool.Count; } }
        }
    }
}
