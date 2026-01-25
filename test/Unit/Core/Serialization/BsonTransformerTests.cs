namespace NebulaTests.Unit.Core.Serialization;

using Nebula.Testing.Unit;
using Xunit;
using Nebula.Serialization;
using Godot;
using MongoDB.Bson;
using System;

[NebulaUnitTest]
public class BsonTransformerTests
{
    [NebulaUnitTest]
    public void TestSerializeDeserialize_String()
    {
        var value = new BsonString("test string");
        
        var bytes = BsonTransformer.SerializeBsonValue(value);
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        
        var result = BsonTransformer.DeserializeBsonValue<BsonString>(bytes);
        Assert.Equal("test string", result.AsString);
    }

    [NebulaUnitTest]
    public void TestSerializeDeserialize_Int32()
    {
        var value = new BsonInt32(42);
        
        var bytes = BsonTransformer.SerializeBsonValue(value);
        var result = BsonTransformer.DeserializeBsonValue<BsonInt32>(bytes);
        
        Assert.Equal(42, result.AsInt32);
    }

    [NebulaUnitTest]
    public void TestSerializeDeserialize_Int64()
    {
        var value = new BsonInt64(9876543210L);
        
        var bytes = BsonTransformer.SerializeBsonValue(value);
        var result = BsonTransformer.DeserializeBsonValue<BsonInt64>(bytes);
        
        Assert.Equal(9876543210L, result.AsInt64);
    }

    [NebulaUnitTest]
    public void TestSerializeDeserialize_Double()
    {
        var value = new BsonDouble(3.14159);
        
        var bytes = BsonTransformer.SerializeBsonValue(value);
        var result = BsonTransformer.DeserializeBsonValue<BsonDouble>(bytes);
        
        Assert.True(Math.Abs(result.AsDouble - 3.14159) < 0.00001);
    }

    [NebulaUnitTest]
    public void TestSerializeDeserialize_Boolean()
    {
        var value = new BsonBoolean(true);
        
        var bytes = BsonTransformer.SerializeBsonValue(value);
        var result = BsonTransformer.DeserializeBsonValue<BsonBoolean>(bytes);
        
        Assert.True(result.AsBoolean);
    }

    [NebulaUnitTest]
    public void TestSerializeDeserialize_Array()
    {
        var value = new BsonArray { 1, 2, 3, 4, 5 };
        
        var bytes = BsonTransformer.SerializeBsonValue(value);
        var result = BsonTransformer.DeserializeBsonValue<BsonArray>(bytes);
        
        Assert.Equal(5, result.Count);
        Assert.Equal(1, result[0].AsInt32);
        Assert.Equal(5, result[4].AsInt32);
    }

    [NebulaUnitTest]
    public void TestSerializeDeserialize_Document()
    {
        var value = new BsonDocument
        {
            { "name", "Test" },
            { "value", 42 },
            { "active", true }
        };
        
        var bytes = BsonTransformer.SerializeBsonValue(value);
        var result = BsonTransformer.DeserializeBsonValue<BsonDocument>(bytes);
        
        Assert.Equal("Test", result["name"].AsString);
        Assert.Equal(42, result["value"].AsInt32);
        Assert.True(result["active"].AsBoolean);
    }

    [NebulaUnitTest]
    public void TestSerializeDeserialize_BinaryData()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var value = new BsonBinaryData(data);
        
        var bytes = BsonTransformer.SerializeBsonValue(value);
        var result = BsonTransformer.DeserializeBsonValue<BsonBinaryData>(bytes);
        
