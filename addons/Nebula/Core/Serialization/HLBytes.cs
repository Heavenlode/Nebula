using System;
using Godot;
using Nebula.Utility.Tools;

namespace Nebula.Serialization
{
    /// <summary>
    /// Converts variables and <see cref="Godot.Variant">Godot variants</see> into binary and vice-versa. <see cref="HLBuffer"/> is the medium of storage.
    /// </summary>
    public class HLBytes
    {

        public static void AddRange(ref byte[] array, byte[] toAdd)
        {
            Array.Resize(ref array, array.Length + toAdd.Length);
            Array.Copy(toAdd, 0, array, array.Length - toAdd.Length, toAdd.Length);
        }
        public static void PackVariant(HLBuffer buffer, Variant varVal, bool packLength = false, bool packType = false)
        {
            if (varVal.VariantType == Variant.Type.Vector3)
            {
                Pack(buffer, (Vector3)varVal, packType);
            }
            else if (varVal.VariantType == Variant.Type.Vector2)
            {
                Pack(buffer, (Vector2)varVal, packType);
            }
            else if (varVal.VariantType == Variant.Type.Quaternion)
            {
                Pack(buffer, (Quaternion)varVal, packType);
            }
            else if (varVal.VariantType == Variant.Type.Float)
            {
                Pack(buffer, (float)varVal, packType);
            }
            else if (varVal.VariantType == Variant.Type.Int)
            {
                Pack(buffer, (long)varVal, packType);
            }
            else if (varVal.VariantType == Variant.Type.Bool)
            {
                Pack(buffer, (bool)varVal, packType);
            }
            else if (varVal.VariantType == Variant.Type.Array)
            {
                PackArray(buffer, (Godot.Collections.Array)varVal, packType);
            }
            else if (varVal.VariantType == Variant.Type.Dictionary)
            {
                PackDictionary(buffer, (Godot.Collections.Dictionary)varVal, packType);
            }
            else if (varVal.VariantType == Variant.Type.PackedByteArray)
            {
                Pack(buffer, (byte[])varVal, packLength, packType);
            }
            else if (varVal.VariantType == Variant.Type.PackedInt32Array)
            {
                Pack(buffer, (int[])varVal, packType);
            }
            else if (varVal.VariantType == Variant.Type.PackedInt64Array)
            {
                Pack(buffer, (long[])varVal, packType);
            } else if (varVal.VariantType == Variant.Type.String)
            {
                Pack(buffer, (string)varVal, packType);
            }
            else
            {
                throw new Exception($"HLBytes.Pack: Unhandled type: {varVal.VariantType}");
            }
        }

        public static void Pack(HLBuffer buffer, Vector3 varVal, bool packType = false)
        {
            if (packType)
            {
                Array.Resize(ref buffer.bytes, buffer.bytes.Length + 1);
                buffer.bytes[buffer.Pointer] = (byte)Variant.Type.Vector3;
                buffer.Pointer += 1;
            }

            Array.Resize(ref buffer.bytes, buffer.bytes.Length + 12);
            byte[] floatBytes = BitConverter.GetBytes(varVal.X);
            Array.Copy(floatBytes, 0, buffer.bytes, buffer.Pointer, 4);
            buffer.Pointer += 4;
            floatBytes = BitConverter.GetBytes(varVal.Y);
            Array.Copy(floatBytes, 0, buffer.bytes, buffer.Pointer, 4);
            buffer.Pointer += 4;
            floatBytes = BitConverter.GetBytes(varVal.Z);
            Array.Copy(floatBytes, 0, buffer.bytes, buffer.Pointer, 4);
            buffer.Pointer += 4;
        }

        public static void Pack(HLBuffer buffer, Vector2 varVal, bool packType = false)
        {
            if (packType)
            {
                Array.Resize(ref buffer.bytes, buffer.bytes.Length + 1);
                buffer.bytes[buffer.Pointer] = (byte)Variant.Type.Vector2;
                buffer.Pointer += 1;
            }

            Array.Resize(ref buffer.bytes, buffer.bytes.Length + 4);
            byte[] floatBytes = BitConverter.GetBytes((Half)varVal.X);
            Array.Copy(floatBytes, 0, buffer.bytes, buffer.Pointer, 2);
            buffer.Pointer += 2;
            floatBytes = BitConverter.GetBytes((Half)varVal.Y);
            Array.Copy(floatBytes, 0, buffer.bytes, buffer.Pointer, 2);
            buffer.Pointer += 2;
        }

