using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Nebula.Serialization;
using Nebula.Utility.Tools;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace Nebula.Serialization
{
    public partial class BsonTransformer : Node
    {

        /// <summary>
        /// The singleton instance.
        /// </summary>
        public static BsonTransformer Instance { get; internal set; }

        /// <inheritdoc/>
        public override void _EnterTree()
        {
            if (Instance != null)
            {
                QueueFree();
            }
            Instance = this;
        }

        public byte[] SerializeBsonValue(BsonValue value)
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

        public T DeserializeBsonValue<T>(byte[] bytes) where T : BsonValue
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

        private bool IsCompatibleType<T>(BsonValue value) where T : BsonValue
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

        private T ConvertToType<T>(BsonValue value) where T : BsonValue
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

        public BsonValue SerializeVariant(Variant context, Variant variant, string subtype = "None")
        {
            if (variant.VariantType == Variant.Type.String)
            {
                return variant.ToString();
            }
            else if (variant.VariantType == Variant.Type.Float)
            {
                return variant.AsDouble();
            }
            else if (variant.VariantType == Variant.Type.Int)
            {
                if (subtype == "Byte")
                {
                    return variant.AsByte();
                }
                else if (subtype == "Int")
                {
                    return variant.AsInt32();
                }
                else
                {
                    return variant.AsInt64();
                }
            }
            else if (variant.VariantType == Variant.Type.Bool)
            {
                return variant.AsBool();
            }
            else if (variant.VariantType == Variant.Type.Vector2)
            {
                var vec = variant.As<Vector2>();
                return new BsonArray { vec.X, vec.Y };
            }
            else if (variant.VariantType == Variant.Type.Vector3)
            {
                var vec = variant.As<Vector3>();
                return new BsonArray { vec.X, vec.Y, vec.Z };
            }
            else if (variant.VariantType == Variant.Type.Nil)
            {
                return BsonNull.Value;
            }
            else if (variant.VariantType == Variant.Type.Object)
            {
                var obj = variant.As<GodotObject>();
                if (obj == null)
                {
                    return BsonNull.Value;
                }
                else
                {
                    if (obj is IBsonSerializableBase bsonSerializable)
                    {
                        return bsonSerializable.BsonSerialize(context);
                    }

                    Debugger.Instance.Log($"Attempting to serialize an object that does not implement IBsonSerializable<T>: {obj}", Debugger.DebugLevel.ERROR);
                    return null;
                }
            }
            else if (variant.VariantType == Variant.Type.PackedByteArray)
            {
                return new BsonBinaryData(variant.AsByteArray(), BsonBinarySubType.Binary);
            }
            else if (variant.VariantType == Variant.Type.PackedInt32Array)
            {
                return new BsonArray(variant.AsInt32Array());
            }
            else if (variant.VariantType == Variant.Type.PackedInt64Array)
            {
                return new BsonArray(variant.AsInt64Array());
            }
            else if (variant.VariantType == Variant.Type.Dictionary)
            {
                var dict = variant.AsGodotDictionary();
                var bsonDict = new BsonDocument();
                foreach (var key in dict.Keys)
                {
                    bsonDict[key.ToString()] = SerializeVariant(context, dict[key]);
                }
                return bsonDict;
            }
            else
            {
                Debugger.Instance.Log($"Serializing to JSON unsupported property type: {variant.VariantType}", Debugger.DebugLevel.ERROR);
                return null;
            }
        }
    }
}