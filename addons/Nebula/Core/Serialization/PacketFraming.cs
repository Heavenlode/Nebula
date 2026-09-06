using System;
using Nebula.Serialization.Serializers;

namespace Nebula.Serialization
{
    /// <summary>
    /// Bit-level framing of a tick payload, shared by the server assembler
    /// (<c>WorldRunner.ExportState</c>) and the client parser (<c>WorldRunner.ImportState</c>)
    /// so the two can never disagree:
    ///
    /// <code>
    /// groupPresence      8 bits   bit g = 64-node group g has at least one node in the packet
    /// per set group:     node set (below)
    /// per node, ascending: serializers-run (below)
    /// per node, ascending: its sections, concatenated at bit granularity
    /// pad to byte
    /// </code>
    ///
    /// <para><b>Node set.</b> A group's 64-bit node mask is either dense (<c>1</c> + the raw
    /// 64 bits) or sparse (<c>0</c> + 6-bit count-1, then <c>gamma(firstIndex + 1)</c> and
    /// <c>gamma(gap)</c> per further node, gaps &gt;= 1). Players' nodes are allocated in runs,
    /// so a typical 20-peer group is ~30 bits sparse against 65 dense; the writer picks the
    /// shorter per group. The budget ledger charges the dense worst case.</para>
    ///
    /// <para><b>Serializers-run.</b> A node whose only section is props (the steady-state
    /// case) spends one bit; anything else spends <c>0</c> plus the 3-bit run mask.</para>
    /// </summary>
    internal static class PacketFraming
    {
        public const int GroupPresenceBits = BitConstants.BitsInByte;
        private const int DenseSelectorBits = 1;
        private const int DenseMaskBits = BitConstants.BitsInLong;
        private const int SparseCountBits = 6;
        /// <summary>What the ledger charges for a group opening: the dense form.</summary>
        public const int NodeSetWorstBits = DenseSelectorBits + DenseMaskBits;

        private const int PropsOnlyBits = 1;
        /// <summary>One bit per serializer index (spawn, props, resync).</summary>
        public const int SerializersRunBits = 3;
        /// <summary>What the ledger charges for a node's first section: the long form.</summary>
        public const int SerializersRunWorstBits = PropsOnlyBits + SerializersRunBits;

        /// <summary>Bits the sparse form would cost for this mask (mask must be nonzero).</summary>
        public static int SparseBits(long mask)
        {
            int bits = DenseSelectorBits + SparseCountBits;
            int previous = -1;
            ulong remaining = (ulong)mask;
            while (remaining != 0)
            {
                int index = System.Numerics.BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
                bits += BitCodec.GammaBits((uint)(index - previous));  // first: index + 1; later: gap >= 1
                previous = index;
            }
            return bits;
        }

        /// <summary>Bits the writer will spend on this mask: the shorter form.</summary>
        public static int NodeSetBits(long mask) => Math.Min(NodeSetWorstBits, SparseBits(mask));

        public static void WriteNodeSet(NetBuffer buffer, long mask)
        {
            if (mask == 0) throw new ArgumentOutOfRangeException(nameof(mask), "a present group has at least one node");
            if (SparseBits(mask) >= NodeSetWorstBits)
            {
                buffer.WriteBool(true);
                buffer.WriteBits((ulong)mask, DenseMaskBits);
                return;
            }
            buffer.WriteBool(false);
            int count = System.Numerics.BitOperations.PopCount((ulong)mask);
            buffer.WriteBits((ulong)(count - 1), SparseCountBits);
            int previous = -1;
            ulong remaining = (ulong)mask;
            while (remaining != 0)
            {
                int index = System.Numerics.BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
                BitCodec.WriteGamma(buffer, (uint)(index - previous));
                previous = index;
            }
        }

        public static long ReadNodeSet(NetBuffer buffer)
        {
            if (buffer.ReadBool())
            {
                return (long)buffer.ReadBits(DenseMaskBits);
            }
            int count = (int)buffer.ReadBits(SparseCountBits) + 1;
            long mask = 0;
            int previous = -1;
            for (int i = 0; i < count; i++)
            {
                int index = previous + (int)BitCodec.ReadGamma(buffer);
                if (index >= DenseMaskBits) throw new InvalidOperationException($"sparse node set names index {index}");
                mask |= 1L << index;
                previous = index;
            }
            return mask;
        }

        public static void WriteSerializersRun(NetBuffer buffer, byte run, int propsSerializerIndex)
        {
            if (run == (1 << propsSerializerIndex))
            {
                buffer.WriteBool(true);
                return;
            }
            buffer.WriteBool(false);
            buffer.WriteBits(run, SerializersRunBits);
        }

        public static byte ReadSerializersRun(NetBuffer buffer, int propsSerializerIndex)
        {
            if (buffer.ReadBool()) return (byte)(1 << propsSerializerIndex);
            return (byte)buffer.ReadBits(SerializersRunBits);
        }
    }
}