        public static void Pack(HLBuffer buffer, Quaternion varVal, bool packType = false)
        {
            if (packType)
            {
                Array.Resize(ref buffer.bytes, buffer.bytes.Length + 1);
                buffer.bytes[buffer.Pointer] = (byte)Variant.Type.Quaternion;
                buffer.Pointer += 1;
            }

            Array.Resize(ref buffer.bytes, buffer.bytes.Length + 8);
            byte[] floatBytes = BitConverter.GetBytes((Half)varVal.X);
            Array.Copy(floatBytes, 0, buffer.bytes, buffer.Pointer, 2);
            buffer.Pointer += 2;
            floatBytes = BitConverter.GetBytes((Half)varVal.Y);
            Array.Copy(floatBytes, 0, buffer.bytes, buffer.Pointer, 2);
            buffer.Pointer += 2;
            floatBytes = BitConverter.GetBytes((Half)varVal.Z);
            Array.Copy(floatBytes, 0, buffer.bytes, buffer.Pointer, 2);
            buffer.Pointer += 2;
            floatBytes = BitConverter.GetBytes((Half)varVal.W);
            Array.Copy(floatBytes, 0, buffer.bytes, buffer.Pointer, 2);
            buffer.Pointer += 2;
        }

        public static void Pack(HLBuffer buffer, float varVal, bool packType = false)
        {
            if (packType)
            {
                Array.Resize(ref buffer.bytes, buffer.bytes.Length + 1);
                buffer.bytes[buffer.Pointer] = (byte)Variant.Type.Float;
                buffer.Pointer += 1;
            }

            Array.Resize(ref buffer.bytes, buffer.bytes.Length + 4);
            byte[] floatBytes = BitConverter.GetBytes(varVal);
            Array.Copy(floatBytes, 0, buffer.bytes, buffer.Pointer, 4);
            buffer.Pointer += 4;
        }

        public static void Pack(HLBuffer buffer, byte varVal)
        {
            Array.Resize(ref buffer.bytes, buffer.bytes.Length + 1);
            buffer.bytes[buffer.Pointer] = varVal;
            buffer.Pointer += 1;
        }

        public static void Pack(HLBuffer buffer, short varVal, bool packType = false)
        {
            if (packType)
            {
                throw new Exception("Cannot packType short. Only long allowed becaused Godot Variant.Int is long.");
            }

            Array.Resize(ref buffer.bytes, buffer.bytes.Length + 2);
            byte[] intBytes = BitConverter.GetBytes(varVal);
            Array.Copy(intBytes, 0, buffer.bytes, buffer.Pointer, 2);
            buffer.Pointer += 2;
        }

        public static void Pack(HLBuffer buffer, int varVal, bool packType = false)
        {
            if (packType)
            {
                throw new Exception("Cannot packType int. Only long allowed becaused Godot Variant.Int is long.");
            }

            Array.Resize(ref buffer.bytes, buffer.bytes.Length + 4);
            byte[] intBytes = BitConverter.GetBytes(varVal);
            Array.Copy(intBytes, 0, buffer.bytes, buffer.Pointer, 4);
            buffer.Pointer += 4;
        }

        public static void Pack(HLBuffer buffer, long varVal, bool packType = false)
        {
            if (packType)
            {
                Array.Resize(ref buffer.bytes, buffer.bytes.Length + 1);
                buffer.bytes[buffer.Pointer] = (byte)Variant.Type.Int;
                buffer.Pointer += 1;
            }

            Array.Resize(ref buffer.bytes, buffer.bytes.Length + 8);
            byte[] floatBytes = BitConverter.GetBytes(varVal);
            Array.Copy(floatBytes, 0, buffer.bytes, buffer.Pointer, 8);
            buffer.Pointer += 8;
        }

        public static void Pack(HLBuffer buffer, bool varVal, bool packType = false)
        {
            if (packType)
            {
                Array.Resize(ref buffer.bytes, buffer.bytes.Length + 1);
                buffer.bytes[buffer.Pointer] = (byte)Variant.Type.Bool;
                buffer.Pointer += 1;
            }

            Array.Resize(ref buffer.bytes, buffer.bytes.Length + 1);
            buffer.bytes[buffer.Pointer] = (byte)(varVal ? 1 : 0);
            buffer.Pointer += 1;
        }

