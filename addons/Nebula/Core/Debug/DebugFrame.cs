using System;

namespace Nebula
{
    /// <summary>
    /// The Nebula debug-channel wire format, defined in exactly one place and
    /// shared by every participant: the in-process <see cref="DebugHub"/> that
    /// writes frames, the editor debugger view that reads them, and the
    /// integration test harness (Testing/Integration/GodotProcess.cs).
    ///
    /// <code>
    /// frame := [len:int32 LE] [type:uint8] [worldId:16] [payload]
    /// </code>
    ///
    /// <para><c>len</c> counts everything after the prefix (type + worldId +
    /// payload), matching the original framing's "length excludes itself"
    /// convention.</para>
    ///
    /// <para>The type byte deliberately stays at offset 0 of the framed body:
    /// the test harness identifies DEBUG_EVENT frames by that byte, so the
    /// worldId was inserted after it rather than before.</para>
    /// </summary>
    public static class DebugFrame
    {
        public const int LengthPrefixSize = 4;
        public const int WorldIdSize = 16;

        /// <summary>Bytes between the length prefix and the payload: type + worldId.</summary>
        public const int HeaderSize = 1 + WorldIdSize;

        /// <summary>Offset of the payload from the start of a frame.</summary>
        public const int PayloadOffset = LengthPrefixSize + HeaderSize;

        /// <summary>
        /// Sanity bound on a declared frame length. A larger value means the
        /// stream is desynchronized (or hostile), and the reader gives up
        /// rather than allocating on a garbage length.
        /// </summary>
        public const int MaxLength = 8 * 1024 * 1024;

        /// <summary>
        /// Total bytes a frame carrying <paramref name="payloadLength"/> occupies,
        /// prefix included. Use this to size the destination for <see cref="Write"/>.
        /// </summary>
        public static int FrameSize(int payloadLength) => PayloadOffset + payloadLength;

        /// <summary>
        /// Writes a complete framed packet into <paramref name="destination"/> and
        /// returns how many bytes it used.
        ///
        /// <para>Span-based and allocation-free by design: this runs once per debug
        /// frame per tick, so the caller supplies a pooled buffer rather than having
        /// one allocated per frame (see DebugHub's frame pool).</para>
        /// </summary>
        public static int Write(Span<byte> destination, byte type, in UUID worldId, ReadOnlySpan<byte> payload)
        {
            int bodyLength = HeaderSize + payload.Length;
            int frameSize = LengthPrefixSize + bodyLength;
            if (destination.Length < frameSize)
                throw new ArgumentException($"destination needs {frameSize} bytes", nameof(destination));

            BitConverter.TryWriteBytes(destination.Slice(0, LengthPrefixSize), bodyLength);
            destination[LengthPrefixSize] = type;
            if (!worldId.TryWriteBytes(destination.Slice(LengthPrefixSize + 1, WorldIdSize)))
                throw new ArgumentException("failed to write worldId", nameof(worldId));
            payload.CopyTo(destination.Slice(PayloadOffset));

            return frameSize;
        }

        /// <summary>
        /// Attempts to read one frame from <paramref name="data"/> starting at
        /// <paramref name="offset"/>.
        /// </summary>
        /// <param name="consumed">
        /// Total bytes the frame occupies (prefix included). Only meaningful
        /// when this returns true.
        /// </param>
        /// <returns>
        /// False when the buffer holds an incomplete frame — the caller should
        /// keep the remaining bytes and retry after more data arrives.
        /// </returns>
        public static bool TryRead(
            ReadOnlySpan<byte> data,
            int offset,
            out int consumed,
            out byte type,
            out ReadOnlySpan<byte> worldId,
            out ReadOnlySpan<byte> payload)
        {
            consumed = 0;
            type = 0;
            worldId = default;
            payload = default;

            if (offset + PayloadOffset > data.Length)
                return false;

            int bodyLength = BitConverter.ToInt32(data.Slice(offset, LengthPrefixSize));
            if (bodyLength < HeaderSize || bodyLength > MaxLength)
                return false;

            if (offset + LengthPrefixSize + bodyLength > data.Length)
                return false;

            type = data[offset + LengthPrefixSize];
            worldId = data.Slice(offset + LengthPrefixSize + 1, WorldIdSize);
            payload = data.Slice(offset + PayloadOffset, bodyLength - HeaderSize);
            consumed = LengthPrefixSize + bodyLength;
            return true;
        }
    }
}