        var resultData = result.AsByteArray;
        Assert.Equal(5, resultData.Length);
        Assert.Equal(1, resultData[0]);
        Assert.Equal(5, resultData[4]);
    }

    [NebulaUnitTest]
    public void TestSerializeDeserialize_Null()
    {
        var value = BsonNull.Value;
        
        var bytes = BsonTransformer.SerializeBsonValue(value);
        var result = BsonTransformer.DeserializeBsonValue<BsonNull>(bytes);
        
        Assert.True(result.IsBsonNull);
    }

    [NebulaUnitTest]
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
        
        var bytes = BsonTransformer.SerializeBsonValue(value);
        var result = BsonTransformer.DeserializeBsonValue<BsonDocument>(bytes);
        
        Assert.Equal("test", result["string"].AsString);
        Assert.Equal(42, result["int"].AsInt32);
        Assert.True(Math.Abs(result["double"].AsDouble - 3.14) < 0.01);
        Assert.True(result["bool"].AsBoolean);
        Assert.Equal(3, result["array"].AsBsonArray.Count);
        Assert.Equal("value", result["nested"].AsBsonDocument["key"].AsString);
    }

    // BsonTypeHelper tests (replacing the old Variant-based SerializeVariant tests)

    [NebulaUnitTest]
    public void TestBsonTypeHelper_String()
    {
        var value = "test string";
        var result = BsonTypeHelper.ToBson(value);
        
        Assert.Equal("test string", result.AsString);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_Int()
    {
        var value = 42;
        var result = BsonTypeHelper.ToBson(value);
        
        Assert.Equal(42, result.AsInt32);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_Long()
    {
        var value = 9876543210L;
        var result = BsonTypeHelper.ToBson(value);
        
        Assert.Equal(9876543210L, result.AsInt64);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_Float()
    {
        var value = 3.14f;
        var result = BsonTypeHelper.ToBson(value);
        
        Assert.True(Math.Abs(result.AsDouble - 3.14) < 0.01);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_Bool()
    {
        var value = true;
        var result = BsonTypeHelper.ToBson(value);
        
        Assert.True(result.AsBoolean);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_Vector2()
    {
        var value = new Vector2(1.5f, 2.5f);
        var result = BsonTypeHelper.ToBson(value);
        
        Assert.True(result.IsBsonArray);
        var array = result.AsBsonArray;
        Assert.Equal(2, array.Count);
        Assert.True(Math.Abs(array[0].AsDouble - 1.5) < 0.01);
        Assert.True(Math.Abs(array[1].AsDouble - 2.5) < 0.01);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_Vector3()
    {
        var value = new Vector3(1.0f, 2.0f, 3.0f);
        var result = BsonTypeHelper.ToBson(value);
        
        Assert.True(result.IsBsonArray);
        var array = result.AsBsonArray;
        Assert.Equal(3, array.Count);
        Assert.True(Math.Abs(array[0].AsDouble - 1.0) < 0.01);
        Assert.True(Math.Abs(array[1].AsDouble - 2.0) < 0.01);
        Assert.True(Math.Abs(array[2].AsDouble - 3.0) < 0.01);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_NullString()
    {
        string value = null;
        var result = BsonTypeHelper.ToBson(value);
        
        Assert.True(result.IsBsonNull);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_ByteArray()
    {
        var value = new byte[] { 1, 2, 3, 4, 5 };
        var result = BsonTypeHelper.ToBson(value);
        
        Assert.True(result.IsBsonBinaryData);
        var binaryData = result.AsBsonBinaryData.AsByteArray;
        Assert.Equal(5, binaryData.Length);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_Int32Array()
    {
        var value = new int[] { 10, 20, 30 };
        var result = BsonTypeHelper.ToBson(value);
        
        Assert.True(result.IsBsonArray);
        Assert.Equal(3, result.AsBsonArray.Count);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_Int64Array()
    {
        var value = new long[] { 100L, 200L, 300L };
        var result = BsonTypeHelper.ToBson(value);
        
        Assert.True(result.IsBsonArray);
        Assert.Equal(3, result.AsBsonArray.Count);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_Quaternion()
    {
        var value = new Quaternion(0.5f, 0.5f, 0.5f, 0.5f);
        var result = BsonTypeHelper.ToBson(value);
        
        Assert.True(result.IsBsonArray);
        var array = result.AsBsonArray;
        Assert.Equal(4, array.Count);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_Color()
    {
        var value = new Color(1.0f, 0.5f, 0.25f, 1.0f);
        var result = BsonTypeHelper.ToBson(value);
        
        Assert.True(result.IsBsonArray);
        var array = result.AsBsonArray;
        Assert.Equal(4, array.Count);
    }

    // Deserialization tests for BsonTypeHelper

    [NebulaUnitTest]
    public void TestBsonTypeHelper_ToString()
    {
        var bson = new BsonString("hello");
        var result = BsonTypeHelper.ToString(bson);
        
        Assert.Equal("hello", result);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_ToInt()
    {
        var bson = new BsonInt32(42);
        var result = BsonTypeHelper.ToInt(bson);
        
        Assert.Equal(42, result);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_ToVector2()
    {
        var bson = new BsonArray { 1.5, 2.5 };
        var result = BsonTypeHelper.ToVector2(bson);
        
        Assert.True(Math.Abs(result.X - 1.5f) < 0.01);
        Assert.True(Math.Abs(result.Y - 2.5f) < 0.01);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_ToVector3()
    {
        var bson = new BsonArray { 1.0, 2.0, 3.0 };
        var result = BsonTypeHelper.ToVector3(bson);
        
        Assert.True(Math.Abs(result.X - 1.0f) < 0.01);
        Assert.True(Math.Abs(result.Y - 2.0f) < 0.01);
        Assert.True(Math.Abs(result.Z - 3.0f) < 0.01);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_ToQuaternion()
    {
        var bson = new BsonArray { 0.5, 0.5, 0.5, 0.5 };
        var result = BsonTypeHelper.ToQuaternion(bson);
        
        Assert.True(Math.Abs(result.X - 0.5f) < 0.01);
        Assert.True(Math.Abs(result.Y - 0.5f) < 0.01);
        Assert.True(Math.Abs(result.Z - 0.5f) < 0.01);
        Assert.True(Math.Abs(result.W - 0.5f) < 0.01);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_ToByteArray()
    {
        var bson = new BsonBinaryData(new byte[] { 1, 2, 3 });
        var result = BsonTypeHelper.ToByteArray(bson);
        
        Assert.Equal(3, result.Length);
        Assert.Equal(1, result[0]);
        Assert.Equal(2, result[1]);
        Assert.Equal(3, result[2]);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_ToInt32Array()
    {
        var bson = new BsonArray { 10, 20, 30 };
        var result = BsonTypeHelper.ToInt32Array(bson);
        
        Assert.Equal(3, result.Length);
        Assert.Equal(10, result[0]);
        Assert.Equal(20, result[1]);
        Assert.Equal(30, result[2]);
    }

    [NebulaUnitTest]
    public void TestBsonTypeHelper_ToInt64Array()
    {
        var bson = new BsonArray { 100L, 200L, 300L };
        var result = BsonTypeHelper.ToInt64Array(bson);
        
        Assert.Equal(3, result.Length);
        Assert.Equal(100L, result[0]);
        Assert.Equal(200L, result[1]);
        Assert.Equal(300L, result[2]);
    }
}
