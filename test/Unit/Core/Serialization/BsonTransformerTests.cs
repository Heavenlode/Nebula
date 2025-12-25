namespace NebulaTests.Unit.Core.Serialization;

using NebulaTests.Unit;
using Xunit;
using Nebula.Serialization;
using Godot;
using MongoDB.Bson;
using System;

public class BsonTransformerTests : IDisposable
{
    private BsonTransformer _transformer;

    public BsonTransformerTests()
    {
        _transformer = new BsonTransformer();
    }

    public void Dispose()
    {
            _transformer = null;
    }

    [GodotFact]
    public void TestSimple()
    {
        Assert.True(true);
    }

    [GodotFact]
    public void TestSerializeDeserialize_String()
    {
        var value = new BsonString("test string");
        
        var bytes = _transformer.SerializeBsonValue(value);
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        
        var result = _transformer.DeserializeBsonValue<BsonString>(bytes);
        Assert.Equal("test string", result.AsString);
    }

    [GodotFact]
    public void TestSerializeDeserialize_Int32()
    {
        var value = new BsonInt32(42);
        
        var bytes = _transformer.SerializeBsonValue(value);
        var result = _transformer.DeserializeBsonValue<BsonInt32>(bytes);
        
        Assert.Equal(42, result.AsInt32);
    }

    [GodotFact]
    public void TestSerializeDeserialize_Int64()
    {
        var value = new BsonInt64(9876543210L);
        
        var bytes = _transformer.SerializeBsonValue(value);
        var result = _transformer.DeserializeBsonValue<BsonInt64>(bytes);
        
        Assert.Equal(9876543210L, result.AsInt64);
    }

    [GodotFact]
    public void TestSerializeDeserialize_Double()
    {
        var value = new BsonDouble(3.14159);
        
        var bytes = _transformer.SerializeBsonValue(value);
        var result = _transformer.DeserializeBsonValue<BsonDouble>(bytes);
        
        Assert.True(Math.Abs(result.AsDouble - 3.14159) < 0.00001);
    }

    [GodotFact]
    public void TestSerializeDeserialize_Boolean()
    {
        var value = new BsonBoolean(true);
        
        var bytes = _transformer.SerializeBsonValue(value);
        var result = _transformer.DeserializeBsonValue<BsonBoolean>(bytes);
        
        Assert.True(result.AsBoolean);
    }

    [GodotFact]
    public void TestSerializeDeserialize_Array()
    {
        var value = new BsonArray { 1, 2, 3, 4, 5 };
        
        var bytes = _transformer.SerializeBsonValue(value);
        var result = _transformer.DeserializeBsonValue<BsonArray>(bytes);
        
        Assert.Equal(5, result.Count);
        Assert.Equal(1, result[0].AsInt32);
        Assert.Equal(5, result[4].AsInt32);
    }

    [GodotFact]
    public void TestSerializeDeserialize_Document()
    {
        var value = new BsonDocument
        {
            { "name", "Test" },
            { "value", 42 },
            { "active", true }
        };
        
        var bytes = _transformer.SerializeBsonValue(value);
        var result = _transformer.DeserializeBsonValue<BsonDocument>(bytes);
        
        Assert.Equal("Test", result["name"].AsString);
        Assert.Equal(42, result["value"].AsInt32);
        Assert.True(result["active"].AsBoolean);
    }

    [GodotFact]
    public void TestSerializeDeserialize_BinaryData()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var value = new BsonBinaryData(data);
        
        var bytes = _transformer.SerializeBsonValue(value);
        var result = _transformer.DeserializeBsonValue<BsonBinaryData>(bytes);
        