        public static void PackArray(HLBuffer buffer, Godot.Collections.Array varVal, bool packType = false)
        {
            if (packType)
            {
                Array.Resize(ref buffer.bytes, buffer.bytes.Length + 1);
                buffer.bytes[buffer.Pointer] = (byte)Variant.Type.Array;
                buffer.Pointer += 1;
            }

            Array.Resize(ref buffer.bytes, buffer.bytes.Length + 1);
            buffer.bytes[buffer.Pointer] = (byte)varVal.Count;
            buffer.Pointer += 1;
            foreach (var val in varVal)
            {
                PackVariant(buffer, val, packType: true);
            }
        }

        public static void PackDictionary(HLBuffer buffer, Godot.Collections.Dictionary varVal, bool packType = false) {
            if (packType)
            {
                Array.Resize(ref buffer.bytes, buffer.bytes.Length + 1);
                buffer.bytes[buffer.Pointer] = (byte)Variant.Type.Dictionary;
                buffer.Pointer += 1;
            }

            Array.Resize(ref buffer.bytes, buffer.bytes.Length + 1);
            buffer.bytes[buffer.Pointer] = (byte)varVal.Count;
            buffer.Pointer += 1;
            var packedType = false;
            foreach (var key in varVal.Keys)
            {
                PackVariant(buffer, key, packType: !packedType);
                PackVariant(buffer, varVal[key], packType: !packedType);
                packedType = true;
            }
        }

        public static void Pack(HLBuffer buffer, int[] varVal, bool packType = false)
        {
            if (packType)
            {
                Array.Resize(ref buffer.bytes, buffer.bytes.Length + 1);
                buffer.bytes[buffer.Pointer] = (byte)Variant.Type.PackedInt32Array;
                buffer.Pointer += 1;
            }
            Array.Resize(ref buffer.bytes, buffer.bytes.Length + 4);
            byte[] bytes = BitConverter.GetBytes(varVal.Length);
            Array.Copy(bytes, 0, buffer.bytes, buffer.Pointer, 4);
            buffer.Pointer += 4;
            foreach (var val in varVal)
            {
                Array.Resize(ref buffer.bytes, buffer.bytes.Length + 4);
                bytes = BitConverter.GetBytes(val);
                Array.Copy(bytes, 0, buffer.bytes, buffer.Pointer, 4);
                buffer.Pointer += 4;
            }
        }

        public static void Pack(HLBuffer buffer, long[] varVal, bool packType = false)
        {
            if (packType)
            {
                Array.Resize(ref buffer.bytes, buffer.bytes.Length + 1);
                buffer.bytes[buffer.Pointer] = (byte)Variant.Type.PackedInt64Array;
                buffer.Pointer += 1;
            }
            Pack(buffer, varVal.Length);
            foreach (var val in varVal)
            {
                Pack(buffer, val);
            }
        }


        public static void Pack(HLBuffer buffer, byte[] varVal, bool packLength = false, bool packType = false)
        {
            if (packType)
            {
                Array.Resize(ref buffer.bytes, buffer.bytes.Length + 1);
                buffer.bytes[buffer.Pointer] = (byte)Variant.Type.PackedInt32Array;
                buffer.Pointer += 1;
            }
            if (packLength)
            {
                Pack(buffer, varVal.Length);
            }
            Array.Resize(ref buffer.bytes, buffer.bytes.Length + varVal.Length);
            Array.Copy(varVal, 0, buffer.bytes, buffer.Pointer, varVal.Length);
            buffer.Pointer += varVal.Length;
        }

        public static void Pack(HLBuffer buffer, HLBuffer varVal, bool packLength = false, bool packType = false)
        {
            Pack(buffer, varVal.bytes, packLength, packType);
        }

        public static void Pack(HLBuffer buffer, string varVal, bool packType = false)
        {
            if (packType)
            {
                Array.Resize(ref buffer.bytes, buffer.bytes.Length + 1);
                buffer.bytes[buffer.Pointer] = (byte)Variant.Type.String;
                buffer.Pointer += 1;
            }
            Pack(buffer, varVal.Length);
            Array.Resize(ref buffer.bytes, buffer.bytes.Length + varVal.Length);
            Array.Copy(System.Text.Encoding.UTF8.GetBytes(varVal), 0, buffer.bytes, buffer.Pointer, varVal.Length);
            buffer.Pointer += varVal.Length;
        }

