using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Godot;

namespace Nebula.Serialization
{
    /// <summary>
    /// High-performance binary reader using BinaryPrimitives for zero-allocation deserialization.
    /// Replaces the old HLBytes.Unpack* methods.
    /// </summary>
    public static class NetReader
    {
        #region Primitives

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte ReadByte(NetBuffer buffer)
        {
            var span = buffer.GetReadSpan(1);
            var value = span[0];
            buffer.AdvanceRead(1);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ReadBool(NetBuffer buffer)
        {
            return ReadByte(buffer) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short ReadInt16(NetBuffer buffer)
        {
            var span = buffer.GetReadSpan(2);
            var value = BinaryPrimitives.ReadInt16LittleEndian(span);
            buffer.AdvanceRead(2);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ReadUInt16(NetBuffer buffer)
        {
            var span = buffer.GetReadSpan(2);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(span);
            buffer.AdvanceRead(2);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadInt32(NetBuffer buffer)
        {
            var span = buffer.GetReadSpan(4);
            var value = BinaryPrimitives.ReadInt32LittleEndian(span);
            buffer.AdvanceRead(4);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadUInt32(NetBuffer buffer)
        {
            var span = buffer.GetReadSpan(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(span);
            buffer.AdvanceRead(4);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ReadInt64(NetBuffer buffer)
        {
            var span = buffer.GetReadSpan(8);
            var value = BinaryPrimitives.ReadInt64LittleEndian(span);
            buffer.AdvanceRead(8);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReadUInt64(NetBuffer buffer)
        {
            var span = buffer.GetReadSpan(8);
            var value = BinaryPrimitives.ReadUInt64LittleEndian(span);
            buffer.AdvanceRead(8);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ReadFloat(NetBuffer buffer)
        {
            var span = buffer.GetReadSpan(4);
            var value = BinaryPrimitives.ReadSingleLittleEndian(span);
            buffer.AdvanceRead(4);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double ReadDouble(NetBuffer buffer)
        {
            var span = buffer.GetReadSpan(8);
            var value = BinaryPrimitives.ReadDoubleLittleEndian(span);
            buffer.AdvanceRead(8);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Half ReadHalf(NetBuffer buffer)
        {
            var span = buffer.GetReadSpan(2);
            var value = BinaryPrimitives.ReadHalfLittleEndian(span);
            buffer.AdvanceRead(2);
            return value;
        }

        /// <summary>
        /// Reads a half-precision float and returns it as a full float.
        /// Convenience method for delta decoding.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ReadHalfFloat(NetBuffer buffer)
        {
            var span = buffer.GetReadSpan(2);
            var value = (float)BinaryPrimitives.ReadHalfLittleEndian(span);
            buffer.AdvanceRead(2);
            return value;
        }

        #endregion

        #region Godot Vectors

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 ReadVector2(NetBuffer buffer)
        {
            // Half precision (2 bytes per component) like the original
            var span = buffer.GetReadSpan(4);
            var x = (float)BinaryPrimitives.ReadHalfLittleEndian(span);
            var y = (float)BinaryPrimitives.ReadHalfLittleEndian(span.Slice(2));
            buffer.AdvanceRead(4);
            return new Vector2(x, y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 ReadVector2Full(NetBuffer buffer)
        {
            // Full precision (4 bytes per component)
            var span = buffer.GetReadSpan(8);
            var x = BinaryPrimitives.ReadSingleLittleEndian(span);
            var y = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(4));
            buffer.AdvanceRead(8);
            return new Vector2(x, y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 ReadVector3(NetBuffer buffer)
        {
            // Full precision (4 bytes per component) - 12 bytes total
            var span = buffer.GetReadSpan(12);
            var x = BinaryPrimitives.ReadSingleLittleEndian(span);
            var y = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(4));
            var z = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(8));
            buffer.AdvanceRead(12);
            return new Vector3(x, y, z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 ReadVector3Half(NetBuffer buffer)
        {
            // Half precision (2 bytes per component) - 6 bytes total
            var span = buffer.GetReadSpan(6);
            var x = (float)BinaryPrimitives.ReadHalfLittleEndian(span);
            var y = (float)BinaryPrimitives.ReadHalfLittleEndian(span.Slice(2));
            var z = (float)BinaryPrimitives.ReadHalfLittleEndian(span.Slice(4));
            buffer.AdvanceRead(6);
            return new Vector3(x, y, z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion ReadQuaternion(NetBuffer buffer)
        {
            // Half precision (2 bytes per component) like the original - 8 bytes total
            var span = buffer.GetReadSpan(8);
            var x = (float)BinaryPrimitives.ReadHalfLittleEndian(span);
            var y = (float)BinaryPrimitives.ReadHalfLittleEndian(span.Slice(2));
            var z = (float)BinaryPrimitives.ReadHalfLittleEndian(span.Slice(4));
            var w = (float)BinaryPrimitives.ReadHalfLittleEndian(span.Slice(6));
            buffer.AdvanceRead(8);
            return new Quaternion(x, y, z, w);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion ReadQuaternionFull(NetBuffer buffer)
        {
            // Full precision (4 bytes per component) - 16 bytes total
            var span = buffer.GetReadSpan(16);
            var x = BinaryPrimitives.ReadSingleLittleEndian(span);
            var y = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(4));
            var z = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(8));
            var w = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(12));
            buffer.AdvanceRead(16);
            return new Quaternion(x, y, z, w);
        }

        /// <summary>
        /// Reads a quaternion encoded with smallest-three compression (6 bytes).
        /// Reconstructs the largest component from the other three.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion ReadQuatSmallestThree(NetBuffer buffer)
        {
            var span = buffer.GetReadSpan(6);
            ushort packed0 = BinaryPrimitives.ReadUInt16LittleEndian(span);
            ushort packed1 = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2));
            ushort packed2 = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4));
            buffer.AdvanceRead(6);
            
            // Extract largest index from high 2 bits of first value
            int maxIndex = packed0 >> 14;
            ushort ua = (ushort)(packed0 & 0x3FFF);
            ushort ub = packed1;
            ushort uc = packed2;
            
            // Convert from 14-bit fixed point back to float: [0, 16383] -> [-1, 1]
            const float invScale = 1f / 8191.5f;
            float a = ua * invScale - 1f;
            float b = ub * invScale - 1f;
            float c = uc * invScale - 1f;
            
            // Reconstruct the largest component using unit quaternion property
            // |q|^2 = x^2 + y^2 + z^2 + w^2 = 1
            float sumSq = a * a + b * b + c * c;
            float largest = MathF.Sqrt(MathF.Max(0f, 1f - sumSq));
            
            // Reconstruct quaternion based on which component was largest
            float x, y, z, w;
            switch (maxIndex)
            {
                case 0: x = largest; y = a; z = b; w = c; break;
                case 1: x = a; y = largest; z = b; w = c; break;
                case 2: x = a; y = b; z = largest; w = c; break;
                default: x = a; y = b; z = c; w = largest; break;
            }
            
            return new Quaternion(x, y, z, w);
        }

        #endregion

        #region Arrays and Strings

        /// <summary>
        /// Reads a fixed number of bytes into a new array.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] ReadBytes(NetBuffer buffer, int count)
        {
            var span = buffer.GetReadSpan(count);
            var result = span.ToArray();
            buffer.AdvanceRead(count);
            return result;
        }

        /// <summary>
        /// Reads bytes with a length prefix.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] ReadBytesWithLength(NetBuffer buffer)
        {
            int length = ReadInt32(buffer);
            if (length == 0) return Array.Empty<byte>();
            return ReadBytes(buffer, length);
        }

        /// <summary>
        /// Reads all remaining bytes in the buffer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] ReadRemainingBytes(NetBuffer buffer)
        {
            return ReadBytes(buffer, buffer.Remaining);
        }

        /// <summary>
        /// Reads a UTF-8 string with length prefix.
        /// </summary>
        public static string ReadString(NetBuffer buffer)
        {
            int byteCount = ReadInt32(buffer);
            if (byteCount == 0) return string.Empty;

            var span = buffer.GetReadSpan(byteCount);
            var result = Encoding.UTF8.GetString(span);
            buffer.AdvanceRead(byteCount);
            return result;
        }

        public static int[] ReadInt32Array(NetBuffer buffer)
        {
            int count = ReadInt32(buffer);
            if (count == 0) return Array.Empty<int>();

            var result = new int[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = ReadInt32(buffer);
            }
            return result;
        }

        public static long[] ReadInt64Array(NetBuffer buffer)
        {
            int count = ReadInt32(buffer);
            if (count == 0) return Array.Empty<long>();

            var result = new long[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = ReadInt64(buffer);
            }
            return result;
        }

        #endregion

        #region Type-Tagged Reading (for polymorphic paths)

        /// <summary>
        /// Reads a type tag and then the value. Used for polymorphic deserialization.
        /// Returns the type that was read via out parameter.
        /// </summary>
        public static object ReadWithType(NetBuffer buffer, out SerialVariantType type)
        {
            type = (SerialVariantType)ReadByte(buffer);
            return ReadByType(buffer, type);
        }

        /// <summary>
        /// Reads a value based on its SerialVariantType without reading the type tag.
        /// </summary>
        public static object ReadByType(NetBuffer buffer, SerialVariantType type)
        {
            return type switch
            {
                SerialVariantType.Bool => ReadBool(buffer),
                SerialVariantType.Int => ReadInt64(buffer),
                SerialVariantType.Float => ReadFloat(buffer),
                SerialVariantType.String => ReadString(buffer),
                SerialVariantType.Vector2 => ReadVector2(buffer),
                SerialVariantType.Vector3 => ReadVector3(buffer),
                SerialVariantType.Quaternion => ReadQuaternion(buffer),
                SerialVariantType.PackedByteArray => ReadBytesWithLength(buffer),
                SerialVariantType.PackedInt32Array => ReadInt32Array(buffer),
                SerialVariantType.PackedInt64Array => ReadInt64Array(buffer),
                _ => throw new NotSupportedException($"NetReader.ReadByType: Unsupported type {type}")
            };
        }

        /// <summary>
        /// Reads a value and converts it to Godot.Variant.
        /// Only use this at the boundary where you need to interface with Godot APIs.
        /// </summary>
        public static Variant ReadAsVariant(NetBuffer buffer, SerialVariantType type)
        {
            return type switch
            {
                SerialVariantType.Bool => ReadBool(buffer),
                SerialVariantType.Int => ReadInt64(buffer),
                SerialVariantType.Float => ReadFloat(buffer),
                SerialVariantType.String => ReadString(buffer),
                SerialVariantType.Vector2 => ReadVector2(buffer),
                SerialVariantType.Vector3 => ReadVector3(buffer),
                SerialVariantType.Quaternion => ReadQuaternion(buffer),
                SerialVariantType.PackedByteArray => ReadBytesWithLength(buffer),
                SerialVariantType.PackedInt32Array => ReadInt32Array(buffer),
                SerialVariantType.PackedInt64Array => ReadInt64Array(buffer),
                _ => throw new NotSupportedException($"NetReader.ReadAsVariant: Unsupported type {type}")
            };
        }

        #endregion
    }
}
