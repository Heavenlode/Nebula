using System;
using Nebula.Serialization.Serializers;

namespace Nebula.Serialization
{
    /// <summary>
    /// Wire encoding of a props section's presence mask (which of a scene's properties the
    /// section carries), shared by the server writer and the client reader so the two can
    /// never disagree. Bit-packed: a narrow scene spends exactly one bit per declared
    /// property; a wide scene spends one header bit per mask byte plus eight bits for each
    /// byte that is nonzero.
    ///
    /// <para>A scene with N properties has a mask of <c>ceil(N/8)</c> bytes, and a typical
    /// section sets two or three bits of it: a 60-property player scene would pay 60 bits
    /// every tick to say that position and rotation changed. So for wide masks the wire
    /// carries a HEADER whose bit i means "mask byte i is nonzero", followed by only those
    /// bytes, in ascending order. The property index layout clusters a node's hot properties
    /// into one or two mask bytes, which is what makes this pay: 60 bits become 16.</para>
    ///
    /// <para>Narrow masks stay flat. With one or two mask bytes, <c>header + nonzero</c> can
    /// never come in under the flat width. Both sides derive the rule from the property
    /// count alone - no per-section selector.</para>
    ///
    /// <para>Because the header has exactly one bit per mask byte, no header value can name a
    /// byte that does not exist; the "corrupt header" abort the byte-era decoder needed is
    /// gone with it.</para>
    /// </summary>
    internal static class PresenceMask
    {
        /// <summary>
        /// Smallest mask width (in bytes) that uses the two-level encoding. Below this the
        /// header cannot win: at width 2 a single nonzero byte already costs 2 + 8 against a
        /// flat 9..16.
        /// </summary>
        public const int TwoLevelMinBytes = 3;

        /// <summary>
        /// Widest mask this encoding supports. Equal to the scene property ceiling
        /// (<see cref="BitConstants.MaxSceneProperties"/> = 64 bits), so nothing the generator
        /// admits can exceed it.
        /// </summary>
        public const int MaxMaskBytes = BitConstants.MaxSceneProperties / BitConstants.BitsInByte;

        public static bool UsesTwoLevel(int byteCount) => byteCount >= TwoLevelMinBytes;

        /// <summary>Bits a mask of this shape costs on the wire.</summary>
        public static int Bits(ReadOnlySpan<byte> mask, int propertyCount)
        {
            if (!UsesTwoLevel(mask.Length)) return propertyCount;
            int bits = mask.Length;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] != 0) bits += BitConstants.BitsInByte;
            }
            return bits;
        }

        /// <summary>
        /// The most bits any mask of this width can cost: the flat width, or header plus every
        /// mask byte when all of them turn out nonzero. What the writer budgets against before
        /// the mask is final.
        /// </summary>
        public static int WorstCaseBits(int byteCount, int propertyCount)
            => UsesTwoLevel(byteCount) ? byteCount + byteCount * BitConstants.BitsInByte : propertyCount;

        /// <summary>
        /// Writes <paramref name="mask"/> (exactly <c>ceil(propertyCount/8)</c> bytes, with no
        /// bit set at or above <paramref name="propertyCount"/>).
        /// </summary>
        public static void Write(NetBuffer buffer, ReadOnlySpan<byte> mask, int propertyCount)
        {
            if (mask.Length > MaxMaskBytes)
            {
                throw new InvalidOperationException(
                    $"Presence mask of {mask.Length} bytes exceeds the {MaxMaskBytes}-byte ceiling.");
            }

            if (!UsesTwoLevel(mask.Length))
            {
                int remaining = propertyCount;
                for (int i = 0; i < mask.Length && remaining > 0; i++)
                {
                    int bits = Math.Min(BitConstants.BitsInByte, remaining);
                    buffer.WriteBits(mask[i], bits);
                    remaining -= bits;
                }
                return;
            }

            ulong header = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] != 0) header |= 1UL << i;
            }
            buffer.WriteBits(header, mask.Length);
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] != 0) buffer.WriteBits(mask[i], BitConstants.BitsInByte);
            }
        }

        /// <summary>
        /// Reads a mask of exactly <c>mask.Length</c> bytes. Every byte of
        /// <paramref name="mask"/> is written - the ones the header does not name are zeroed -
        /// so a caller may hand in reused scratch without clearing it.
        /// </summary>
        public static void Read(NetBuffer buffer, Span<byte> mask, int propertyCount)
        {
            if (mask.Length > MaxMaskBytes)
            {
                throw new InvalidOperationException(
                    $"Presence mask of {mask.Length} bytes exceeds the {MaxMaskBytes}-byte ceiling.");
            }

            if (!UsesTwoLevel(mask.Length))
            {
                int remaining = propertyCount;
                for (int i = 0; i < mask.Length; i++)
                {
                    int bits = Math.Min(BitConstants.BitsInByte, remaining);
                    mask[i] = bits > 0 ? (byte)buffer.ReadBits(bits) : (byte)0;
                    remaining -= bits;
                }
                return;
            }

            ulong header = buffer.ReadBits(mask.Length);
            for (int i = 0; i < mask.Length; i++)
            {
                mask[i] = (header & (1UL << i)) != 0 ? (byte)buffer.ReadBits(BitConstants.BitsInByte) : (byte)0;
            }
        }
    }
}
