namespace NebulaTests.Unit.Core.Serialization;

using NebulaTests.Unit;
using Xunit;
using Nebula.Serialization;
using Godot;
using System;

public class HLBytesTests
{
    [GodotFact]
    public void TestSimple()
    {
        Assert.True(true);
    }

    [GodotFact]
    public void TestPackUnpackByte()
    {
        var buffer = new HLBuffer();
        byte value = 42;
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackByte(buffer);
        Assert.Equal(value, result);
    }

    [GodotFact]
    public void TestPackUnpackInt32()
    {
        var buffer = new HLBuffer();
        int value = 123456;
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackInt32(buffer);
        Assert.Equal(value, result);
    }

    [GodotFact]
    public void TestPackUnpackInt16()
    {
        var buffer = new HLBuffer();
        short value = 12345;
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackInt16(buffer);
        Assert.Equal(value, result);
    }

    [GodotFact]
    public void TestPackUnpackInt64()
    {
        var buffer = new HLBuffer();
        long value = 9876543210L;
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackInt64(buffer);
        Assert.Equal(value, result);
    }

    [GodotFact]
    public void TestPackUnpackFloat()
    {
        var buffer = new HLBuffer();
        float value = 3.14159f;
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackFloat(buffer);
        Assert.True(Math.Abs(result - value) < 0.0001);
    }

    [GodotFact]
    public void TestPackUnpackBool_True()
    {
        var buffer = new HLBuffer();
        bool value = true;
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackBool(buffer);
        Assert.True(result);
    }

    [GodotFact]
    public void TestPackUnpackBool_False()
    {
        var buffer = new HLBuffer();
        bool value = false;
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackBool(buffer);
        Assert.False(result);
    }

    [GodotFact]
    public void TestPackUnpackString()
    {
        var buffer = new HLBuffer();
        string value = "Hello, Nebula!";
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackString(buffer);
        Assert.Equal(value, result);
    }

    [GodotFact]
    public void TestPackUnpackVector2()
    {
        var buffer = new HLBuffer();
        var value = new Vector2(1.5f, 2.5f);
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackVector2(buffer);
        Assert.True(Math.Abs(result.X - value.X) < 0.01);
        Assert.True(Math.Abs(result.Y - value.Y) < 0.01);
    }

    [GodotFact]
    public void TestPackUnpackVector3()
    {
        var buffer = new HLBuffer();
        var value = new Vector3(1.5f, 2.5f, 3.5f);
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackVector3(buffer);
        Assert.True(Math.Abs(result.X - value.X) < 0.0001);
        Assert.True(Math.Abs(result.Y - value.Y) < 0.0001);
        Assert.True(Math.Abs(result.Z - value.Z) < 0.0001);
    }

    [GodotFact]
    public void TestPackUnpackQuaternion()
    {
        var buffer = new HLBuffer();
        var value = new Quaternion(0.5f, 0.5f, 0.5f, 0.5f);
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackQuaternion(buffer);
        Assert.True(Math.Abs(result.X - value.X) < 0.01);
    }

    [GodotFact]
    public void TestPackUnpackByteArray()
    {
        var buffer = new HLBuffer();
        var value = new byte[] { 1, 2, 3, 4, 5 };
        
        HLBytes.Pack(buffer, value, packLength: true);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackByteArray(buffer);
        Assert.Equal(value.Length, result.Length);
        for (int i = 0; i < value.Length; i++)
        {
            Assert.Equal(value[i], result[i]);
        }
    }

    [GodotFact]
    public void TestPackUnpackInt32Array()
    {
        var buffer = new HLBuffer();
        var value = new int[] { 10, 20, 30, 40 };
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackInt32Array(buffer);
        Assert.Equal(value.Length, result.Length);
        for (int i = 0; i < value.Length; i++)
        {
            Assert.Equal(value[i], result[i]);
        }
    }

    [GodotFact]
    public void TestPackUnpackInt64Array()
    {
        var buffer = new HLBuffer();
        var value = new long[] { 100L, 200L, 300L };
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackInt64Array(buffer);
        Assert.Equal(value.Length, result.Length);
        for (int i = 0; i < value.Length; i++)
        {
            Assert.Equal(value[i], result[i]);
        }
    }

    [GodotFact]
    public void TestPackUnpackVariant_Int()
    {
        var buffer = new HLBuffer();
        Variant value = 42;
        
        HLBytes.PackVariant(buffer, value, packType: true);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackVariant(buffer);
        Assert.True(result.HasValue);
        Assert.Equal(42L, result.Value.AsInt64());
    }

    [GodotFact]
    public void TestPackUnpackVariant_Float()
    {
        var buffer = new HLBuffer();
        Variant value = 3.14f;
        
        HLBytes.PackVariant(buffer, value, packType: true);
        var readBuffer = new HLBuffer(buffer.bytes);
        
        var result = HLBytes.UnpackVariant(readBuffer);
        Assert.True(result.HasValue);
        Assert.True(Math.Abs(result.Value.AsSingle() - 3.14f) < 0.0001);
    }

    [GodotFact]
    public void TestPackUnpackVariant_String()
    {
        var buffer = new HLBuffer();
        Variant value = "Test String";
        
        HLBytes.PackVariant(buffer, value, packType: true);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackVariant(buffer);
        Assert.True(result.HasValue);
        Assert.Equal("Test String", result.Value.AsString());
    }

    [GodotFact]
    public void TestPackUnpackVariant_Bool()
    {
        var buffer = new HLBuffer();
        Variant value = true;
        
        HLBytes.PackVariant(buffer, value, packType: true);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackVariant(buffer);
        Assert.True(result.HasValue);
        Assert.True(result.Value.AsBool());
    }

    [GodotFact]
    public void TestPackUnpackVariant_Vector3()
    {
        var buffer = new HLBuffer();
        Variant value = new Vector3(1.0f, 2.0f, 3.0f);
        
        HLBytes.PackVariant(buffer, value, packType: true);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackVariant(buffer);
        Assert.True(result.HasValue);
        var vec = result.Value.As<Vector3>();
        Assert.True(Math.Abs(vec.X - 1.0f) < 0.0001);
        Assert.True(Math.Abs(vec.Y - 2.0f) < 0.0001);
        Assert.True(Math.Abs(vec.Z - 3.0f) < 0.0001);
    }

    [GodotFact]
    public void TestPackUnpackArray()
    {
        var buffer = new HLBuffer();
        var array = new Godot.Collections.Array { 1, 2, 3 };
        
        HLBytes.PackArray(buffer, array);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackArray(buffer);
        Assert.Equal(3, result.Count);
    }

    [GodotFact]
    public void TestPackUnpackDictionary()
    {
        var buffer = new HLBuffer();
        var dict = new Godot.Collections.Dictionary
        {
            { "key1", 1 },
            { "key2", 2 }
        };
        
        HLBytes.PackDictionary(buffer, dict);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackDictionary(buffer);
        Assert.Equal(2, result.Count);
    }

    [GodotFact]
    public void TestMultipleValues()
    {
        var buffer = new HLBuffer();
        
        HLBytes.Pack(buffer, 42);
        HLBytes.Pack(buffer, 3.14f);
        HLBytes.Pack(buffer, "test");
        
        buffer.ResetPointer();
        
        var int_result = HLBytes.UnpackInt32(buffer);
        var float_result = HLBytes.UnpackFloat(buffer);
        var string_result = HLBytes.UnpackString(buffer);
        
        Assert.Equal(42, int_result);
        Assert.True(Math.Abs(float_result - 3.14f) < 0.0001);
        Assert.Equal("test", string_result);
    }
}
