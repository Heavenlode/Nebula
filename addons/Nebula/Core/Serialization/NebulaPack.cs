using System;

namespace Nebula.Serialization
{
    // ============================================================================================
    //  NebulaPack — tick payload compression
    // ============================================================================================
    //
    //  THE PROBLEM
    //
    //  Most of a tick's payload prefix is byte-for-byte identical to the previous one. Node
    //  bitmasks, serializer masks, property masks and encoding-flag bytes almost never change. In a
    //  real capture, 42 of 69 bytes were the same in every single packet.
    //
    //
    //  THE IDEA
    //
    //  Don't send the payload. Send only the bytes that differ from a payload the client already
    //  has, walking both byte-by-byte at the same position.
    //
    //      client already has:   A B C D E F G H
    //      we want to send:      A B C X Y F G H
    //
    //      what we actually send:   skip 3, then 2 literal bytes (X Y), then skip 3
    //
    //  That is three small numbers and two bytes, instead of eight bytes. We call each
    //  "skip N, then M literal bytes" pair a run, and a payload is just a list of runs.
    //
    //  This is NOT a general compressor. It only compares position 0 to position 0, 1 to 1, and so
    //  on. That sounds too simple to work, but tick payloads are naturally aligned, and on real
    //  data it beat deflate-with-a-shared-dictionary while being far cheaper and far less code.
    //
    //
    //  WHICH PREVIOUS PAYLOAD?
    //
    //  We can only delta against a payload the client definitely has. The client acks every tick it
    //  applies, so the server keeps the last few payloads it sent each peer and marks each one as
    //  the ack for it arrives. Only a marked payload can be used.
    //
    //  Note this is per-tick, not "everything up to tick N". Acks travel over the same lossy channel
    //  as the ticks, so an ack for tick 15 tells us nothing about whether tick 13 arrived. Assuming
    //  it did would mean deltaing against bytes the client never received.
    //
    //  The packet says which one, as an age: "this is a delta against the payload from 2 ticks
    //  ago". The client keeps the same last-few-payloads ring, so it looks up tick-minus-2 and
    //  decodes against that.
    //
    //  The server tries every acknowledged payload in range and picks whichever produces the
    //  smallest result. Usually that is the immediately previous tick, but not always: when a node
    //  spawns or despawns the payload shifts and everything misaligns, and a payload from a few
    //  ticks earlier can match far better. Trying them all is cheap because measuring is just the
    //  same byte walk without writing anything.
    //
    //
    //  WHAT IF A PACKET IS LOST?
    //
    //  Nothing breaks. A lost tick simply never gets acked, so it never becomes a baseline
    //  candidate. Losing packets means the server has fewer baselines to choose from — never a
    //  wrong one.
    //
    //  If the client somehow can't decode a packet, it refuses it: doesn't apply it, doesn't ack
    //  it. That tick never becomes a baseline either, and once every acked payload has aged out of
    //  range the server goes back to sending raw by itself. No renegotiation, no reset message.
    //  It just heals.
    //
    //  Sending raw is always allowed and always safe. Every packet says which it is.
    //
    //
    //  WIRE FORMAT
    //
    //      [flags: 1 byte]
    //      [baselineAge: 1 byte]   only when the delta flag is set; always >= 1
    //      [body]                  the runs, or the raw payload
    //      [checksum: 2 bytes]     only when the checksum flag is set
    //
    //      flags bit 0 (0x01)  body is a delta
    //      flags bit 1 (0x02)  a checksum follows the body
    //
    //  The body of a delta is:
    //
    //      [rawLen varint][skip varint][literalCount varint][literal bytes]  [skip][count][bytes] ...
    //
    //  rawLen comes first so the decoder knows how big the result is before writing anything, and
    //  can reject a packet claiming more than fits.
    //
    //  The checksum is of the RAW payload, so it verifies that the client decoded to exactly what
    //  the server started with. It catches the two windows disagreeing, which is the one failure
    //  that would otherwise be silent.
    //
    //
    //  WHERE THINGS LIVE
    //
    //      WritePacket   server side. Picks a baseline, writes the whole packet body.
    //      ReadPacket    client side. Validates, decodes, checksums.
    //      NebulaPackWindow   the ring of recent payloads, one per peer on the server,
    //                         one on the client.
    //
    //  The server calls WritePacket, then records the payload it sent. The client calls ReadPacket,
    //  and records the payload only if it applied AND acked it — so the client's ring is never
    //  missing something the server thinks it has.
    //
    // ============================================================================================

