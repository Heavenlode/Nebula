using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Nebula.Serialization
{
    /// <summary>
    /// High-performance network buffer using ArrayPool for zero-allocation serialization.
    /// Replaces the old HLBuffer that extended Godot's RefCounted.
    ///
    /// <para><b>Bit cursors.</b> Both cursors are kept in BITS; the byte-granular
    /// <see cref="WritePosition"/> / <see cref="ReadPosition"/> are derived views
    /// (<c>ceil(bits / 8)</c>) whose setters seek to a byte boundary. Fields of any width go
    /// through <see cref="WriteBits"/> / <see cref="ReadBits"/>, LSB-first within the stream.
    /// Every byte-granular operation (<see cref="GetWriteSpan"/>, <see cref="GetReadSpan"/>, and
    /// so every NetWriter / NetReader call) <b>auto-aligns silently</b>: mid-byte, the writer pads
    /// the rest of the byte with zeros and the reader skips the same bits. Writer and reader
    /// execute the same call sequence, so they always agree, which is what lets bit and byte
    /// calls interleave with no alignment ceremony anywhere - a custom INetSerializable type
    /// never calls an align function. Padding is counted in
    /// <see cref="Diagnostics.TickProfiler.Counter.PadBits"/> so engine code cannot waste bits
    /// unnoticed.</para>
    ///
    /// <para>Bit writes are applied straight to the byte array (no accumulator): the partial
    /// byte's bits above the cursor are masked on every write, so a rewind to an earlier bit
    /// position can never leak stale bits from an abandoned write.</para>
    /// </summary>
    public sealed class NetBuffer : IDisposable
    {
        /// <summary>
        /// Default capacity based on network MTU (1500) plus some headroom for headers.
        /// </summary>
        public const int DefaultCapacity = 1536;

        private const int BitsPerByte = 8;
        private const int MaxBitsPerCall = 64;

        private byte[] _buffer;
        private int _capacity; // Not readonly: Attach() re-points wrapper instances at new data.
        private bool _disposed;
        private bool _isPooled;

        private int _writeBits;
        private int _readBits;

        /// <summary>
        /// Bit positions in this buffer that must land on a BYTE boundary in the final stream.
        /// A section is written into a scratch at phase 0 and later appended at an arbitrary
        /// bit phase, so a byte-coded value inside it (object property, string) cannot be
        /// aligned where it is written - only the final assembler knows the phase. The writer
        /// calls <see cref="MarkAlign"/> instead; <see cref="AppendBits(NetBuffer)"/> carries
        /// marks through intermediate buffers and <see cref="AppendBitsApplyingMarks"/> pads at
        /// them in the destination. The reader, which always sees the final stream, simply
        /// aligns at the same logical point.
        /// </summary>
        private const int MaxAlignMarks = 8;
        // A mark is the bit where the requirement was raised (_markStart) and the bit where the
        // content that must be aligned begins (_markContent = start rounded up to a byte). The
        // bits between are this buffer's own pad, so that content written after the mark by
        // byte ops sits at a byte boundary HERE too; the final assembler drops that pad and
        // inserts its own, so a mark costs at most 7 bits on the wire, never 14.
        private readonly int[] _markStart = new int[MaxAlignMarks];
        private readonly int[] _markContent = new int[MaxAlignMarks];
        private int _alignMarkCount;

        public int AlignMarkCount => _alignMarkCount;
        /// <summary>The bit position where mark <paramref name="index"/> was raised.</summary>
        public int AlignMarkAt(int index) => _markStart[index];

        /// <summary>
        /// Records that everything written from here on must be byte-aligned in the final
        /// stream, and pads this buffer to a byte so it is aligned here as well.
        /// </summary>
        public void MarkAlign()
        {
            if (_alignMarkCount == MaxAlignMarks)
                throw new InvalidOperationException($"more than {MaxAlignMarks} align marks in one buffer");
            _markStart[_alignMarkCount] = _writeBits;
            int bitInByte = _writeBits & 7;
            if (bitInByte != 0)
            {
                // Local pad, not counted: the assembler replaces it with the real one.
                _buffer[_writeBits >> 3] &= (byte)((1 << bitInByte) - 1);
                _writeBits += BitsPerByte - bitInByte;
            }
            _markContent[_alignMarkCount] = _writeBits;
            _alignMarkCount++;
        }

        private void DropMarksAtOrAfter(int bit)
        {
            // A rewind into or below a mark's pad invalidates the mark.
            while (_alignMarkCount > 0 && bit < _markContent[_alignMarkCount - 1]) _alignMarkCount--;
        }

        /// <summary>
        /// Write cursor in bits. Seeking backwards clears the abandoned bits above the new
        /// cursor in its byte, so <see cref="WrittenSpan"/> never carries stale bits from a
        /// rewound write (bit readers would not care; byte-level comparisons and checksums do).
        /// </summary>
        public int WriteBitPosition
        {
            get => _writeBits;
            set
            {
                if (value < _writeBits) DropMarksAtOrAfter(value);
                _writeBits = value;
                int bitInByte = value & 7;
                if (bitInByte != 0) _buffer[value >> 3] &= (byte)((1 << bitInByte) - 1);
            }
        }

        /// <summary>Read cursor in bits.</summary>
        public int ReadBitPosition
        {
            get => _readBits;
            set => _readBits = value;
        }

        /// <summary>Bits written so far.</summary>
        public int WrittenBits => _writeBits;

        /// <summary>Bits written but not yet read.</summary>
        public int UnreadBits => _writeBits - _readBits;

        /// <summary>
        /// Current write position in whole bytes: the number of bytes the written bits occupy.
        /// Setting it seeks to that byte boundary (any partial byte is abandoned).
        /// </summary>
        public int WritePosition
        {
            get => BytesFor(_writeBits);
            set
            {
                int bits = value * BitsPerByte;
                if (bits < _writeBits) DropMarksAtOrAfter(bits);
                _writeBits = bits;
            }
        }

        /// <summary>
        /// Current read position in whole bytes (the byte containing the next unread bit, rounded
        /// up when mid-byte). Setting it seeks to that byte boundary.
        /// </summary>
        public int ReadPosition
        {
            get => BytesFor(_readBits);
            set => _readBits = value * BitsPerByte;
        }

        /// <summary>
        /// Number of bytes written to the buffer.
        /// </summary>
        public int Length => WritePosition;

        /// <summary>
        /// Total capacity of the buffer.
        /// </summary>
        public int Capacity => _capacity;

        /// <summary>
        /// Whether all data has been read.
        /// </summary>
        public bool IsReadComplete => _readBits >= _writeBits;

        /// <summary>
        /// Number of whole bytes remaining to be read.
        /// </summary>
        public int Remaining => WritePosition - ReadPosition;

        /// <summary>
        /// Gets a span over the written portion of the buffer (whole bytes; a partial last byte
        /// is included with its unused high bits zero).
        /// </summary>
        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, WritePosition);

        /// <summary>
        /// Gets a span over the unread portion of the buffer, from the read cursor's byte.
        /// </summary>
        public ReadOnlySpan<byte> UnreadSpan => _buffer.AsSpan(ReadPosition, WritePosition - ReadPosition);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int BytesFor(int bits) => (bits + BitsPerByte - 1) >> 3;

        /// <summary>
        /// Gets a span for writing at the current write position. Auto-aligns to the next byte
        /// boundary first (see class doc).
        /// </summary>
        public Span<byte> GetWriteSpan(int length)
        {
            AlignWrite();
            EnsureCapacity(length);
            return _buffer.AsSpan(_writeBits >> 3, length);
        }

        /// <summary>
        /// Gets a span for reading at the current read position. Auto-aligns to the next byte
        /// boundary first (see class doc).
        /// </summary>
        public ReadOnlySpan<byte> GetReadSpan(int length)
        {
            AlignRead();
            // Reject negative lengths explicitly, and compare against Remaining rather than
            // `ReadPosition + length` so a hostile length (e.g. int.MaxValue) cannot overflow
            // past the guard and reach AsSpan with an out-of-range value.
            if (length < 0)
                throw new InvalidOperationException($"Cannot read negative length {length}");
            int readByte = _readBits >> 3;
            if (length > WritePosition - readByte)
                throw new InvalidOperationException($"Cannot read {length} bytes, only {WritePosition - readByte} remaining");
            return _buffer.AsSpan(readByte, length);
        }

        /// <summary>
        /// Direct access to the underlying buffer. Use with caution.
        /// </summary>
        public byte[] RawBuffer => _buffer;

        // ───────────────────────────── bit operations ─────────────────────────────

        /// <summary>
        /// Writes the low <paramref name="count"/> bits of <paramref name="value"/> (1..64),
        /// LSB first. Bits above the cursor in the current byte are cleared, so a rewound
        /// partial byte never leaks stale bits.
        /// </summary>
        public void WriteBits(ulong value, int count)
        {
            if ((uint)(count - 1) >= MaxBitsPerCall)
                throw new ArgumentOutOfRangeException(nameof(count), count, "1..64 bits per call");
            if (_writeBits + count > _capacity * BitsPerByte)
                throw new InvalidOperationException(
                    $"Buffer overflow: cannot write {count} bits at bit {_writeBits} (capacity: {_capacity} bytes)");

            while (count > 0)
            {
                int bitInByte = _writeBits & 7;
                int byteIndex = _writeBits >> 3;
                int take = Math.Min(BitsPerByte - bitInByte, count);
                byte chunk = (byte)(value & ((1u << take) - 1));
                byte keepMask = (byte)((1 << bitInByte) - 1);
                _buffer[byteIndex] = (byte)((_buffer[byteIndex] & keepMask) | (chunk << bitInByte));
                value >>= take;
                count -= take;
                _writeBits += take;
            }
        }

        /// <summary>Reads <paramref name="count"/> bits (1..64), LSB first.</summary>
        public ulong ReadBits(int count)
        {
            if ((uint)(count - 1) >= MaxBitsPerCall)
                throw new ArgumentOutOfRangeException(nameof(count), count, "1..64 bits per call");
            if (_readBits + count > _writeBits)
                throw new InvalidOperationException($"Cannot read {count} bits, only {UnreadBits} remaining");

            ulong result = 0;
            int shift = 0;
            while (count > 0)
            {
                int bitInByte = _readBits & 7;
                int take = Math.Min(BitsPerByte - bitInByte, count);
                ulong chunk = ((ulong)_buffer[_readBits >> 3] >> bitInByte) & ((1u << take) - 1);
                result |= chunk << shift;
                shift += take;
                count -= take;
                _readBits += take;
            }
            return result;
        }

        /// <summary>One bit.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBool(bool value) => WriteBits(value ? 1u : 0u, 1);

        /// <summary>One bit.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadBool() => ReadBits(1) != 0;

        /// <summary>
        /// Pads the write cursor to the next byte boundary with zero bits. Called implicitly by
        /// every byte-granular write; explicit use is for the engine's end-of-section pad only.
        /// </summary>
        public void AlignWrite()
        {
            int bitInByte = _writeBits & 7;
            if (bitInByte == 0) return;
            int pad = BitsPerByte - bitInByte;
            int byteIndex = _writeBits >> 3;
            _buffer[byteIndex] &= (byte)((1 << bitInByte) - 1);
            _writeBits += pad;
            Diagnostics.TickProfiler.Current?.Add(Diagnostics.TickProfiler.Counter.PadBits, pad);
        }

        /// <summary>Skips the read cursor to the next byte boundary (the writer's pad).</summary>
        public void AlignRead()
        {
            int bitInByte = _readBits & 7;
            if (bitInByte == 0) return;
            _readBits += BitsPerByte - bitInByte;
        }

        /// <summary>
        /// Appends <paramref name="bitCount"/> bits from <paramref name="src"/>, which holds them
        /// from bit 0 (phase 0), at the current write cursor, shifting as needed.
        /// </summary>
        public void AppendBits(ReadOnlySpan<byte> src, int bitCount)
        {
            if (bitCount < 0 || BytesFor(bitCount) > src.Length)
                throw new ArgumentOutOfRangeException(nameof(bitCount));
            if (_writeBits + bitCount > _capacity * BitsPerByte)
                throw new InvalidOperationException(
                    $"Buffer overflow: cannot append {bitCount} bits at bit {_writeBits} (capacity: {_capacity} bytes)");

            if ((_writeBits & 7) == 0)
            {
                // Aligned destination: straight byte copy, then trim the last byte's pad bits.
                int wholeBytes = bitCount >> 3;
                int dst = _writeBits >> 3;
                src.Slice(0, wholeBytes).CopyTo(_buffer.AsSpan(dst, wholeBytes));
                _writeBits += wholeBytes * BitsPerByte;
                int rest = bitCount & 7;
                if (rest != 0) WriteBits(src[wholeBytes], rest);
                return;
            }

            int srcBit = 0;
            while (bitCount > 0)
            {
                int take = Math.Min(MaxBitsPerCall, bitCount);
                WriteBits(ReadBitsFrom(src, srcBit, take), take);
                srcBit += take;
                bitCount -= take;
            }
        }

        /// <summary>
        /// Appends another buffer's written bits and CARRIES its align marks (re-based to this
        /// buffer). For an intermediate buffer whose own phase in the final stream is unknown.
        /// </summary>
        public void AppendBits(NetBuffer source)
        {
            int baseBit = _writeBits;
            for (int i = 0; i < source._alignMarkCount; i++)
            {
                if (_alignMarkCount == MaxAlignMarks)
                    throw new InvalidOperationException($"more than {MaxAlignMarks} align marks in one buffer");
                _markStart[_alignMarkCount] = baseBit + source._markStart[i];
                _markContent[_alignMarkCount] = baseBit + source._markContent[i];
                _alignMarkCount++;
            }
            AppendBits(source._buffer, source._writeBits);
        }

        /// <summary>
        /// Appends another buffer's written bits into the FINAL stream, padding to a byte at
        /// each of the source's align marks. Each mark costs at most 7 bits (counted as pad).
        /// </summary>
        public void AppendBitsApplyingMarks(NetBuffer source)
        {
            int from = 0;
            for (int i = 0; i < source._alignMarkCount; i++)
            {
                int start = source._markStart[i];
                if (start > from) AppendBitsRange(source._buffer, from, start - from);
                AlignWrite();                      // the real pad, at the final phase
                from = source._markContent[i];     // skip the source's own pad
            }
            if (source._writeBits > from) AppendBitsRange(source._buffer, from, source._writeBits - from);
        }

        /// <summary>Appends <paramref name="bitCount"/> bits of <paramref name="src"/> starting at bit <paramref name="fromBit"/>.</summary>
        private void AppendBitsRange(ReadOnlySpan<byte> src, int fromBit, int bitCount)
        {
            if ((fromBit & 7) == 0)
            {
                AppendBits(src.Slice(fromBit >> 3), bitCount);
                return;
            }
            if (_writeBits + bitCount > _capacity * BitsPerByte)
                throw new InvalidOperationException(
                    $"Buffer overflow: cannot append {bitCount} bits at bit {_writeBits} (capacity: {_capacity} bytes)");
            while (bitCount > 0)
            {
                int take = Math.Min(MaxBitsPerCall, bitCount);
                WriteBits(ReadBitsFrom(src, fromBit, take), take);
                fromBit += take;
                bitCount -= take;
            }
        }

        /// <summary>
        /// Copies <paramref name="bitCount"/> bits starting at absolute bit <paramref name="fromBit"/>
        /// into <paramref name="dst"/> at phase 0; the last byte's unused high bits are zero.
        /// </summary>
        public void ExtractBits(int fromBit, int bitCount, Span<byte> dst)
        {
            if (fromBit < 0 || bitCount < 0 || fromBit + bitCount > _writeBits)
                throw new ArgumentOutOfRangeException(nameof(bitCount));
            if (BytesFor(bitCount) > dst.Length)
                throw new ArgumentOutOfRangeException(nameof(dst));

            if ((fromBit & 7) == 0)
            {
                int wholeBytes = bitCount >> 3;
                _buffer.AsSpan(fromBit >> 3, wholeBytes).CopyTo(dst);
                int rest = bitCount & 7;
                if (rest != 0) dst[wholeBytes] = (byte)(_buffer[(fromBit >> 3) + wholeBytes] & ((1 << rest) - 1));
                return;
            }

            int outBit = 0;
            while (bitCount > 0)
            {
                int take = Math.Min(MaxBitsPerCall, bitCount);
                WriteBitsTo(dst, outBit, ReadBitsFrom(_buffer, fromBit, take), take);
                fromBit += take;
                outBit += take;
                bitCount -= take;
            }
        }

        private static ulong ReadBitsFrom(ReadOnlySpan<byte> src, int bitPos, int count)
        {
            ulong result = 0;
            int shift = 0;
            while (count > 0)
            {
                int bitInByte = bitPos & 7;
                int take = Math.Min(BitsPerByte - bitInByte, count);
                ulong chunk = ((ulong)src[bitPos >> 3] >> bitInByte) & ((1u << take) - 1);
                result |= chunk << shift;
                shift += take;
                count -= take;
                bitPos += take;
            }
            return result;
        }

        private static void WriteBitsTo(Span<byte> dst, int bitPos, ulong value, int count)
        {
            while (count > 0)
            {
                int bitInByte = bitPos & 7;
                int byteIndex = bitPos >> 3;
                int take = Math.Min(BitsPerByte - bitInByte, count);
                byte chunk = (byte)(value & ((1u << take) - 1));
                byte keepMask = (byte)((1 << bitInByte) - 1);
                dst[byteIndex] = (byte)((dst[byteIndex] & keepMask) | (chunk << bitInByte));
                value >>= take;
                count -= take;
                bitPos += take;
            }
        }

        // ───────────────────────────── construction ─────────────────────────────

        /// <summary>
        /// Creates a new NetBuffer with default capacity from the pool.
        /// </summary>
        public NetBuffer() : this(DefaultCapacity, usePool: true)
        {
        }

        /// <summary>
        /// Creates a new NetBuffer with specified capacity.
        /// </summary>
        /// <param name="capacity">Buffer capacity in bytes</param>
        /// <param name="usePool">Whether to rent from ArrayPool (true) or allocate directly (false)</param>
        public NetBuffer(int capacity, bool usePool = true)
        {
            _capacity = capacity;
            _isPooled = usePool;
            _buffer = usePool
                ? ArrayPool<byte>.Shared.Rent(capacity)
                : new byte[capacity];
            _writeBits = 0;
            _readBits = 0;
        }

        /// <summary>
        /// Creates a NetBuffer wrapping existing data for reading.
        /// The buffer is NOT pooled and will not be returned to ArrayPool.
        /// </summary>
        /// <param name="data">Existing byte array to wrap</param>
        public NetBuffer(byte[] data)
        {
            _buffer = data;
            _capacity = data.Length;
            _isPooled = false;
            _writeBits = data.Length * BitsPerByte;
            _readBits = 0;
        }

        /// <summary>
        /// Re-points this instance at existing data for reading, without allocating. The inbound
        /// packet paths parse one packet after another on a single thread; a long-lived wrapper
        /// re-attached per packet replaces a per-packet <c>new NetBuffer(byte[])</c>.
        ///
        /// Only legal on wrapper instances (created via <see cref="NetBuffer(byte[])"/>): a pooled
        /// instance would leak its rented storage on the first Attach.
        /// </summary>
        /// <param name="data">The array to read from. May be longer than <paramref name="length"/>
        /// (rented pool arrays are oversized); bytes beyond it are never read.</param>
        /// <param name="length">The number of valid bytes in <paramref name="data"/>.</param>
        public void Attach(byte[] data, int length)
        {
            if (_isPooled)
            {
                throw new InvalidOperationException("Attach is only valid on wrapper NetBuffers (created from an existing array).");
            }
            _buffer = data;
            _capacity = length;
            _writeBits = length * BitsPerByte;
            _readBits = 0;
        }

        /// <summary>
        /// Creates a NetBuffer by copying data from a span.
        /// </summary>
        /// <param name="data">Data to copy into the buffer</param>
        /// <param name="usePool">Whether to rent from ArrayPool</param>
        public NetBuffer(ReadOnlySpan<byte> data, bool usePool = true)
        {
            _capacity = Math.Max(data.Length, DefaultCapacity);
            _isPooled = usePool;
            _buffer = usePool
                ? ArrayPool<byte>.Shared.Rent(_capacity)
                : new byte[_capacity];
            data.CopyTo(_buffer);
            _writeBits = data.Length * BitsPerByte;
            _readBits = 0;
        }

        /// <summary>
        /// Ensures the buffer has enough capacity for the specified additional bytes at the
        /// (aligned) write position.
        /// </summary>
        private void EnsureCapacity(int additionalBytes)
        {
            if (WritePosition + additionalBytes > _capacity)
            {
                throw new InvalidOperationException(
                    $"Buffer overflow: cannot write {additionalBytes} bytes at position {WritePosition} (capacity: {_capacity})");
            }
        }

        /// <summary>
        /// Advances the write position after writing through a span from <see cref="GetWriteSpan"/>.
        /// </summary>
        public void AdvanceWrite(int count)
        {
            _writeBits += count * BitsPerByte;
        }

        /// <summary>
        /// Advances the read position after reading through a span from <see cref="GetReadSpan"/>.
        /// </summary>
        public void AdvanceRead(int count)
        {
            _readBits += count * BitsPerByte;
        }

        /// <summary>
        /// Resets both read and write positions to 0, allowing buffer reuse.
        /// Does NOT clear the buffer contents.
        /// </summary>
        public void Reset()
        {
            _writeBits = 0;
            _readBits = 0;
            _alignMarkCount = 0;
        }

        /// <summary>
        /// Resets write position to 0 and clears the buffer contents.
        /// </summary>
        public void Clear()
        {
            Array.Clear(_buffer, 0, WritePosition);
            _writeBits = 0;
            _readBits = 0;
            _alignMarkCount = 0;
        }

        /// <summary>
        /// Resets only the read position to 0, allowing re-reading of written data.
        /// </summary>
        public void ResetRead()
        {
            _readBits = 0;
        }

        /// <summary>
        /// Copies the written portion of the buffer to a new byte array.
        /// </summary>
        public byte[] ToArray()
        {
            var result = new byte[WritePosition];
            Buffer.BlockCopy(_buffer, 0, result, 0, WritePosition);
            return result;
        }

        /// <summary>
        /// Copies data from another buffer into this one at the current write position (whole
        /// bytes; both sides auto-align).
        /// </summary>
        public void CopyFrom(NetBuffer source)
        {
            AlignWrite();
            var length = source.WritePosition;
            EnsureCapacity(length);
            Buffer.BlockCopy(source._buffer, 0, _buffer, WritePosition, length);
            _writeBits += length * BitsPerByte;
        }

        /// <summary>
        /// Copies data from a span into this buffer at the current write position (auto-aligns).
        /// </summary>
        public void CopyFrom(ReadOnlySpan<byte> source)
        {
            AlignWrite();
            EnsureCapacity(source.Length);
            source.CopyTo(_buffer.AsSpan(WritePosition));
            _writeBits += source.Length * BitsPerByte;
        }

        /// <summary>
        /// Copies data from a byte array into this buffer at the current write position (auto-aligns).
        /// </summary>
        public void CopyFrom(byte[] source, int offset, int count)
        {
            AlignWrite();
            EnsureCapacity(count);
            Buffer.BlockCopy(source, offset, _buffer, WritePosition, count);
            _writeBits += count * BitsPerByte;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_isPooled && _buffer != null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
            }
            _buffer = null;
        }
    }
}
