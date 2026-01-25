namespace NebulaTests.Unit.Core.Serialization;

using Nebula.Testing.Unit;
using Xunit;
using Nebula.Serialization;
using Godot;
using System;

[NebulaUnitTest]
public class NetReaderWriterTests
{
    [NebulaUnitTest]
    public void TestWriteReadByte()
    {
        using var buffer = new NetBuffer();
        byte value = 42;
        
        NetWriter.WriteByte(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadByte(buffer);
        Assert.Equal(value, result);
    }

    [NebulaUnitTest]
    public void TestWriteReadInt32()
    {
        using var buffer = new NetBuffer();
        int value = 123456;
        
        NetWriter.WriteInt32(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadInt32(buffer);
        Assert.Equal(value, result);
    }

    [NebulaUnitTest]
    public void TestWriteReadInt16()
    {
        using var buffer = new NetBuffer();
        short value = 12345;
        
        NetWriter.WriteInt16(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadInt16(buffer);
        Assert.Equal(value, result);
    }

    [NebulaUnitTest]
    public void TestWriteReadInt64()
    {
        using var buffer = new NetBuffer();
        long value = 9876543210L;
        
        NetWriter.WriteInt64(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadInt64(buffer);
        Assert.Equal(value, result);
    }

    [NebulaUnitTest]
    public void TestWriteReadFloat()
    {
        using var buffer = new NetBuffer();
        float value = 3.14159f;
        
        NetWriter.WriteFloat(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadFloat(buffer);
        Assert.True(Math.Abs(result - value) < 0.0001);
    }

    [NebulaUnitTest]
    public void TestWriteReadDouble()
    {
        using var buffer = new NetBuffer();
        double value = 3.14159265358979;
        
        NetWriter.WriteDouble(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadDouble(buffer);
        Assert.True(Math.Abs(result - value) < 0.00000001);
    }

    [NebulaUnitTest]
    public void TestWriteReadBool_True()
    {
        using var buffer = new NetBuffer();
        bool value = true;
        
        NetWriter.WriteBool(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadBool(buffer);
        Assert.True(result);
    }

    [NebulaUnitTest]
    public void TestWriteReadBool_False()
    {
        using var buffer = new NetBuffer();
        bool value = false;
        
        NetWriter.WriteBool(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadBool(buffer);
        Assert.False(result);
    }

    [NebulaUnitTest]
    public void TestWriteReadString()
    {
        using var buffer = new NetBuffer();
        string value = "Hello, Nebula!";
        
        NetWriter.WriteString(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadString(buffer);
        Assert.Equal(value, result);
    }

    [NebulaUnitTest]
    public void TestWriteReadString_Empty()
    {
        using var buffer = new NetBuffer();
        string value = "";
        
        NetWriter.WriteString(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadString(buffer);
        Assert.Equal(value, result);
    }

    [NebulaUnitTest]
    public void TestWriteReadVector2()
    {
        using var buffer = new NetBuffer();
        var value = new Vector2(1.5f, 2.5f);
        
        NetWriter.WriteVector2(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadVector2(buffer);
        // Half precision has lower accuracy
        Assert.True(Math.Abs(result.X - value.X) < 0.01);
        Assert.True(Math.Abs(result.Y - value.Y) < 0.01);
    }

    [NebulaUnitTest]
    public void TestWriteReadVector2Full()
    {
        using var buffer = new NetBuffer();
        var value = new Vector2(1.5f, 2.5f);
        
        NetWriter.WriteVector2Full(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadVector2Full(buffer);
        Assert.True(Math.Abs(result.X - value.X) < 0.0001);
        Assert.True(Math.Abs(result.Y - value.Y) < 0.0001);
    }

    [NebulaUnitTest]
    public void TestWriteReadVector3()
    {
        using var buffer = new NetBuffer();
        var value = new Vector3(1.5f, 2.5f, 3.5f);
        
        NetWriter.WriteVector3(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadVector3(buffer);
        Assert.True(Math.Abs(result.X - value.X) < 0.0001);
        Assert.True(Math.Abs(result.Y - value.Y) < 0.0001);
        Assert.True(Math.Abs(result.Z - value.Z) < 0.0001);
    }

    [NebulaUnitTest]
    public void TestWriteReadVector3Half()
    {
        using var buffer = new NetBuffer();
        var value = new Vector3(1.5f, 2.5f, 3.5f);
        
        NetWriter.WriteVector3Half(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadVector3Half(buffer);
        // Half precision has lower accuracy
        Assert.True(Math.Abs(result.X - value.X) < 0.01);
        Assert.True(Math.Abs(result.Y - value.Y) < 0.01);
        Assert.True(Math.Abs(result.Z - value.Z) < 0.01);
    }

    [NebulaUnitTest]
    public void TestWriteReadQuaternion()
    {
        using var buffer = new NetBuffer();
        var value = new Quaternion(0.5f, 0.5f, 0.5f, 0.5f);
        
        NetWriter.WriteQuaternion(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadQuaternion(buffer);
        // Half precision has lower accuracy
        Assert.True(Math.Abs(result.X - value.X) < 0.01);
        Assert.True(Math.Abs(result.Y - value.Y) < 0.01);
        Assert.True(Math.Abs(result.Z - value.Z) < 0.01);
        Assert.True(Math.Abs(result.W - value.W) < 0.01);
    }

    [NebulaUnitTest]
    public void TestWriteReadQuaternionFull()
    {
        using var buffer = new NetBuffer();
        var value = new Quaternion(0.5f, 0.5f, 0.5f, 0.5f);
        
        NetWriter.WriteQuaternionFull(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadQuaternionFull(buffer);
        Assert.True(Math.Abs(result.X - value.X) < 0.0001);
        Assert.True(Math.Abs(result.Y - value.Y) < 0.0001);
        Assert.True(Math.Abs(result.Z - value.Z) < 0.0001);
        Assert.True(Math.Abs(result.W - value.W) < 0.0001);
    }

    [NebulaUnitTest]
    public void TestWriteReadQuatSmallestThree()
    {
        using var buffer = new NetBuffer();
        // Normalized quaternion
        var value = new Quaternion(0.5f, 0.5f, 0.5f, 0.5f).Normalized();
        
        NetWriter.WriteQuatSmallestThree(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadQuatSmallestThree(buffer);
        // Smallest-three has some precision loss
        Assert.True(Math.Abs(result.X - value.X) < 0.01);
        Assert.True(Math.Abs(result.Y - value.Y) < 0.01);
        Assert.True(Math.Abs(result.Z - value.Z) < 0.01);
        Assert.True(Math.Abs(result.W - value.W) < 0.01);
    }

    [NebulaUnitTest]
    public void TestWriteReadBytesWithLength()
    {
        using var buffer = new NetBuffer();
        var value = new byte[] { 1, 2, 3, 4, 5 };
        
        NetWriter.WriteBytesWithLength(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadBytesWithLength(buffer);
        Assert.Equal(value.Length, result.Length);
        for (int i = 0; i < value.Length; i++)
        {
            Assert.Equal(value[i], result[i]);
        }
    }

    [NebulaUnitTest]
    public void TestWriteReadInt32Array()
    {
        using var buffer = new NetBuffer();
        var value = new int[] { 10, 20, 30, 40 };
        
        NetWriter.WriteInt32Array(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadInt32Array(buffer);
        Assert.Equal(value.Length, result.Length);
        for (int i = 0; i < value.Length; i++)
        {
            Assert.Equal(value[i], result[i]);
        }
    }

    [NebulaUnitTest]
    public void TestWriteReadInt64Array()
    {
        using var buffer = new NetBuffer();
        var value = new long[] { 100L, 200L, 300L };
        
        NetWriter.WriteInt64Array(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadInt64Array(buffer);
        Assert.Equal(value.Length, result.Length);
        for (int i = 0; i < value.Length; i++)
        {
            Assert.Equal(value[i], result[i]);
        }
    }

    [NebulaUnitTest]
    public void TestWriteReadUInt16()
    {
        using var buffer = new NetBuffer();
        ushort value = 65535;
        
        NetWriter.WriteUInt16(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadUInt16(buffer);
        Assert.Equal(value, result);
    }

    [NebulaUnitTest]
    public void TestWriteReadUInt32()
    {
        using var buffer = new NetBuffer();
        uint value = 4294967295;
        
        NetWriter.WriteUInt32(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadUInt32(buffer);
        Assert.Equal(value, result);
    }

    [NebulaUnitTest]
    public void TestWriteReadUInt64()
    {
        using var buffer = new NetBuffer();
        ulong value = 18446744073709551615;
        
        NetWriter.WriteUInt64(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadUInt64(buffer);
        Assert.Equal(value, result);
    }

    [NebulaUnitTest]
    public void TestWriteReadHalf()
    {
        using var buffer = new NetBuffer();
        Half value = (Half)3.14f;
        
        NetWriter.WriteHalf(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadHalf(buffer);
        Assert.True(Math.Abs((float)result - (float)value) < 0.01);
    }

    [NebulaUnitTest]
    public void TestWriteReadHalfFloat()
    {
        using var buffer = new NetBuffer();
        float value = 3.14f;
        
        NetWriter.WriteHalfFloat(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadHalfFloat(buffer);
        Assert.True(Math.Abs(result - value) < 0.01);
    }

    [NebulaUnitTest]
    public void TestMultipleValues()
    {
        using var buffer = new NetBuffer();
        
        NetWriter.WriteInt32(buffer, 42);
        NetWriter.WriteFloat(buffer, 3.14f);
        NetWriter.WriteString(buffer, "test");
        
        buffer.ResetRead();
        
        var int_result = NetReader.ReadInt32(buffer);
        var float_result = NetReader.ReadFloat(buffer);
        var string_result = NetReader.ReadString(buffer);
        
        Assert.Equal(42, int_result);
        Assert.True(Math.Abs(float_result - 3.14f) < 0.0001);
        Assert.Equal("test", string_result);
    }

    [NebulaUnitTest]
    public void TestReadBytes()
    {
        using var buffer = new NetBuffer();
        var value = new byte[] { 10, 20, 30 };
        
        NetWriter.WriteBytes(buffer, value);
        buffer.ResetRead();
        
        var result = NetReader.ReadBytes(buffer, 3);
        Assert.Equal(3, result.Length);
        Assert.Equal(10, result[0]);
        Assert.Equal(20, result[1]);
        Assert.Equal(30, result[2]);
    }

    [NebulaUnitTest]
    public void TestReadRemainingBytes()
    {
        using var buffer = new NetBuffer();
        
        NetWriter.WriteByte(buffer, 1);
        NetWriter.WriteByte(buffer, 2);
        NetWriter.WriteByte(buffer, 3);
        
        buffer.ResetRead();
        NetReader.ReadByte(buffer); // Read first byte
        
        var remaining = NetReader.ReadRemainingBytes(buffer);
        Assert.Equal(2, remaining.Length);
        Assert.Equal(2, remaining[0]);
        Assert.Equal(3, remaining[1]);
    }

    [NebulaUnitTest]
    public void TestWriteReadWithType()
    {
        using var buffer = new NetBuffer();
        
        NetWriter.WriteWithType(buffer, SerialVariantType.Int, 42L);
        buffer.ResetRead();
        
        var result = NetReader.ReadWithType(buffer, out var type);
        Assert.Equal(SerialVariantType.Int, type);
        Assert.Equal(42L, result);
    }

    [NebulaUnitTest]
    public void TestWriteReadByType_String()
    {
        using var buffer = new NetBuffer();
        
        NetWriter.WriteByType(buffer, SerialVariantType.String, "hello");
        buffer.ResetRead();
        
        var result = NetReader.ReadByType(buffer, SerialVariantType.String);
        Assert.Equal("hello", result);
    }

    [NebulaUnitTest]
    public void TestWriteReadByType_Vector3()
    {
        using var buffer = new NetBuffer();
        var value = new Vector3(1.0f, 2.0f, 3.0f);
        
        NetWriter.WriteByType(buffer, SerialVariantType.Vector3, value);
        buffer.ResetRead();
        
        var result = (Vector3)NetReader.ReadByType(buffer, SerialVariantType.Vector3);
        Assert.True(Math.Abs(result.X - value.X) < 0.0001);
        Assert.True(Math.Abs(result.Y - value.Y) < 0.0001);
        Assert.True(Math.Abs(result.Z - value.Z) < 0.0001);
    }
}