    /// <summary>Why <see cref="NebulaPack.ReadPacket"/> rejected a packet.</summary>
    public enum PackResult
    {
        Ok,
        /// <summary>Packet was empty or ended before the header was complete.</summary>
        Truncated,
        /// <summary>Flag bits set that this build doesn't understand.</summary>
        UnknownFlags,
        /// <summary>Delta named a baseline tick the client no longer has.</summary>
        BaselineMissing,
        /// <summary>The run list was malformed.</summary>
        MalformedDelta,
        /// <summary>Decoded payload wouldn't fit the destination buffer.</summary>
        TooLarge,
        /// <summary>Decoded cleanly but to different bytes than the server encoded.</summary>
        ChecksumMismatch,
    }

    public static class NebulaPack
    {
        /// <summary>Body is a delta, not a raw payload.</summary>
        public const byte FlagDelta = 0x01;

        /// <summary>A 2-byte checksum of the raw payload follows the body.</summary>
        public const byte FlagChecksum = 0x02;

        /// <summary>Flags this build understands. Anything else is rejected.</summary>
        public const byte FlagMask = FlagDelta | FlagChecksum;

        // ---------------------------------------------------------------- server

        /// <summary>
        /// Writes a complete tick body into <paramref name="destination"/>: flags, then either a
        /// delta or the raw payload, then an optional checksum.
        ///
        /// Falls back to raw when compression is off, when no acknowledged baseline is in range, or
        /// when no baseline actually beats sending raw.
        /// </summary>
        /// <param name="window">The payloads previously sent to this peer.</param>
        /// <param name="currentTick">The tick being sent.</param>
        /// <returns>The baseline age used, or 0 if the payload was sent raw.</returns>
        public static int WritePacket(
            NetBuffer destination,
            ReadOnlySpan<byte> payload,
            NebulaPackWindow window,
            Tick currentTick,
            bool allowDelta,
            bool addChecksum)
        {
            int age = allowDelta
                ? PickBaseline(payload, window, currentTick, out int deltaSize)
                : 0;

            byte flags = 0;
            if (age > 0) flags |= FlagDelta;
            if (addChecksum) flags |= FlagChecksum;

            int rewindTo = destination.WritePosition;
            NetWriter.WriteByte(destination, flags);

            bool wroteDelta = false;
            if (age > 0 && window.TryGetAcked(currentTick - age, out var baseline))
            {
                NetWriter.WriteByte(destination, (byte)age);
                int written = EncodeDelta(payload, baseline, destination.GetWriteSpan(payload.Length));
                if (written > 0)
                {
                    destination.AdvanceWrite(written);
                    wroteDelta = true;
                }
                else
                {
                    // Didn't fit after all. Start the body over as a raw payload.
                    destination.WritePosition = rewindTo;
                    NetWriter.WriteByte(destination, (byte)(flags & ~FlagDelta));
                }
            }

            if (!wroteDelta)
            {
                age = 0;
                NetWriter.WriteBytes(destination, payload);
            }

            if (addChecksum) NetWriter.WriteUInt16(destination, Checksum(payload));

            return age;
        }

        /// <summary>
        /// Finds the acknowledged baseline that compresses <paramref name="payload"/> smallest.
        /// Returns its age in ticks, or 0 if none beats sending raw.
        ///
        /// Measuring doesn't write anything, so trying every candidate costs only a few byte
        /// comparisons each.
        /// </summary>
        private static int PickBaseline(
            ReadOnlySpan<byte> payload,
            NebulaPackWindow window,
            Tick currentTick,
            out int bestSize)
        {
            bestSize = -1;
            int bestAge = 0;
            if (window == null) return 0;

            for (int age = 1; age <= NebulaPackWindow.MaxPackAge; age++)
            {
                Tick candidate = currentTick - age;
                if (candidate < 0) break;
                // TryGetAcked, not TryGet: only a tick this peer specifically acknowledged.
                if (!window.TryGetAcked(candidate, out var baseline)) continue;

                int size = MeasureDelta(payload, baseline);
                if (size < payload.Length && (bestSize < 0 || size < bestSize))
                {
                    bestSize = size;
                    bestAge = age;
                }
            }

            return bestAge;
        }

