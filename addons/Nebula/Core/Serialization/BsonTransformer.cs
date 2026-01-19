using System;
using System.IO;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace Nebula.Serialization
{
    /// <summary>
    /// Static utility class for BSON byte serialization/deserialization.
    /// Type-to-BSON conversion is now handled by BsonTypeHelper and generated code.
    /// </summary>
    public static class BsonTransformer
    {
        public static byte[] SerializeBsonValue(BsonValue value)
        {
            var wrapper = new BsonDocument("value", value);

            using (var memoryStream = new MemoryStream())
            {
                using (var writer = new BsonBinaryWriter(memoryStream))
                {
                    BsonSerializer.Serialize(writer, typeof(BsonDocument), wrapper);
                }
                return memoryStream.ToArray();
            }
        }

        public static T DeserializeBsonValue<T>(byte[] bytes) where T : BsonValue
        {
            using (var memoryStream = new MemoryStream(bytes))
            {
                using (var reader = new BsonBinaryReader(memoryStream))
                {
                    var wrapper = BsonSerializer.Deserialize<BsonDocument>(reader);
                    BsonValue value = wrapper["value"];

                    if (typeof(T) == typeof(BsonValue))
                    {
                        // If requesting base BsonValue type, return as is
                        return (T)value;
                    }

                    // Check if the actual type matches the requested type
                    if (IsCompatibleType<T>(value))
                    {
                        // Convert to the requested type
                        return ConvertToType<T>(value);
                    }

                    if (value.BsonType == BsonType.Null)
                    {
                        return null;
                    }

                    throw new InvalidCastException(
                        $"Cannot convert BsonValue of type {value.BsonType} to {typeof(T).Name}: {value.ToJson()}");
                }
            }
        }

        private static bool IsCompatibleType<T>(BsonValue value) where T : BsonValue
        {
            if (typeof(T) == typeof(BsonDocument))
                return value.IsBsonDocument;
            else if (typeof(T) == typeof(BsonBinaryData))
                return value.IsBsonBinaryData;
            else if (typeof(T) == typeof(BsonString))
                return value.IsString;
            else if (typeof(T) == typeof(BsonInt32))
                return value.IsInt32;
            else if (typeof(T) == typeof(BsonInt64))
                return value.IsInt64;
            else if (typeof(T) == typeof(BsonDouble))
                return value.IsDouble;
            else if (typeof(T) == typeof(BsonBoolean))
                return value.IsBoolean;
            else if (typeof(T) == typeof(BsonDateTime))
                return value.IsBsonDateTime;
            else if (typeof(T) == typeof(BsonArray))
                return value.IsBsonArray;
            else if (typeof(T) == typeof(BsonObjectId))
                return value.IsObjectId;
            else if (typeof(T) == typeof(BsonNull))
                return value.IsBsonNull;
            // Add other types as needed

            return false;
        }

        private static T ConvertToType<T>(BsonValue value) where T : BsonValue
        {
            if (typeof(T) == typeof(BsonDocument))
                return (T)(BsonValue)value.AsBsonDocument;
            else if (typeof(T) == typeof(BsonBinaryData))
                return (T)(BsonValue)value.AsBsonBinaryData;
            else if (typeof(T) == typeof(BsonString))
                return (T)(BsonValue)value.AsString;
            else if (typeof(T) == typeof(BsonInt32))
                return (T)(BsonValue)value.AsInt32;
            else if (typeof(T) == typeof(BsonInt64))
                return (T)(BsonValue)value.AsInt64;
            else if (typeof(T) == typeof(BsonDouble))
                return (T)(BsonValue)value.AsDouble;
            else if (typeof(T) == typeof(BsonBoolean))
                return (T)(BsonValue)value.AsBoolean;
            else if (typeof(T) == typeof(BsonDateTime))
                return (T)(BsonValue)value.AsBsonDateTime;
            else if (typeof(T) == typeof(BsonArray))
                return (T)(BsonValue)value.AsBsonArray;
            else if (typeof(T) == typeof(BsonObjectId))
                return (T)(BsonValue)value.AsObjectId;
            else if (typeof(T) == typeof(BsonNull))
                return (T)(BsonValue)value.AsBsonNull;

            throw new InvalidCastException(
                $"Conversion from {value.BsonType} to {typeof(T).Name} is not implemented");
        }
    }
}
