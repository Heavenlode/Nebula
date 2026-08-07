using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        /// <summary>
        /// Bytes per block digest. 32 gives ~28 digests for a typical payload — fine enough that a
        /// differing-block count tracks literal volume closely, coarse enough that ranking is a few
        /// dozen word compares.
        /// </summary>
        public const int DigestBlockBytes = 32;

        /// <summary>
        /// How many of the digest-ranked candidates get an exact measurement. Ranking is a proxy, so
        /// measuring only the single best would inherit every ranking error; measuring the top few
        /// recovers most of that for one extra scan each, and the exact sizes still decide the
        /// winner.
        /// </summary>
        private const int TopCandidatesToMeasure = 3;

        /// <summary>
        /// Digest slots kept on the stack for the outgoing payload. Sized past the MTU-capped
        /// payload so the heap path is unreachable in practice but still correct if a project
        /// raises the MTU.
        /// </summary>
        private const int MaxStackDigests = 128;

        /// <summary>Digest blocks a payload of this length occupies.</summary>
        public static int BlockCount(int length)
            => (length + DigestBlockBytes - 1) / DigestBlockBytes;

        /// <summary>
        /// Fills <paramref name="into"/> with one digest per block of <paramref name="payload"/>.
        ///
        /// <para>Multiply-and-rotate rather than an XOR fold: XOR cancels, so two different blocks
        /// that happen to contain the same bytes in a different order would collide constantly, and
        /// ranking would treat unrelated baselines as identical. This is not a hash anyone attacks —
        /// it only has to separate blocks that differ, and a collision costs a slightly worse
        /// baseline, never a wrong delta.</para>
        /// </summary>
        public static void ComputeDigests(ReadOnlySpan<byte> payload, Span<ulong> into)
        {
            const ulong Basis = 0xcbf29ce484222325UL;
            const ulong Prime = 0x100000001b3UL;

            int blocks = BlockCount(payload.Length);
            for (int b = 0; b < blocks; b++)
            {
                int start = b * DigestBlockBytes;
                int length = Math.Min(DigestBlockBytes, payload.Length - start);
                var block = payload.Slice(start, length);

                ulong h = Basis;
                int offset = 0;
                while (offset + sizeof(ulong) <= length)
                {
                    ulong word = MemoryMarshal.Read<ulong>(block.Slice(offset, sizeof(ulong)));
                    h = System.Numerics.BitOperations.RotateLeft((h ^ word) * Prime, 31);
                    offset += sizeof(ulong);
                }
                // Tail of a short final block, folded in a byte at a time.
                for (; offset < length; offset++)
                {
                    h = System.Numerics.BitOperations.RotateLeft((h ^ block[offset]) * Prime, 31);
                }
                into[b] = h;
            }
        }

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
            var profiler = Diagnostics.TickProfiler.Current;
            var pickTs = Diagnostics.TickProfiler.Now();
            int age = allowDelta
                ? PickBaseline(payload, window, currentTick, out int deltaSize)
                : 0;
            profiler?.Record(Diagnostics.TickProfiler.Phase.PackPick, pickTs);

            byte flags = 0;
            if (age > 0) flags |= FlagDelta;
            if (addChecksum) flags |= FlagChecksum;

            int rewindTo = destination.WritePosition;
            NetWriter.WriteByte(destination, flags);

            bool wroteDelta = false;
            if (age > 0 && window.TryGetAcked(currentTick - age, out var baseline))
            {
                NetWriter.WriteByte(destination, (byte)age);
                var encodeTs = Diagnostics.TickProfiler.Now();
                int written = EncodeDelta(payload, baseline, destination.GetWriteSpan(payload.Length));
                profiler?.Record(Diagnostics.TickProfiler.Phase.PackEncode, encodeTs);
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
                var rawTs = Diagnostics.TickProfiler.Now();
                NetWriter.WriteBytes(destination, payload);
                profiler?.Record(Diagnostics.TickProfiler.Phase.PackRaw, rawTs);
            }

            if (addChecksum)
            {
                var checksumTs = Diagnostics.TickProfiler.Now();
                NetWriter.WriteUInt16(destination, Checksum(payload));
                profiler?.Record(Diagnostics.TickProfiler.Phase.PackChecksum, checksumTs);
            }

            return age;
        }

        /// <summary>
        /// Finds the acknowledged baseline that compresses <paramref name="payload"/> smallest.
        /// Returns its age in ticks, or 0 if none beats sending raw.
        ///
        /// <para>Two-stage, because measuring every candidate exactly was the single most expensive
        /// thing the server did: ~29 acked baselines each cost a full encode pass over a ~870-byte
        /// payload, which measured 26% of the entire world tick.</para>
        ///
        /// <para><b>Rank</b> every candidate by how many 32-byte blocks differ, comparing digests
        /// the ring computed once when each payload was stored. Differing-block count tracks literal
        /// volume closely enough to order candidates, and costs ~28 word compares instead of ~870
        /// byte comparisons. <b>Then measure</b> only the few best exactly, each bounded by the best
        /// size found so far so a candidate that cannot win stops partway.</para>
        ///
        /// <para>Ranking is a heuristic and may pass over the true optimum, so this can pick a
        /// slightly larger delta than an exhaustive search would. It cannot pick a WRONG one: the
        /// winner is always measured exactly before anything is encoded, and any acked baseline is
        /// legal on the wire regardless.</para>
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

            var profiler = Diagnostics.TickProfiler.Current;

            // ---- stage 1: rank by differing blocks -------------------------------------------
            int blocks = BlockCount(payload.Length);
            Span<ulong> payloadDigests = blocks <= MaxStackDigests
                ? stackalloc ulong[MaxStackDigests].Slice(0, blocks)
                : new ulong[blocks];
            ComputeDigests(payload, payloadDigests);

            // Top-K by differing-block count, kept as a tiny insertion-sorted list. K is 3, so a
            // heap would be more code than it saves.
            Span<int> topAges = stackalloc int[TopCandidatesToMeasure];
            Span<int> topScores = stackalloc int[TopCandidatesToMeasure];
            int topCount = 0;

            for (int age = 1; age <= NebulaPackWindow.MaxPackAge; age++)
            {
                Tick candidate = currentTick - age;
                if (candidate < 0) break;
                // TryGetAcked, not TryGet: only a tick this peer specifically acknowledged.
                if (!window.TryGetAckedDigests(candidate, out var baselineDigests)) continue;

                if (profiler != null) profiler.Add(Diagnostics.TickProfiler.Counter.PackCandidates, 1);

                // Blocks the baseline does not reach count as differing, which is what a delta would
                // have to spell out literally anyway.
                int shared = Math.Min(blocks, baselineDigests.Length);
                int differing = blocks - shared;
                for (int b = 0; b < shared; b++)
                {
                    if (payloadDigests[b] != baselineDigests[b]) differing++;
                }

                for (int slot = 0; slot < TopCandidatesToMeasure; slot++)
                {
                    if (slot < topCount && differing >= topScores[slot]) continue;
                    for (int shift = Math.Min(topCount, TopCandidatesToMeasure - 1); shift > slot; shift--)
                    {
                        topAges[shift] = topAges[shift - 1];
                        topScores[shift] = topScores[shift - 1];
                    }
                    topAges[slot] = age;
                    topScores[slot] = differing;
                    if (topCount < TopCandidatesToMeasure) topCount++;
                    break;
                }
            }

            // ---- stage 2: measure the finalists exactly --------------------------------------
            // Bound starts at the raw length, since a delta at or above that is unusable anyway,
            // and tightens with every candidate that beats it.
            int bound = payload.Length;
            for (int i = 0; i < topCount; i++)
            {
                if (!window.TryGetAcked(currentTick - topAges[i], out var baseline)) continue;

                if (profiler != null)
                {
                    profiler.Add(Diagnostics.TickProfiler.Counter.PackMeasured, 1);
                    profiler.Add(Diagnostics.TickProfiler.Counter.PackBytesMeasured, payload.Length);
                }

                int size = MeasureDeltaBounded(payload, baseline, bound);
                if (size < bound)
                {
                    bound = size;
                    bestSize = size;
                    bestAge = topAges[i];
                }
            }

            if (profiler != null && bestAge > 0)
            {
                profiler.Add(Diagnostics.TickProfiler.Counter.PackDeltasChosen, 1);
                profiler.Add(Diagnostics.TickProfiler.Counter.PackChosenAgeSum, bestAge);
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
        /// <see cref="MeasureDelta"/> that gives up as soon as the delta is provably no better than
        /// <paramref name="abortAtOrAbove"/>, returning that bound rather than the true size.
        ///
        /// <para>Exact, not approximate: the running size only ever grows, so once it reaches the
        /// bound the finished size cannot come in under it. Feeding the best size found so far as
        /// the bound therefore preserves the same global minimum an unbounded search would find,
        /// while letting hopeless candidates stop partway.</para>
        /// </summary>
        public static int MeasureDeltaBounded(ReadOnlySpan<byte> input, ReadOnlySpan<byte> window, int abortAtOrAbove)
            => Encode(input, window, default, measureOnly: true, abortAtOrAbove);

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
        private static int Encode(
            ReadOnlySpan<byte> input, ReadOnlySpan<byte> window, Span<byte> output, bool measureOnly,
            int abortAtOrAbove = int.MaxValue)
        {
            int inputLen = input.Length;
            int windowLen = window.Length;

            int size = VarintSize((uint)inputLen);
            int pos = 0;
            if (!measureOnly && !TryWriteVarint(output, ref pos, (uint)inputLen)) return -1;

            // Past either end there is nothing to compare, so word stepping stops here and the
            // remainder is literal by definition.
            int compareLimit = Math.Min(inputLen, windowLen);
            ref byte inputRef = ref MemoryMarshal.GetReference(input);
            ref byte windowRef = ref MemoryMarshal.GetReference(window);

            int i = 0;
            while (i < inputLen)
            {
                // How many bytes match the baseline from here? Compared a machine word at a time:
                // XOR is zero exactly while the eight bytes agree, and when it isn't, the index of
                // the first differing byte falls straight out of the trailing-zero count.
                //
                // This used to call CommonPrefixLength, whose vectorization is real but is charged
                // a fixed setup cost. Measured against live traffic the matching runs average under
                // two bytes, so that setup was the entire cost and the SIMD never had length to
                // work with.
                int skipStart = i;
                while (i + sizeof(ulong) <= compareLimit)
                {
                    ulong difference = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inputRef, i))
                                     ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref windowRef, i));
                    if (difference != 0)
                    {
                        i += FirstDifferingByte(difference);
                        break;
                    }
                    i += sizeof(ulong);
                }
                while (i < compareLimit && Unsafe.Add(ref inputRef, i) == Unsafe.Add(ref windowRef, i)) i++;
                int skip = i - skipStart;

                // How many differ before they line up again? Anything past the end of the baseline
                // counts as differing, so a payload longer than its baseline needs no special case.
                //
                // Same word trick inverted: step eight bytes whenever none of them match, which is
                // the common case here (differing runs average ~15 bytes). HasZeroByte can report a
                // false positive, never a false negative, so a hit only drops us to the byte loop
                // rather than ever ending a run early.
                int literalStart = i;
                while (true)
                {
                    if (i + sizeof(ulong) <= compareLimit)
                    {
                        ulong difference = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inputRef, i))
                                         ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref windowRef, i));
                        if (!HasZeroByte(difference)) { i += sizeof(ulong); continue; }
                    }
                    if (i < inputLen && (i >= windowLen || Unsafe.Add(ref inputRef, i) != Unsafe.Add(ref windowRef, i)))
                    {
                        i++;
                        continue;
                    }
                    break;
                }
                int literals = i - literalStart;

                size += VarintSize((uint)skip) + VarintSize((uint)literals) + literals;

                // Hopeless already. size only grows, so the finished delta cannot come in under the
                // bound and the remaining bytes would be scanned for nothing. Measuring only --
                // a real encode has to write every run.
                if (measureOnly && size >= abortAtOrAbove) return abortAtOrAbove;

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

        /// <summary>
        /// Index of the first byte (in memory order) set in <paramref name="difference"/>, which for
        /// an XOR of two words is the first byte at which they disagree. Exact, not a heuristic:
        /// XOR has no carries between bytes.
        ///
        /// <para>Memory order is what matters, so the bit scan direction follows endianness — on a
        /// little-endian machine the first byte is the least significant. The check folds away at
        /// JIT time.</para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FirstDifferingByte(ulong difference)
            // Fully qualified: Nebula has its own internal BitOperations (NetArray.cs) which would
            // otherwise win name resolution here and does not carry these members.
            => BitConverter.IsLittleEndian
                ? System.Numerics.BitOperations.TrailingZeroCount(difference) >> 3
                : System.Numerics.BitOperations.LeadingZeroCount(difference) >> 3;

        /// <summary>
        /// True if any byte of <paramref name="value"/> is zero — i.e. for an XOR, whether the two
        /// words agree anywhere.
        ///
        /// <para>The subtraction borrows across byte boundaries, so this can answer true when the
        /// only zero byte is one the borrow manufactured. That is why callers treat a hit as "look
        /// closer" rather than "a match starts here": it has no false negatives, so a run is never
        /// ended early, and a false positive costs at most eight byte comparisons.</para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasZeroByte(ulong value)
            => ((value - 0x0101010101010101UL) & ~value & 0x8080808080808080UL) != 0;

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

        /// <summary>
        /// One 64-bit digest per fixed-size block of each stored payload, computed when the payload
        /// is recorded.
        ///
        /// <para>This is what lets baseline selection stop scanning whole payloads. Counting blocks
        /// whose digests differ is a good proxy for how many literal bytes a delta would carry, and
        /// it costs ~28 word compares instead of ~872 byte comparisons. Crucially the digest work
        /// amortizes: it happens once per payload STORED, not once per candidate per packet, so the
        /// ~29-way search stops multiplying it.</para>
        ///
        /// <para>Digests are only a ranking signal — a collision or a weak mix costs a slightly
        /// worse baseline choice, never a wrong delta, because the winner is still measured exactly
        /// before anything is encoded.</para>
        /// </summary>
        private readonly ulong[][] _digests = new ulong[RingSize][];
        private readonly int[] _digestCounts = new int[RingSize];

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

            int blocks = NebulaPack.BlockCount(payload.Length);
            var digests = _digests[idx];
            if (digests == null || digests.Length < blocks)
            {
                _digests[idx] = digests = new ulong[Math.Max(blocks, 64)];
            }
            NebulaPack.ComputeDigests(payload, digests);
            _digestCounts[idx] = blocks;
        }

        /// <summary>
        /// The block digests for an acknowledged tick. Pairs with <see cref="TryGetAcked"/>; kept
        /// separate so ranking can run without touching the payload bytes at all.
        /// </summary>
        internal bool TryGetAckedDigests(Tick tick, out ReadOnlySpan<ulong> digests)
        {
            digests = default;
            if (tick < 0) return false;
            int idx = tick % RingSize;
            if (_ticks[idx] != tick || !_acked[idx]) return false;
            var stored = _digests[idx];
            if (stored == null) return false;
            digests = stored.AsSpan(0, _digestCounts[idx]);
            return true;
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