        // ---------------------------------------------------------------- client

        /// <summary>
        /// Reads a tick body written by <see cref="WritePacket"/> and leaves the raw payload in
        /// <paramref name="destination"/>, ready to import.
        ///
        /// Everything here runs on untrusted bytes, so every length is checked before use and this
        /// never throws. On any failure the caller must not apply or acknowledge the tick — that's
        /// what makes the server fall back to raw on its own.
        /// </summary>
        /// <param name="window">Payloads this client has already applied and acked.</param>
        /// <param name="tick">The tick this packet is for.</param>
        public static PackResult ReadPacket(
            ReadOnlySpan<byte> wire,
            Tick tick,
            NebulaPackWindow window,
            NetBuffer destination)
        {
            if (wire.Length < 1) return PackResult.Truncated;

            byte flags = wire[0];
            if ((flags & ~FlagMask) != 0) return PackResult.UnknownFlags;

            bool isDelta = (flags & FlagDelta) != 0;
            bool hasChecksum = (flags & FlagChecksum) != 0;

            int offset = 1;
            int age = 0;
            if (isDelta)
            {
                if (wire.Length < offset + 1) return PackResult.Truncated;
                age = wire[offset++];
                if (age < 1) return PackResult.MalformedDelta;    // 0 would mean "delta against myself"
            }

            int bodyEnd = wire.Length - (hasChecksum ? 2 : 0);
            if (bodyEnd < offset) return PackResult.Truncated;
            var body = wire.Slice(offset, bodyEnd - offset);

            destination.Reset();
            var output = destination.GetWriteSpan(destination.Capacity);

            int produced;
            if (isDelta)
            {
                if (window == null || !window.TryGet(tick - age, out var baseline))
                    return PackResult.BaselineMissing;

                produced = DecodeDelta(body, baseline, output);
                if (produced < 0) return PackResult.MalformedDelta;
            }
            else
            {
                if (body.Length > output.Length) return PackResult.TooLarge;
                body.CopyTo(output);
                produced = body.Length;
            }

            if (hasChecksum)
            {
                ushort expected = (ushort)(wire[bodyEnd] | (wire[bodyEnd + 1] << 8));
                if (expected != Checksum(output.Slice(0, produced)))
                    return PackResult.ChecksumMismatch;
            }

            destination.AdvanceWrite(produced);
            destination.ResetRead();
            return PackResult.Ok;
        }

        // ---------------------------------------------------------------- codec

        /// <summary>
        /// How many bytes <see cref="EncodeDelta"/> would produce, without producing them.
        /// </summary>
        public static int MeasureDelta(ReadOnlySpan<byte> input, ReadOnlySpan<byte> window)
            => Encode(input, window, default, measureOnly: true);

        /// <summary>
        /// Writes the run list. Returns bytes written, or -1 if <paramref name="output"/> is too
        /// small. Note a delta can legitimately be larger than the input when nothing matches —
        /// deciding whether it is worth sending is the caller's job.
        /// </summary>
        public static int EncodeDelta(ReadOnlySpan<byte> input, ReadOnlySpan<byte> window, Span<byte> output)
            => Encode(input, window, output, measureOnly: false);

        /// <summary>
        /// Rebuilds the payload from a run list. Returns bytes written, or -1 if the input is
        /// malformed in any way. Never throws, never reads or writes out of bounds.
        /// </summary>
        public static int DecodeDelta(ReadOnlySpan<byte> input, ReadOnlySpan<byte> window, Span<byte> output)
        {
            int readPos = 0;
            if (!TryReadVarint(input, ref readPos, out uint rawLen)) return -1;
            if (rawLen > (uint)output.Length) return -1;

            int writePos = 0;
            while (writePos < (int)rawLen)
            {
                if (!TryReadVarint(input, ref readPos, out uint skip)) return -1;
                if (!TryReadVarint(input, ref readPos, out uint literals)) return -1;

                // A run has to consume something, or a malformed packet would loop forever.
                if (skip == 0 && literals == 0) return -1;

                uint remaining = rawLen - (uint)writePos;

                if (skip > 0)
                {
                    // Skipped bytes are copied from the same position in the baseline.
                    if (skip > remaining) return -1;
                    if ((uint)writePos + skip > (uint)window.Length) return -1;
                    window.Slice(writePos, (int)skip).CopyTo(output.Slice(writePos, (int)skip));
                    writePos += (int)skip;
                    remaining -= skip;
                }

                if (literals > 0)
                {
                    if (literals > remaining) return -1;
                    if ((uint)readPos + literals > (uint)input.Length) return -1;
                    input.Slice(readPos, (int)literals).CopyTo(output.Slice(writePos, (int)literals));
                    readPos += (int)literals;
                    writePos += (int)literals;
                }
            }

            return (int)rawLen;
        }

