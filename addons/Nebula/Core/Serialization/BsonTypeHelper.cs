using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using MongoDB.Bson;

namespace Nebula.Serialization
{
    /// <summary>
    /// Static helper for converting C# types to/from BSON values.
    /// Replaces the Godot Variant-based SerializeVariant approach with direct type handling.
    /// </summary>
    public static class BsonTypeHelper
    {
        #region Serialization (C# -> BSON)

        public static BsonValue ToBson(string value) => value != null ? (BsonValue)value : BsonNull.Value;
        
        public static BsonValue ToBson(bool value) => value;
        
        public static BsonValue ToBson(byte value) => (int)value;
        
        public static BsonValue ToBson(short value) => (int)value;
        
        public static BsonValue ToBson(int value) => value;
        
        public static BsonValue ToBson(long value) => value;
        
        public static BsonValue ToBson(ulong value) => (long)value;
        
        public static BsonValue ToBson(float value) => (double)value;
        
        public static BsonValue ToBson(double value) => value;
        
        public static BsonValue ToBson(Vector2 value) => new BsonArray { value.X, value.Y };
        
        public static BsonValue ToBson(Vector2I value) => new BsonArray { value.X, value.Y };
        
        public static BsonValue ToBson(Vector3 value) => new BsonArray { value.X, value.Y, value.Z };
        
        public static BsonValue ToBson(Vector3I value) => new BsonArray { value.X, value.Y, value.Z };
        
        public static BsonValue ToBson(Vector4 value) => new BsonArray { value.X, value.Y, value.Z, value.W };
        
        public static BsonValue ToBson(Quaternion value) => new BsonArray { value.X, value.Y, value.Z, value.W };
        
        public static BsonValue ToBson(Color value) => new BsonArray { value.R, value.G, value.B, value.A };
        
        public static BsonValue ToBson(byte[] value) => value != null 
            ? new BsonBinaryData(value, BsonBinarySubType.Binary) 
            : BsonNull.Value;
        
        public static BsonValue ToBson(int[] value) => value != null 
            ? new BsonArray(value) 
            : BsonNull.Value;
        
        public static BsonValue ToBson(long[] value) => value != null 
            ? new BsonArray(value) 
            : BsonNull.Value;

        /// <summary>
        /// Serializes a NetArray to BSON as an array of its elements.
        /// </summary>
        public static BsonValue ToBson<T>(NetArray<T> value) where T : struct
        {
            if (value == null || value.Length == 0)
                return BsonNull.Value;
            
            var arr = new BsonArray();
            for (int i = 0; i < value.Length; i++)
            {
                arr.Add(ElementToBson(value[i]));
            }
            return arr;
        }
        
        /// <summary>
        /// Helper to convert individual NetArray elements to BSON.
        /// </summary>
        private static BsonValue ElementToBson<T>(T value) where T : struct
        {
            return value switch
            {
                int i => i,
                float f => (double)f,
                byte b => (int)b,
                long l => l,
                short s => (int)s,
                Vector2 v2 => new BsonArray { v2.X, v2.Y },
                Vector3 v3 => new BsonArray { v3.X, v3.Y, v3.Z },
                Quaternion q => new BsonArray { q.X, q.Y, q.Z, q.W },
                Vector2I v2i => new BsonArray { v2i.X, v2i.Y },
                Vector3I v3i => new BsonArray { v3i.X, v3i.Y, v3i.Z },
                Color c => new BsonArray { c.R, c.G, c.B, c.A },
                _ => throw new NotSupportedException($"NetArray element type {typeof(T).Name} is not supported for BSON serialization")
            };
        }

        /// <summary>
        /// Serializes an enum value to BSON as its underlying integer value.
        /// </summary>
        public static BsonValue ToBsonEnum<T>(T value) where T : struct, Enum
        {
            return Convert.ToInt32(value);
        }

        /// <summary>
        /// Serializes an IBsonSerializableBase object to BSON.
        /// </summary>
        public static BsonValue ToBson(IBsonSerializableBase value, NetBsonContext context = default)
        {
            return value?.BsonSerialize(context) ?? BsonNull.Value;
        }

        /// <summary>
        /// Serializes an IBsonValue struct to BSON.
        /// </summary>
        public static BsonValue ToBsonValue<T>(in T value) where T : struct, IBsonValue<T>
        {
            return T.BsonSerialize(in value);
        }

        #endregion

        #region Deserialization (BSON -> C#)

        public static string ToString(BsonValue value) => value.IsBsonNull ? null : value.AsString;
        
        public static bool ToBool(BsonValue value) => value.AsBoolean;
        
        public static byte ToByte(BsonValue value) => (byte)value.AsInt32;
        
        public static short ToShort(BsonValue value) => (short)value.AsInt32;
        
        public static int ToInt(BsonValue value) => value.AsInt32;
        
        public static long ToLong(BsonValue value) => value.AsInt64;
        
        public static ulong ToULong(BsonValue value) => (ulong)value.AsInt64;
        
        public static float ToFloat(BsonValue value) => (float)value.AsDouble;
        
        public static double ToDouble(BsonValue value) => value.AsDouble;

        public static Vector2 ToVector2(BsonValue value)
        {
            var arr = value.AsBsonArray;
            return new Vector2((float)arr[0].AsDouble, (float)arr[1].AsDouble);
        }

        public static Vector2I ToVector2I(BsonValue value)
        {
            var arr = value.AsBsonArray;
            return new Vector2I(arr[0].AsInt32, arr[1].AsInt32);
        }

