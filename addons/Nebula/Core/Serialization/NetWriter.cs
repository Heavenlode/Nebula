using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Godot;

namespace Nebula.Serialization
{
    /// <summary>
    /// High-performance binary writer using BinaryPrimitives for zero-allocation serialization.
    /// Replaces the old HLBytes.Pack* methods.
    /// </summary>
    public static class NetWriter
    {
        #region Primitives

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteByte(NetBuffer buffer, byte value)
        {
            var span = buffer.GetWriteSpan(1);
            span[0] = value;
            buffer.AdvanceWrite(1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBool(NetBuffer buffer, bool value)
        {
            WriteByte(buffer, value ? (byte)1 : (byte)0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt16(NetBuffer buffer, short value)
        {
            var span = buffer.GetWriteSpan(2);
            BinaryPrimitives.WriteInt16LittleEndian(span, value);
            buffer.AdvanceWrite(2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt16(NetBuffer buffer, ushort value)
        {
            var span = buffer.GetWriteSpan(2);
            BinaryPrimitives.WriteUInt16LittleEndian(span, value);
            buffer.AdvanceWrite(2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt32(NetBuffer buffer, int value)
        {
            var span = buffer.GetWriteSpan(4);
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            buffer.AdvanceWrite(4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt32(NetBuffer buffer, uint value)
        {
            var span = buffer.GetWriteSpan(4);
            BinaryPrimitives.WriteUInt32LittleEndian(span, value);
            buffer.AdvanceWrite(4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt64(NetBuffer buffer, long value)
        {
            var span = buffer.GetWriteSpan(8);
            BinaryPrimitives.WriteInt64LittleEndian(span, value);
            buffer.AdvanceWrite(8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt64(NetBuffer buffer, ulong value)
        {
            var span = buffer.GetWriteSpan(8);
            BinaryPrimitives.WriteUInt64LittleEndian(span, value);
            buffer.AdvanceWrite(8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteFloat(NetBuffer buffer, float value)
        {
            var span = buffer.GetWriteSpan(4);
            BinaryPrimitives.WriteSingleLittleEndian(span, value);
            buffer.AdvanceWrite(4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteDouble(NetBuffer buffer, double value)
        {
            var span = buffer.GetWriteSpan(8);
            BinaryPrimitives.WriteDoubleLittleEndian(span, value);
            buffer.AdvanceWrite(8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteHalf(NetBuffer buffer, Half value)
        {
            var span = buffer.GetWriteSpan(2);
            BinaryPrimitives.WriteHalfLittleEndian(span, value);
            buffer.AdvanceWrite(2);
        }

        /// <summary>
        /// Writes a float value as a half-precision float (2 bytes).
        /// Convenience method that performs the float-to-half conversion.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteHalfFloat(NetBuffer buffer, float value)
        {
            var span = buffer.GetWriteSpan(2);
            BinaryPrimitives.WriteHalfLittleEndian(span, (Half)value);
            buffer.AdvanceWrite(2);
        }

        #endregion

        #region Godot Vectors

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteVector2(NetBuffer buffer, Vector2 value)
        {
            // Uses Half precision (2 bytes per component) like the original
            var span = buffer.GetWriteSpan(4);
            BinaryPrimitives.WriteHalfLittleEndian(span, (Half)value.X);
            BinaryPrimitives.WriteHalfLittleEndian(span.Slice(2), (Half)value.Y);
            buffer.AdvanceWrite(4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteVector2Full(NetBuffer buffer, Vector2 value)
        {
            // Full precision (4 bytes per component)
            var span = buffer.GetWriteSpan(8);
            BinaryPrimitives.WriteSingleLittleEndian(span, value.X);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(4), value.Y);
            buffer.AdvanceWrite(8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteVector3(NetBuffer buffer, Vector3 value)
        {
            // Full precision (4 bytes per component) - 12 bytes total
            var span = buffer.GetWriteSpan(12);
            BinaryPrimitives.WriteSingleLittleEndian(span, value.X);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(4), value.Y);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(8), value.Z);
            buffer.AdvanceWrite(12);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteVector3Half(NetBuffer buffer, Vector3 value)
        {
            // Half precision (2 bytes per component) - 6 bytes total
            var span = buffer.GetWriteSpan(6);
            BinaryPrimitives.WriteHalfLittleEndian(span, (Half)value.X);
            BinaryPrimitives.WriteHalfLittleEndian(span.Slice(2), (Half)value.Y);
            BinaryPrimitives.WriteHalfLittleEndian(span.Slice(4), (Half)value.Z);
            buffer.AdvanceWrite(6);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteQuaternion(NetBuffer buffer, Quaternion value)
        {
            // Uses Half precision (2 bytes per component) like the original - 8 bytes total
            var span = buffer.GetWriteSpan(8);
            BinaryPrimitives.WriteHalfLittleEndian(span, (Half)value.X);
            BinaryPrimitives.WriteHalfLittleEndian(span.Slice(2), (Half)value.Y);
            BinaryPrimitives.WriteHalfLittleEndian(span.Slice(4), (Half)value.Z);
            BinaryPrimitives.WriteHalfLittleEndian(span.Slice(6), (Half)value.W);
            buffer.AdvanceWrite(8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteQuaternionFull(NetBuffer buffer, Quaternion value)
        {
            // Full precision (4 bytes per component) - 16 bytes total
            var span = buffer.GetWriteSpan(16);
            BinaryPrimitives.WriteSingleLittleEndian(span, value.X);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(4), value.Y);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(8), value.Z);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(12), value.W);
            buffer.AdvanceWrite(16);
        }

        /// <summary>
        /// Writes a quaternion using smallest-three compression (6 bytes total).
        /// The largest component is omitted and reconstructed on read.
        /// Format: [2 bits: largest index][3 x 14-bit fixed-point components]
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteQuatSmallestThree(NetBuffer buffer, Quaternion q)
        {
            // Find largest component by absolute value
            float absX = MathF.Abs(q.X), absY = MathF.Abs(q.Y);
            float absZ = MathF.Abs(q.Z), absW = MathF.Abs(q.W);
            
            int maxIndex = 0;
            float maxVal = absX;
            if (absY > maxVal) { maxIndex = 1; maxVal = absY; }
            if (absZ > maxVal) { maxIndex = 2; maxVal = absZ; }
            if (absW > maxVal) { maxIndex = 3; }
            
            // Ensure the largest component is positive (for consistent reconstruction)
            float sign = maxIndex switch
            {
                0 => q.X >= 0 ? 1f : -1f,
                1 => q.Y >= 0 ? 1f : -1f,
                2 => q.Z >= 0 ? 1f : -1f,
                _ => q.W >= 0 ? 1f : -1f
            };
            
            // Get the three smallest components (normalized by sign)
            float a, b, c;
            switch (maxIndex)
            {
                case 0: a = q.Y * sign; b = q.Z * sign; c = q.W * sign; break;
                case 1: a = q.X * sign; b = q.Z * sign; c = q.W * sign; break;
                case 2: a = q.X * sign; b = q.Y * sign; c = q.W * sign; break;
                default: a = q.X * sign; b = q.Y * sign; c = q.Z * sign; break;
            }
            
            // Convert to 14-bit fixed point: range [-1, 1] -> [0, 16383]
            // Using 14 bits gives ~0.00012 precision which is plenty for quaternions
            const float scale = 8191.5f; // (16383 / 2)
            ushort ua = (ushort)Math.Clamp((int)((a + 1f) * scale), 0, 16383);
            ushort ub = (ushort)Math.Clamp((int)((b + 1f) * scale), 0, 16383);
            ushort uc = (ushort)Math.Clamp((int)((c + 1f) * scale), 0, 16383);
            
            // Pack: 2 bits index + 14 bits a + 14 bits b + 14 bits c = 44 bits = 6 bytes
            // Byte 0: [index:2][a_high:6]
            // Byte 1: [a_low:8]
            // Byte 2: [b_high:6][a_remainder:2] -> actually let's use simpler packing
            // Simpler: 3 x 16-bit values, first 2 bits of first value is index
            // Pack index into high 2 bits of first component
            var span = buffer.GetWriteSpan(6);
            ushort packed0 = (ushort)((maxIndex << 14) | ua);
            BinaryPrimitives.WriteUInt16LittleEndian(span, packed0);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2), ub);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4), uc);
            buffer.AdvanceWrite(6);
        }

        #endregion

        #region Arrays and Strings

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBytes(NetBuffer buffer, byte[] data)
        {
            buffer.CopyFrom(data, 0, data.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBytes(NetBuffer buffer, byte[] data, int offset, int count)
        {
            buffer.CopyFrom(data, offset, count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBytes(NetBuffer buffer, ReadOnlySpan<byte> data)
        {
            buffer.CopyFrom(data);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBytesWithLength(NetBuffer buffer, byte[] data)
        {
            WriteInt32(buffer, data.Length);
            WriteBytes(buffer, data);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBytesWithLength(NetBuffer buffer, ReadOnlySpan<byte> data)
        {
            WriteInt32(buffer, data.Length);
            buffer.CopyFrom(data);
        }

        public static void WriteString(NetBuffer buffer, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                WriteInt32(buffer, 0);
                return;
            }

            // Get the byte count first
            int byteCount = Encoding.UTF8.GetByteCount(value);
            WriteInt32(buffer, byteCount);

            // Write directly into the buffer
            var span = buffer.GetWriteSpan(byteCount);
            Encoding.UTF8.GetBytes(value.AsSpan(), span);
            buffer.AdvanceWrite(byteCount);
        }

        public static void WriteInt32Array(NetBuffer buffer, int[] values)
        {
            WriteInt32(buffer, values.Length);
            foreach (var val in values)
            {
                WriteInt32(buffer, val);
            }
        }

        public static void WriteInt64Array(NetBuffer buffer, long[] values)
        {
            WriteInt32(buffer, values.Length);
            foreach (var val in values)
            {
                WriteInt64(buffer, val);
            }
        }

        #endregion

        #region Buffer Operations

        /// <summary>
        /// Writes the contents of another NetBuffer to this buffer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBuffer(NetBuffer buffer, NetBuffer source)
        {
            buffer.CopyFrom(source);
        }

        /// <summary>
        /// Writes the contents of another NetBuffer with a length prefix.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBufferWithLength(NetBuffer buffer, NetBuffer source)
        {
            WriteInt32(buffer, source.Length);
            buffer.CopyFrom(source);
        }

        #endregion

        #region Type-Tagged Writing (for polymorphic paths)

        /// <summary>
        /// Writes a type tag followed by the value. Used for polymorphic serialization
        /// where the type isn't known at compile time.
        /// </summary>
        public static void WriteWithType(NetBuffer buffer, SerialVariantType type, object value)
        {
            WriteByte(buffer, (byte)type);
            WriteByType(buffer, type, value);
        }

        /// <summary>
        /// Writes a value based on its SerialVariantType without writing the type tag.
        /// </summary>
        public static void WriteByType(NetBuffer buffer, SerialVariantType type, object value)
        {
            switch (type)
            {
                case SerialVariantType.Bool:
                    WriteBool(buffer, (bool)value);
                    break;
                case SerialVariantType.Int:
                    WriteInt64(buffer, Convert.ToInt64(value));
                    break;
                case SerialVariantType.Float:
                    WriteFloat(buffer, Convert.ToSingle(value));
                    break;
                case SerialVariantType.String:
                    WriteString(buffer, (string)value);
                    break;
                case SerialVariantType.Vector2:
                    WriteVector2(buffer, (Vector2)value);
                    break;
                case SerialVariantType.Vector3:
                    WriteVector3(buffer, (Vector3)value);
                    break;
                case SerialVariantType.Quaternion:
                    WriteQuaternion(buffer, (Quaternion)value);
                    break;
                case SerialVariantType.PackedByteArray:
                    WriteBytesWithLength(buffer, (byte[])value);
                    break;
                case SerialVariantType.PackedInt32Array:
                    WriteInt32Array(buffer, (int[])value);
                    break;
                case SerialVariantType.PackedInt64Array:
                    WriteInt64Array(buffer, (long[])value);
                    break;
                default:
                    throw new NotSupportedException($"NetWriter.WriteByType: Unsupported type {type}");
            }
        }

        #endregion
    }
}