        var resultData = result.AsByteArray;
        Assert.Equal(5, resultData.Length);
        Assert.Equal(1, resultData[0]);
        Assert.Equal(5, resultData[4]);
    }

    [GodotFact]
    public void TestSerializeDeserialize_Null()
    {
        var value = BsonNull.Value;
        
        var bytes = _transformer.SerializeBsonValue(value);
        var result = _transformer.DeserializeBsonValue<BsonNull>(bytes);
        
        Assert.True(result.IsBsonNull);
    }

    [GodotFact]
    public void TestSerializeVariant_String()
    {
        Variant variant = "test string";
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        Assert.Equal("test string", result.AsString);
    }

    [GodotFact]
    public void TestSerializeVariant_Int()
    {
        Variant variant = 42;
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        Assert.Equal(42L, result.AsInt64);
    }

    [GodotFact]
    public void TestSerializeVariant_IntWithSubtype_Byte()
    {
        Variant variant = 255;
        
        var result = _transformer.SerializeVariant(new Variant(), variant, "Byte");
        
        Assert.NotNull(result);
    }

    [GodotFact]
    public void TestSerializeVariant_IntWithSubtype_Int()
    {
        Variant variant = 12345;
        
        var result = _transformer.SerializeVariant(new Variant(), variant, "Int");
        
        Assert.Equal(12345, result.AsInt32);
    }

    [GodotFact]
    public void TestSerializeVariant_Float()
    {
        Variant variant = 3.14f;
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        Assert.True(Math.Abs(result.AsDouble - 3.14) < 0.01);
    }

    [GodotFact]
    public void TestSerializeVariant_Bool()
    {
        Variant variant = true;
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        Assert.True(result.AsBoolean);
    }

    [GodotFact]
    public void TestSerializeVariant_Vector2()
    {
        Variant variant = new Vector2(1.5f, 2.5f);
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        Assert.True(result.IsBsonArray);
        var array = result.AsBsonArray;
        Assert.Equal(2, array.Count);
        Assert.True(Math.Abs(array[0].AsDouble - 1.5) < 0.01);
        Assert.True(Math.Abs(array[1].AsDouble - 2.5) < 0.01);
    }

    [GodotFact]
    public void TestSerializeVariant_Vector3()
    {
        Variant variant = new Vector3(1.0f, 2.0f, 3.0f);
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        Assert.True(result.IsBsonArray);
        var array = result.AsBsonArray;
        Assert.Equal(3, array.Count);
        Assert.True(Math.Abs(array[0].AsDouble - 1.0) < 0.01);
        Assert.True(Math.Abs(array[1].AsDouble - 2.0) < 0.01);
        Assert.True(Math.Abs(array[2].AsDouble - 3.0) < 0.01);
    }

    [GodotFact]
    public void TestSerializeVariant_Nil()
    {
        Variant variant = new Variant();
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        Assert.True(result.IsBsonNull);
    }

    [GodotFact]
    public void TestSerializeVariant_PackedByteArray()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        Variant variant = bytes;
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        Assert.True(result.IsBsonBinaryData);
        var binaryData = result.AsBsonBinaryData.AsByteArray;
        Assert.Equal(5, binaryData.Length);
    }

    [GodotFact]
    public void TestSerializeVariant_PackedInt32Array()
    {
        var ints = new int[] { 10, 20, 30 };
        Variant variant = ints;
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        Assert.True(result.IsBsonArray);
        Assert.Equal(3, result.AsBsonArray.Count);
    }

    [GodotFact]
    public void TestSerializeVariant_PackedInt64Array()
    {
        var longs = new long[] { 100L, 200L, 300L };
        Variant variant = longs;
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        Assert.True(result.IsBsonArray);
        Assert.Equal(3, result.AsBsonArray.Count);
    }

    [GodotFact]
    public void TestSerializeVariant_Dictionary()
    {
        var dict = new Godot.Collections.Dictionary
        {
            { "name", "Test" },
            { "value", 42 }
        };
        Variant variant = dict;
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        Assert.True(result.IsBsonDocument);
        var doc = result.AsBsonDocument;
        Assert.Equal("Test", doc["name"].AsString);
        Assert.Equal(42L, doc["value"].AsInt64);
    }

    [GodotFact]
    public void TestSerializeDeserialize_ComplexDocument()
    {
        var value = new BsonDocument
        {
            { "string", "test" },
            { "int", 42 },
            { "double", 3.14 },
            { "bool", true },
            { "array", new BsonArray { 1, 2, 3 } },
            { "nested", new BsonDocument { { "key", "value" } } }
        };
        
        var bytes = _transformer.SerializeBsonValue(value);
        var result = _transformer.DeserializeBsonValue<BsonDocument>(bytes);
        
        Assert.Equal("test", result["string"].AsString);
        Assert.Equal(42, result["int"].AsInt32);
        Assert.True(Math.Abs(result["double"].AsDouble - 3.14) < 0.01);
        Assert.True(result["bool"].AsBoolean);
        Assert.Equal(3, result["array"].AsBsonArray.Count);
        Assert.Equal("value", result["nested"].AsBsonDocument["key"].AsString);
    }
}
