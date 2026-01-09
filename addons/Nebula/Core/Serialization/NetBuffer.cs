using System;
using System.Buffers;

namespace Nebula.Serialization
{
    /// <summary>
    /// High-performance network buffer using ArrayPool for zero-allocation serialization.
    /// Replaces the old HLBuffer that extended Godot's RefCounted.
    /// </summary>
    public sealed class NetBuffer : IDisposable
    {
        /// <summary>
        /// Default capacity based on network MTU (1500) plus some headroom for headers.
        /// </summary>
        public const int DefaultCapacity = 1536;

        private byte[] _buffer;
        private readonly int _capacity;
        private bool _disposed;
        private bool _isPooled;

        /// <summary>
        /// Current write position in the buffer.
        /// </summary>
        public int WritePosition { get; set; }

        /// <summary>
        /// Current read position in the buffer.
        /// </summary>
        public int ReadPosition { get; set; }

        /// <summary>
        /// Number of bytes written to the buffer.
        /// </summary>
        public int Length => WritePosition;

        /// <summary>
        /// Total capacity of the buffer.
        /// </summary>
        public int Capacity => _capacity;

        /// <summary>
        /// Whether all data has been read (read position >= write position).
        /// </summary>
        public bool IsReadComplete => ReadPosition >= WritePosition;

        /// <summary>
        /// Number of bytes remaining to be read.
        /// </summary>
        public int Remaining => WritePosition - ReadPosition;

        /// <summary>
        /// Gets a span over the written portion of the buffer.
        /// </summary>
        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, WritePosition);

        /// <summary>
        /// Gets a span over the unread portion of the buffer.
        /// </summary>
        public ReadOnlySpan<byte> UnreadSpan => _buffer.AsSpan(ReadPosition, WritePosition - ReadPosition);

        /// <summary>
        /// Gets a span for writing at the current write position.
        /// </summary>
        public Span<byte> GetWriteSpan(int length)
        {
            EnsureCapacity(length);
            return _buffer.AsSpan(WritePosition, length);
        }

        /// <summary>
        /// Gets a span for reading at the current read position.
        /// </summary>
        public ReadOnlySpan<byte> GetReadSpan(int length)
        {
            if (ReadPosition + length > WritePosition)
                throw new InvalidOperationException($"Cannot read {length} bytes, only {Remaining} remaining");
            return _buffer.AsSpan(ReadPosition, length);
        }

        /// <summary>
        /// Direct access to the underlying buffer. Use with caution.
        /// </summary>
        public byte[] RawBuffer => _buffer;

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
            WritePosition = 0;
            ReadPosition = 0;
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
            WritePosition = data.Length;
            ReadPosition = 0;
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
            WritePosition = data.Length;
            ReadPosition = 0;
        }

        /// <summary>
        /// Ensures the buffer has enough capacity for the specified additional bytes.
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
        /// Advances the write position after writing.
        /// </summary>
        public void AdvanceWrite(int count)
        {
            WritePosition += count;
        }

        /// <summary>
        /// Advances the read position after reading.
        /// </summary>
        public void AdvanceRead(int count)
        {
            ReadPosition += count;
        }

        /// <summary>
        /// Resets both read and write positions to 0, allowing buffer reuse.
        /// Does NOT clear the buffer contents.
        /// </summary>
        public void Reset()
        {
            WritePosition = 0;
            ReadPosition = 0;
        }

        /// <summary>
        /// Resets write position to 0 and clears the buffer contents.
        /// </summary>
        public void Clear()
        {
            Array.Clear(_buffer, 0, WritePosition);
            WritePosition = 0;
            ReadPosition = 0;
        }

        /// <summary>
        /// Resets only the read position to 0, allowing re-reading of written data.
        /// </summary>
        public void ResetRead()
        {
            ReadPosition = 0;
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
        /// Copies data from another buffer into this one at the current write position.
        /// </summary>
        public void CopyFrom(NetBuffer source)
        {
            var length = source.WritePosition;
            EnsureCapacity(length);
            Buffer.BlockCopy(source._buffer, 0, _buffer, WritePosition, length);
            WritePosition += length;
        }

        /// <summary>
        /// Copies data from a span into this buffer at the current write position.
        /// </summary>
        public void CopyFrom(ReadOnlySpan<byte> source)
        {
            EnsureCapacity(source.Length);
            source.CopyTo(_buffer.AsSpan(WritePosition));
            WritePosition += source.Length;
        }

        /// <summary>
        /// Copies data from a byte array into this buffer at the current write position.
        /// </summary>
        public void CopyFrom(byte[] source, int offset, int count)
        {
            EnsureCapacity(count);
            Buffer.BlockCopy(source, offset, _buffer, WritePosition, count);
            WritePosition += count;
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