        /// <summary>FNV-1a 64 folded down to 16 bits.</summary>
        public static ushort Checksum(ReadOnlySpan<byte> data)
        {
            ulong h = 1469598103934665603UL;   // FNV offset basis
            for (int i = 0; i < data.Length; i++)
            {
                h ^= data[i];
                h *= 1099511628211UL;          // FNV prime
            }
            h ^= h >> 32;
            h ^= h >> 16;
            return (ushort)h;
        }

        /// <summary>
        /// The byte walk, shared by measuring and encoding so the two can never disagree.
        ///
        /// Each loop consumes at least one input byte: if the skip run is empty then the bytes
        /// differ, which means the literal run that follows is not empty. So it always terminates.
        /// </summary>
        private static int Encode(ReadOnlySpan<byte> input, ReadOnlySpan<byte> window, Span<byte> output, bool measureOnly)
        {
            int inputLen = input.Length;
            int windowLen = window.Length;

            int size = VarintSize((uint)inputLen);
            int pos = 0;
            if (!measureOnly && !TryWriteVarint(output, ref pos, (uint)inputLen)) return -1;

            int i = 0;
            while (i < inputLen)
            {
                // How many bytes match the baseline from here? CommonPrefixLength is SIMD-accelerated.
                int skipStart = i;
                if (i < windowLen)
                {
                    int compareLen = Math.Min(inputLen - i, windowLen - i);
                    i += input.Slice(i, compareLen).CommonPrefixLength(window.Slice(i, compareLen));
                }
                int skip = i - skipStart;

                // How many differ before they line up again? Anything past the end of the baseline
                // counts as differing, so a payload longer than its baseline needs no special case.
                int literalStart = i;
                while (i < inputLen && (i >= windowLen || input[i] != window[i])) i++;
                int literals = i - literalStart;

                size += VarintSize((uint)skip) + VarintSize((uint)literals) + literals;

                if (!measureOnly)
                {
                    if (!TryWriteVarint(output, ref pos, (uint)skip)) return -1;
                    if (!TryWriteVarint(output, ref pos, (uint)literals)) return -1;
                    if (pos + literals > output.Length) return -1;
                    input.Slice(literalStart, literals).CopyTo(output.Slice(pos, literals));
                    pos += literals;
                }
            }

            return measureOnly ? size : pos;
        }

        // ---------------------------------------------------------------- varints

        private static int VarintSize(uint v)
            => v < 0x80u ? 1 : v < 0x4000u ? 2 : v < 0x200000u ? 3 : v < 0x10000000u ? 4 : 5;