        public static Variant? UnpackVariant(HLBuffer buffer, int length = 0, Variant.Type? knownType = null)
        {
            Variant.Type type;
            if (knownType.HasValue)
            {
                type = knownType.Value;
            }
            else
            {
                type = (Variant.Type)buffer.bytes[buffer.Pointer];
                buffer.Pointer += 1;
            }
            if (type == Variant.Type.Vector3)
            {
                return UnpackVector3(buffer);
            }
            else if (type == Variant.Type.Vector2)
            {
                return UnpackVector2(buffer);
            }
            else if (type == Variant.Type.Quaternion)
            {
                return UnpackQuaternion(buffer);
            }
            else if (type == Variant.Type.Float)
            {
                return UnpackFloat(buffer);
            }
            else if (type == Variant.Type.Int)
            {
                return UnpackInt64(buffer);
            }
            else if (type == Variant.Type.Bool)
            {
                return UnpackBool(buffer);
            }
            else if (type == Variant.Type.Array)
            {
                return UnpackArray(buffer);
            }
            else if (type == Variant.Type.Dictionary)
            {
                return UnpackDictionary(buffer);
            }
            else if (type == Variant.Type.PackedByteArray)
            {
                return UnpackByteArray(buffer, length);
            }
            else if (type == Variant.Type.PackedInt32Array)
            {
                return UnpackInt32Array(buffer);
            }
            else if (type == Variant.Type.PackedInt64Array)
            {
                return UnpackInt64Array(buffer);
            }
            else if (type == Variant.Type.String)
            {
                return UnpackString(buffer);
            }
            else
            {
                Debugger.Instance.Log($"HLBytes.UnpackVariant: Unhandled type: {type}", Debugger.DebugLevel.ERROR);
                return null;
            }
        }

        public static Vector2 UnpackVector2(HLBuffer buffer)
        {
            var result = new Vector2();
            var bytes = new byte[4];
            Array.Copy(buffer.bytes, buffer.Pointer, bytes, 0, 4);
            result.X = (float)BitConverter.ToHalf(bytes, 0);
            buffer.Pointer += 2;
            result.Y = (float)BitConverter.ToHalf(bytes, 2);
            buffer.Pointer += 2;
            return result;
        }

        public static Vector3 UnpackVector3(HLBuffer buffer)
        {
            var result = new Vector3();
            var bytes = new byte[12];
            Array.Copy(buffer.bytes, buffer.Pointer, bytes, 0, 12);
            result.X = BitConverter.ToSingle(bytes, 0);
            buffer.Pointer += 4;
            result.Y = BitConverter.ToSingle(bytes, 4);
            buffer.Pointer += 4;
            result.Z = BitConverter.ToSingle(bytes, 8);
            buffer.Pointer += 4;
            return result;
        }

        public static Quaternion UnpackQuaternion(HLBuffer buffer)
        {
            var result = new Quaternion();
            var bytes = new byte[8];
            Array.Copy(buffer.bytes, buffer.Pointer, bytes, 0, 8);
            result.X = (float)BitConverter.ToHalf(bytes, 0);
            buffer.Pointer += 2;
            result.Y = (float)BitConverter.ToHalf(bytes, 2);
            buffer.Pointer += 2;
            result.Y = (float)BitConverter.ToHalf(bytes, 4);
            buffer.Pointer += 2;
            result.Y = (float)BitConverter.ToHalf(bytes, 6);
            buffer.Pointer += 2;
            return result;
        }

        public static float UnpackFloat(HLBuffer buffer)
        {
            var bytes = new byte[4];
            Array.Copy(buffer.bytes, buffer.Pointer, bytes, 0, 4);
            buffer.Pointer += 4;
            return BitConverter.ToSingle(bytes, 0);
        }

        /// <summary>
        /// Alias for <see cref="UnpackInt8(HLBuffer)"/>
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        public static byte UnpackByte(HLBuffer buffer)
        {
            return UnpackInt8(buffer);
        }
        public static byte UnpackInt8(HLBuffer buffer)
        {
            byte int8 = buffer.bytes[buffer.Pointer];
            buffer.Pointer += 1;
            return int8;
        }
        public static int UnpackInt32(HLBuffer buffer)
        {
            var bytes = new byte[4];
            Array.Copy(buffer.bytes, buffer.Pointer, bytes, 0, 4);
            buffer.Pointer += 4;
            return BitConverter.ToInt32(bytes, 0);
        }