        public static Vector3 ToVector3(BsonValue value)
        {
            var arr = value.AsBsonArray;
            return new Vector3((float)arr[0].AsDouble, (float)arr[1].AsDouble, (float)arr[2].AsDouble);
        }

        public static Vector3I ToVector3I(BsonValue value)
        {
            var arr = value.AsBsonArray;
            return new Vector3I(arr[0].AsInt32, arr[1].AsInt32, arr[2].AsInt32);
        }

        public static Vector4 ToVector4(BsonValue value)
        {
            var arr = value.AsBsonArray;
            return new Vector4((float)arr[0].AsDouble, (float)arr[1].AsDouble, (float)arr[2].AsDouble, (float)arr[3].AsDouble);
        }

        public static Quaternion ToQuaternion(BsonValue value)
        {
            var arr = value.AsBsonArray;
            return new Quaternion((float)arr[0].AsDouble, (float)arr[1].AsDouble, (float)arr[2].AsDouble, (float)arr[3].AsDouble);
        }

        public static Color ToColor(BsonValue value)
        {
            var arr = value.AsBsonArray;
            return new Color((float)arr[0].AsDouble, (float)arr[1].AsDouble, (float)arr[2].AsDouble, (float)arr[3].AsDouble);
        }

        public static byte[] ToByteArray(BsonValue value) => value.IsBsonNull ? null : value.AsByteArray;

        public static int[] ToInt32Array(BsonValue value) => value.IsBsonNull 
            ? null 
            : value.AsBsonArray.Select(x => x.AsInt32).ToArray();

        public static long[] ToInt64Array(BsonValue value) => value.IsBsonNull 
            ? null 
            : value.AsBsonArray.Select(x => x.AsInt64).ToArray();

        /// <summary>
        /// Deserializes a BSON array to a NetArray.
        /// </summary>
        public static NetArray<T> ToNetArray<T>(BsonValue value) where T : struct
        {
            if (value.IsBsonNull)
                return null;
            
            var bsonArray = value.AsBsonArray;
            var result = new NetArray<T>(bsonArray.Count);
            result.SetLength(bsonArray.Count);
            
            for (int i = 0; i < bsonArray.Count; i++)
            {
                result.SetFromNetwork(i, BsonToElement<T>(bsonArray[i]));
            }
            
            return result;
        }
        
        /// <summary>
        /// Helper to convert BSON to individual NetArray elements.
        /// </summary>
        private static T BsonToElement<T>(BsonValue value) where T : struct
        {
            if (typeof(T) == typeof(int))
            {
                var v = value.AsInt32;
                return Unsafe.As<int, T>(ref v);
            }
            if (typeof(T) == typeof(float))
            {
                var v = (float)value.AsDouble;
                return Unsafe.As<float, T>(ref v);
            }
            if (typeof(T) == typeof(byte))
            {
                var v = (byte)value.AsInt32;
                return Unsafe.As<byte, T>(ref v);
            }
            if (typeof(T) == typeof(long))
            {
                var v = value.AsInt64;
                return Unsafe.As<long, T>(ref v);
            }
            if (typeof(T) == typeof(short))
            {
                var v = (short)value.AsInt32;
                return Unsafe.As<short, T>(ref v);
            }
            if (typeof(T) == typeof(Vector2))
            {
                var arr = value.AsBsonArray;
                var v = new Vector2((float)arr[0].AsDouble, (float)arr[1].AsDouble);
                return Unsafe.As<Vector2, T>(ref v);
            }
            if (typeof(T) == typeof(Vector3))
            {
                var arr = value.AsBsonArray;
                var v = new Vector3((float)arr[0].AsDouble, (float)arr[1].AsDouble, (float)arr[2].AsDouble);
                return Unsafe.As<Vector3, T>(ref v);
            }
            if (typeof(T) == typeof(Quaternion))
            {
                var arr = value.AsBsonArray;
                var v = new Quaternion((float)arr[0].AsDouble, (float)arr[1].AsDouble, (float)arr[2].AsDouble, (float)arr[3].AsDouble);
                return Unsafe.As<Quaternion, T>(ref v);
            }
            if (typeof(T) == typeof(Vector2I))
            {
                var arr = value.AsBsonArray;
                var v = new Vector2I(arr[0].AsInt32, arr[1].AsInt32);
                return Unsafe.As<Vector2I, T>(ref v);
            }
            if (typeof(T) == typeof(Vector3I))
            {
                var arr = value.AsBsonArray;
                var v = new Vector3I(arr[0].AsInt32, arr[1].AsInt32, arr[2].AsInt32);
                return Unsafe.As<Vector3I, T>(ref v);
            }
            if (typeof(T) == typeof(Color))
            {
                var arr = value.AsBsonArray;
                var v = new Color((float)arr[0].AsDouble, (float)arr[1].AsDouble, (float)arr[2].AsDouble, (float)arr[3].AsDouble);
                return Unsafe.As<Color, T>(ref v);
            }
            
            throw new NotSupportedException($"NetArray element type {typeof(T).Name} is not supported for BSON deserialization");
        }

        /// <summary>
        /// Deserializes a BSON value to an enum.
        /// </summary>
        public static T ToEnum<T>(BsonValue value) where T : struct, Enum
        {
            return (T)Enum.ToObject(typeof(T), value.AsInt32);
        }

        /// <summary>
        /// Deserializes a BSON value using IBsonValue static method.
        /// </summary>
        public static T FromBsonValue<T>(BsonValue value) where T : struct, IBsonValue<T>
        {
            return T.BsonDeserialize(value);
        }

        #endregion
    }
}
