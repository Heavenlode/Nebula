namespace NebulaTests.Core.Serialization;

using GdUnit4;
using static GdUnit4.Assertions;
using Nebula.Serialization;
using Godot;
using MongoDB.Bson;
using System;

[TestSuite]
public class BsonTransformerTests
{
    private BsonTransformer _transformer;

    [Before]
    public void Setup()
    {
        // Create a BsonTransformer instance for testing
        _transformer = new BsonTransformer();
    }

    [After]
    public void Cleanup()
    {
        if (_transformer != null)
        {
            _transformer.Free();
            _transformer = null;
        }
    }

    [TestCase, RequireGodotRuntime]
    public void TestSimple()
    {
        AssertBool(true).IsTrue();
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeDeserialize_String()
    {
        var value = new BsonString("test string");
        
        var bytes = _transformer.SerializeBsonValue(value);
        AssertObject(bytes).IsNotNull();
        AssertInt(bytes.Length).IsGreater(0);
        
        var result = _transformer.DeserializeBsonValue<BsonString>(bytes);
        AssertString(result.AsString).IsEqual("test string");
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeDeserialize_Int32()
    {
        var value = new BsonInt32(42);
        
        var bytes = _transformer.SerializeBsonValue(value);
        var result = _transformer.DeserializeBsonValue<BsonInt32>(bytes);
        
        AssertInt(result.AsInt32).IsEqual(42);
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeDeserialize_Int64()
    {
        var value = new BsonInt64(9876543210L);
        
        var bytes = _transformer.SerializeBsonValue(value);
        var result = _transformer.DeserializeBsonValue<BsonInt64>(bytes);
        
        AssertThat(result.AsInt64).IsEqual(9876543210L);
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeDeserialize_Double()
    {
        var value = new BsonDouble(3.14159);
        
        var bytes = _transformer.SerializeBsonValue(value);
        var result = _transformer.DeserializeBsonValue<BsonDouble>(bytes);
        
        AssertFloat(result.AsDouble).IsEqualApprox(3.14159, 0.00001);
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeDeserialize_Boolean()
    {
        var value = new BsonBoolean(true);
        
        var bytes = _transformer.SerializeBsonValue(value);
        var result = _transformer.DeserializeBsonValue<BsonBoolean>(bytes);
        
        AssertBool(result.AsBoolean).IsTrue();
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeDeserialize_Array()
    {
        var value = new BsonArray { 1, 2, 3, 4, 5 };
        
        var bytes = _transformer.SerializeBsonValue(value);
        var result = _transformer.DeserializeBsonValue<BsonArray>(bytes);
        
        AssertInt(result.Count).IsEqual(5);
        AssertInt(result[0].AsInt32).IsEqual(1);
        AssertInt(result[4].AsInt32).IsEqual(5);
    }

    [TestCase, RequireGodotRuntime]
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
        
        AssertString(result["name"].AsString).IsEqual("Test");
        AssertInt(result["value"].AsInt32).IsEqual(42);
        AssertBool(result["active"].AsBoolean).IsTrue();
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeDeserialize_BinaryData()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var value = new BsonBinaryData(data);
        
        var bytes = _transformer.SerializeBsonValue(value);
        var result = _transformer.DeserializeBsonValue<BsonBinaryData>(bytes);
        
        var resultData = result.AsByteArray;
        AssertInt(resultData.Length).IsEqual(5);
        AssertInt(resultData[0]).IsEqual(1);
        AssertInt(resultData[4]).IsEqual(5);
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeDeserialize_Null()
    {
        var value = BsonNull.Value;
        
        var bytes = _transformer.SerializeBsonValue(value);
        var result = _transformer.DeserializeBsonValue<BsonNull>(bytes);
        
        // BsonNull.Value is a singleton, not actually null
        AssertBool(result.IsBsonNull).IsTrue();
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeVariant_String()
    {
        Variant variant = "test string";
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        AssertString(result.AsString).IsEqual("test string");
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeVariant_Int()
    {
        Variant variant = 42;
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        AssertThat(result.AsInt64).IsEqual(42L);
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeVariant_IntWithSubtype_Byte()
    {
        Variant variant = 255;
        
        var result = _transformer.SerializeVariant(new Variant(), variant, "Byte");
        
        // Should serialize as byte
        AssertObject(result).IsNotNull();
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeVariant_IntWithSubtype_Int()
    {
        Variant variant = 12345;
        
        var result = _transformer.SerializeVariant(new Variant(), variant, "Int");
        
        AssertInt(result.AsInt32).IsEqual(12345);
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeVariant_Float()
    {
        Variant variant = 3.14f;
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        AssertFloat(result.AsDouble).IsEqualApprox(3.14, 0.01);
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeVariant_Bool()
    {
        Variant variant = true;
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        AssertBool(result.AsBoolean).IsTrue();
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeVariant_Vector2()
    {
        Variant variant = new Vector2(1.5f, 2.5f);
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        AssertBool(result.IsBsonArray).IsTrue();
        var array = result.AsBsonArray;
        AssertInt(array.Count).IsEqual(2);
        AssertFloat(array[0].AsDouble).IsEqualApprox(1.5, 0.01);
        AssertFloat(array[1].AsDouble).IsEqualApprox(2.5, 0.01);
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeVariant_Vector3()
    {
        Variant variant = new Vector3(1.0f, 2.0f, 3.0f);
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        AssertBool(result.IsBsonArray).IsTrue();
        var array = result.AsBsonArray;
        AssertInt(array.Count).IsEqual(3);
        AssertFloat(array[0].AsDouble).IsEqualApprox(1.0, 0.01);
        AssertFloat(array[1].AsDouble).IsEqualApprox(2.0, 0.01);
        AssertFloat(array[2].AsDouble).IsEqualApprox(3.0, 0.01);
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeVariant_Nil()
    {
        Variant variant = new Variant();
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        AssertBool(result.IsBsonNull).IsTrue();
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeVariant_PackedByteArray()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        Variant variant = bytes;
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        AssertBool(result.IsBsonBinaryData).IsTrue();
        var binaryData = result.AsBsonBinaryData.AsByteArray;
        AssertInt(binaryData.Length).IsEqual(5);
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeVariant_PackedInt32Array()
    {
        var ints = new int[] { 10, 20, 30 };
        Variant variant = ints;
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        AssertBool(result.IsBsonArray).IsTrue();
        AssertInt(result.AsBsonArray.Count).IsEqual(3);
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeVariant_PackedInt64Array()
    {
        var longs = new long[] { 100L, 200L, 300L };
        Variant variant = longs;
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        AssertBool(result.IsBsonArray).IsTrue();
        AssertInt(result.AsBsonArray.Count).IsEqual(3);
    }

    [TestCase, RequireGodotRuntime]
    public void TestSerializeVariant_Dictionary()
    {
        var dict = new Godot.Collections.Dictionary
        {
            { "name", "Test" },
            { "value", 42 }
        };
        Variant variant = dict;
        
        var result = _transformer.SerializeVariant(new Variant(), variant);
        
        AssertBool(result.IsBsonDocument).IsTrue();
        var doc = result.AsBsonDocument;
        AssertString(doc["name"].AsString).IsEqual("Test");
        AssertThat(doc["value"].AsInt64).IsEqual(42L);
    }

    [TestCase, RequireGodotRuntime]
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
        
        AssertString(result["string"].AsString).IsEqual("test");
        AssertInt(result["int"].AsInt32).IsEqual(42);
        AssertFloat(result["double"].AsDouble).IsEqualApprox(3.14, 0.01);
        AssertBool(result["bool"].AsBoolean).IsTrue();
        AssertInt(result["array"].AsBsonArray.Count).IsEqual(3);
        AssertString(result["nested"].AsBsonDocument["key"].AsString).IsEqual("value");
    }
}