        public static short UnpackInt16(HLBuffer buffer)
        {
            var bytes = new byte[2];
            Array.Copy(buffer.bytes, buffer.Pointer, bytes, 0, 2);
            buffer.Pointer += 2;
            return BitConverter.ToInt16(bytes, 0);
        }

        public static long UnpackInt64(HLBuffer buffer)
        {
            var bytes = new byte[8];
            Array.Copy(buffer.bytes, buffer.Pointer, bytes, 0, 8);
            buffer.Pointer += 8;
            var result = BitConverter.ToInt64(bytes, 0);
            return result;
        }

        public static bool UnpackBool(HLBuffer buffer)
        {
            var result = buffer.bytes[buffer.Pointer] == 1;
            buffer.Pointer += 1;
            return result;
        }

        public static Godot.Collections.Array UnpackArray(HLBuffer buffer)
        {
            var size = buffer.bytes[buffer.Pointer];
            buffer.Pointer += 1;
            var result = new Godot.Collections.Array();
            if (size == 0)
            {
                return result;
            }
            for (int i = 0; i < size; i++)
            {
                var variant = UnpackVariant(buffer);
                if (variant.HasValue)
                {
                    result.Add(variant.Value);
                }
                else
                {
                    Debugger.Instance.Log($"Failed to unpack array element {i}", Debugger.DebugLevel.ERROR);
                }
            }
            return result;
        }

        public static Godot.Collections.Dictionary UnpackDictionary(HLBuffer buffer) {
            var size = buffer.bytes[buffer.Pointer];
            buffer.Pointer += 1;
            var result = new Godot.Collections.Dictionary();
            if (size == 0)
            {
                return result;
            }
            Variant.Type? keyType = null;
            Variant.Type? valueType = null;
            for (int i = 0; i < size; i++)
            {
                var key = UnpackVariant(buffer, knownType: keyType);
                if (keyType == null && key.HasValue)
                {
                    keyType = key.Value.VariantType;
                }
                var value = UnpackVariant(buffer, knownType: valueType);
                if (valueType == null && value.HasValue)
                {
                    valueType = value.Value.VariantType;
                }
                if (key.HasValue && value.HasValue)
                {
                    result.Add(key.Value, value.Value);
                }
                else
                {
                    Debugger.Instance.Log($"Failed to unpack dictionary element {i}", Debugger.DebugLevel.ERROR);
                }
            }
            return result;
        }

        public static int[] UnpackInt32Array(HLBuffer buffer)
        {
            var size = BitConverter.ToInt32(buffer.bytes, buffer.Pointer);
            buffer.Pointer += 4;
            var result = new int[size];
            for (int i = 0; i < size; i++)
            {
                result[i] = BitConverter.ToInt32(buffer.bytes, buffer.Pointer);
                buffer.Pointer += 4;
            }
            return result;
        }

        public static long[] UnpackInt64Array(HLBuffer buffer)
        {
            var size = BitConverter.ToInt32(buffer.bytes, buffer.Pointer);
            buffer.Pointer += 4;
            var result = new long[size];
            for (int i = 0; i < size; i++)
            {
                result[i] = BitConverter.ToInt64(buffer.bytes, buffer.Pointer);
                buffer.Pointer += 8;
            }
            return result;
        }

        public static byte[] UnpackByteArray(HLBuffer buffer, int length = 0, bool untilEnd = false)
        {
            var size = length;
            if (untilEnd)
            {
                size = buffer.bytes.Length - buffer.Pointer;
            } else if (size == 0)
            {
                size = UnpackInt32(buffer);
            }
            var result = new byte[size];
            Array.Copy(buffer.bytes, buffer.Pointer, result, 0, size);
            buffer.Pointer += size;
            return result;
        }

        public static string UnpackString(HLBuffer buffer)
        {
            var size = UnpackInt32(buffer);
            var result = System.Text.Encoding.UTF8.GetString(buffer.bytes, buffer.Pointer, size);
            buffer.Pointer += size;
            return result;
        }
    }
}