        private static bool TryWriteVarint(Span<byte> dst, ref int pos, uint value)
        {
            while (true)
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) b |= 0x80;
                if ((uint)pos >= (uint)dst.Length) return false;
                dst[pos++] = b;
                if (value == 0) return true;
            }
        }

        private static bool TryReadVarint(ReadOnlySpan<byte> src, ref int pos, out uint value)
        {
            value = 0;
            int shift = 0;
            // A uint32 varint is never more than 5 bytes; refusing longer keeps garbage bounded.
            for (int k = 0; k < 5; k++)
            {
                if ((uint)pos >= (uint)src.Length) { value = 0; return false; }
                byte b = src[pos++];
                value |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
            }
            value = 0;
            return false;
        }
    }

    /// <summary>
    /// The last few tick payloads, kept so they can be used as delta baselines. The server keeps
    /// one of these per peer (what it sent); the client keeps one (what it applied and acked).
    ///
    /// Slots are indexed by <c>tick % RingSize</c> and each remembers its own tick, so an old slot
    /// that hasn't been overwritten yet is never mistaken for a current one.
    /// </summary>
    public sealed class NebulaPackWindow
    {
        /// <summary>How many payloads are kept. Must be larger than <see cref="MaxPackAge"/>.</summary>
        public const int RingSize = 32;

        /// <summary>
        /// Oldest baseline that may be used, in ticks. Anything older may already have been
        /// overwritten, which is why this has to stay below <see cref="RingSize"/>.
        ///
        /// This is what decides which players get compression at all. A baseline can only be used
        /// once the peer has acked it, so the server is always at least one round trip behind — if
        /// that round trip is longer than MaxPackAge ticks, nothing is ever in range and every
        /// packet goes out raw.
        ///
        /// At 30 TPS a tick is ~33ms, so 30 covers about a second of round trip. Sized off real
        /// latency rather than loopback: at MaxPackAge = 6 compression switched off entirely above
        /// ~200ms ping, silently, which is an ordinary connection for a remote player.
        ///
        /// Cost is RingSize payloads per peer per side — roughly 3 KB each at current payload sizes.
        /// </summary>
        public const int MaxPackAge = 30;

        static NebulaPackWindow()
        {
            if (MaxPackAge >= RingSize)
            {
                throw new InvalidOperationException(
                    $"NebulaPackWindow.MaxPackAge ({MaxPackAge}) must be less than RingSize ({RingSize}), " +
                    "otherwise a usable baseline can be overwritten before it is used.");
            }
        }

        private readonly byte[][] _slots = new byte[RingSize][];
        private readonly int[] _lengths = new int[RingSize];
        private readonly Tick[] _ticks = new Tick[RingSize];
        private readonly bool[] _acked = new bool[RingSize];

        public NebulaPackWindow() => Reset();

        /// <summary>
        /// Stores a copy of the payload. It has to be a copy — the caller's buffer is pooled and
        /// reused next tick.
        /// </summary>
        public void Record(Tick tick, ReadOnlySpan<byte> payload)
        {
            if (tick < 0) return;   // the client uses -1 while switching worlds

            int idx = tick % RingSize;
            var slot = _slots[idx];
            if (slot == null || slot.Length < payload.Length)
            {
                _slots[idx] = slot = new byte[Math.Max(payload.Length, 128)];
            }
            payload.CopyTo(slot);
            _lengths[idx] = payload.Length;
            _ticks[idx] = tick;
            _acked[idx] = false;
        }

        /// <summary>
        /// Server side. Marks one specific tick as confirmed received by the peer.
        ///
        /// This has to be per-tick. "Everything up to tick N" would be wrong: the client acks each
        /// tick it applies, and those acks travel over the same lossy channel as everything else, so
        /// a newer ack arriving says nothing about whether older ticks got through. Deltaing against
        /// a tick the client never received produces a packet it can only throw away.
        /// </summary>
        public void MarkAcked(Tick tick)
        {
            if (tick < 0) return;
            int idx = tick % RingSize;
            if (_ticks[idx] == tick) _acked[idx] = true;
        }

        /// <summary>
        /// Server side. Like <see cref="TryGet"/>, but only returns payloads the peer has actually
        /// acknowledged — the only ones that are legal to use as a delta baseline.
        /// </summary>
        public bool TryGetAcked(Tick tick, out ReadOnlySpan<byte> payload)
        {
            payload = default;
            if (tick < 0) return false;
            if (!_acked[tick % RingSize]) return false;
            return TryGet(tick, out payload);
        }

        /// <summary>Gets the payload for a tick, if that exact tick still occupies its slot.</summary>
        public bool TryGet(Tick tick, out ReadOnlySpan<byte> payload)
        {
            payload = default;
            if (tick < 0) return false;

            int idx = tick % RingSize;
            if (_ticks[idx] != tick) return false;

            var slot = _slots[idx];
            if (slot == null) return false;

            payload = slot.AsSpan(0, _lengths[idx]);
            return true;
        }

        /// <summary>
        /// Forgets everything. Required when a client switches worlds: node ids are per-world, so an
        /// old payload would decode into completely the wrong nodes.
        /// </summary>
        public void Reset()
        {
            for (int i = 0; i < RingSize; i++)
            {
                _ticks[i] = -1;
                _lengths[i] = 0;
                _acked[i] = false;
            }
        }
    }
}
