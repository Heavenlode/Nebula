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
        /// Builds a complete framed packet ready to write to a socket.
        /// </summary>
        public static byte[] Build(byte type, ReadOnlySpan<byte> worldId, ReadOnlySpan<byte> payload)
        {
            if (worldId.Length != WorldIdSize)
                throw new ArgumentException($"worldId must be {WorldIdSize} bytes", nameof(worldId));

            int bodyLength = HeaderSize + payload.Length;
            var frame = new byte[LengthPrefixSize + bodyLength];

            BitConverter.TryWriteBytes(frame.AsSpan(0, LengthPrefixSize), bodyLength);
            frame[LengthPrefixSize] = type;
            worldId.CopyTo(frame.AsSpan(LengthPrefixSize + 1, WorldIdSize));
            payload.CopyTo(frame.AsSpan(PayloadOffset));

            return frame;
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
