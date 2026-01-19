using System;
using System.Linq;
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
