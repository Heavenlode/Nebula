using System;

namespace Nebula.Serialization
{
    /// <summary>
    /// Variable-width integer forms for the bit stream, shared by writer and reader so the two
    /// can never disagree. All forms are self-delimiting: the reader learns the width from the
    /// stream, never from context.
    /// </summary>
    internal static class BitCodec
    {
        /// <summary>Bits of the length prefix in the magnitude form: (L - 1) for L in 1..32.</summary>
        public const int MagnitudeLengthBits = 5;

        /// <summary>
        /// Signed integer as [nonzero: 1 bit] and, when nonzero, [L-1: 5 bits][the L-1 bits below
        /// the magnitude's implicit leading 1][sign: 1 bit]. Zero costs 1 bit; +-1 costs 7;
        /// 60,123 costs 22; int.MinValue (magnitude 2^31, L = 32) costs 38. Used for quantized
        /// absolutes and full deltas, where the zigzag varint's 8-bit granularity wasted bits,
        /// and where "component did not move" (zero) is the common case.
        /// </summary>
        public static void WriteMagnitude(NetBuffer buffer, int value)
        {
            if (value == 0)
            {
                buffer.WriteBool(false);
                return;
            }
            buffer.WriteBool(true);
            // |int.MinValue| does not fit an int; work in ulong.
            ulong magnitude = value < 0 ? (ulong)(-(long)value) : (ulong)value;
            int length = 64 - System.Numerics.BitOperations.LeadingZeroCount(magnitude);
            buffer.WriteBits((ulong)(length - 1), MagnitudeLengthBits);
            if (length > 1) buffer.WriteBits(magnitude, length - 1); // low bits; the top bit is implicit
            buffer.WriteBool(value < 0);
        }

        public static int ReadMagnitude(NetBuffer buffer)
        {
            if (!buffer.ReadBool()) return 0;
            int length = (int)buffer.ReadBits(MagnitudeLengthBits) + 1;
            ulong magnitude = 1UL << (length - 1);
            if (length > 1) magnitude |= buffer.ReadBits(length - 1);
            bool negative = buffer.ReadBool();
            // magnitude 2^31 is only representable as int.MinValue.
            if (magnitude > int.MaxValue && !(negative && magnitude == (ulong)int.MaxValue + 1))
                throw new InvalidOperationException($"magnitude {magnitude} exceeds an int");
            return negative ? (int)(-(long)magnitude) : (int)magnitude;
        }

        /// <summary>Bits <see cref="WriteMagnitude"/> spends on a value, for budget arithmetic.</summary>
        public static int MagnitudeBits(int value)
        {
            if (value == 0) return 1;
            ulong magnitude = value < 0 ? (ulong)(-(long)value) : (ulong)value;
            int length = 64 - System.Numerics.BitOperations.LeadingZeroCount(magnitude);
            return 1 + MagnitudeLengthBits + (length - 1) + 1;
        }

        /// <summary>
        /// Elias gamma for values &gt;= 1: (L-1) zero bits, then the L-bit value MSB-first, where
        /// L is the value's bit length. 1 costs 1 bit, 2..3 cost 3, 4..7 cost 5, 8..15 cost 7.
        /// Used for node-index gaps in the packet's sparse group form. Callers encode a
        /// zero-based quantity as value + 1.
        /// </summary>
        public static void WriteGamma(NetBuffer buffer, uint value)
        {
            if (value == 0) throw new ArgumentOutOfRangeException(nameof(value), "gamma codes values >= 1");
            int length = 32 - System.Numerics.BitOperations.LeadingZeroCount(value);
            if (length > 1) buffer.WriteBits(0, length - 1);
            // MSB-first so the leading 1 terminates the zero run for the reader.
            for (int i = length - 1; i >= 0; i--) buffer.WriteBool(((value >> i) & 1) != 0);
        }

        public static uint ReadGamma(NetBuffer buffer)
        {
            int zeros = 0;
            while (!buffer.ReadBool())
            {
                zeros++;
                if (zeros > 31) throw new InvalidOperationException("gamma zero run exceeds 31");
            }
            uint value = 1;
            for (int i = 0; i < zeros; i++) value = (value << 1) | (buffer.ReadBool() ? 1u : 0u);
            return value;
        }

        /// <summary>Bits <see cref="WriteGamma"/> spends on a value.</summary>
        public static int GammaBits(uint value) => 2 * (32 - System.Numerics.BitOperations.LeadingZeroCount(value)) - 1;
    }
}
