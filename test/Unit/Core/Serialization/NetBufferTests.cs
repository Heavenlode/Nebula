namespace NebulaTests.Unit.Core.Serialization;

using Nebula.Testing.Unit;
using Xunit;
using Nebula.Serialization;
using System;

[NebulaUnitTest]
public class NetBufferTests
{

    [NebulaUnitTest]
    public void TestDefaultConstructor()
    {
        using var buffer = new NetBuffer();
        
        Assert.NotNull(buffer);
        Assert.NotNull(buffer.RawBuffer);
        Assert.Equal(0, buffer.Length);
    }

    [NebulaUnitTest]
    public void TestByteArrayConstructor()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        using var buffer = new NetBuffer(bytes);
        
        Assert.NotNull(buffer);
        Assert.Equal(bytes, buffer.RawBuffer);
        Assert.Equal(5, buffer.Length);
    }

    [NebulaUnitTest]
    public void TestIsReadComplete_EmptyBuffer()
    {
        using var buffer = new NetBuffer();
        
        Assert.True(buffer.IsReadComplete);
    }

    [NebulaUnitTest]
    public void TestIsReadComplete_AtStart()
    {
        using var buffer = new NetBuffer(new byte[] { 1, 2, 3 });
        
        Assert.False(buffer.IsReadComplete);
    }

    [NebulaUnitTest]
    public void TestLength()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        using var buffer = new NetBuffer(bytes);
        
        Assert.Equal(5, buffer.Length);
    }

    [NebulaUnitTest]
    public void TestRemaining_AtStart()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        using var buffer = new NetBuffer(bytes);
        
        var remaining = buffer.Remaining;
        Assert.Equal(5, remaining);
        
        var unreadSpan = buffer.UnreadSpan;
        Assert.Equal(5, unreadSpan.Length);
        Assert.Equal(1, unreadSpan[0]);
    }

    [NebulaUnitTest]
    public void TestDefaultCapacity()
    {
        Assert.Equal(1536, NetBuffer.DefaultCapacity);
    }

    [NebulaUnitTest]
    public void TestBufferReusePattern()
    {
        using var buffer = new NetBuffer();
        
        NetWriter.WriteInt32(buffer, 42);
        NetWriter.WriteFloat(buffer, 3.14f);
        
        buffer.ResetRead();
        
        var intResult = NetReader.ReadInt32(buffer);
        var floatResult = NetReader.ReadFloat(buffer);
        
        Assert.Equal(42, intResult);
        Assert.True(Math.Abs(floatResult - 3.14f) < 0.0001);
    }

    [NebulaUnitTest]
    public void TestResetRead()
    {
        using var buffer = new NetBuffer();
        
        NetWriter.WriteByte(buffer, 1);
        NetWriter.WriteByte(buffer, 2);
        NetWriter.WriteByte(buffer, 3);
        
        buffer.ResetRead();
        var first = NetReader.ReadByte(buffer);
        var second = NetReader.ReadByte(buffer);
        
        Assert.Equal(1, first);
        Assert.Equal(2, second);
        
        buffer.ResetRead();
        var firstAgain = NetReader.ReadByte(buffer);
        
        Assert.Equal(1, firstAgain);
    }

    [NebulaUnitTest]
    public void TestReset()
    {
        using var buffer = new NetBuffer();
        
        NetWriter.WriteInt32(buffer, 42);
        Assert.Equal(4, buffer.WritePosition);
        Assert.Equal(0, buffer.ReadPosition);
        
        buffer.Reset();
        
        Assert.Equal(0, buffer.WritePosition);
        Assert.Equal(0, buffer.ReadPosition);
    }

    [NebulaUnitTest]
    public void TestToArray()
    {
        using var buffer = new NetBuffer();
        
        NetWriter.WriteByte(buffer, 1);
        NetWriter.WriteByte(buffer, 2);
        NetWriter.WriteByte(buffer, 3);
        
        var array = buffer.ToArray();
        
        Assert.Equal(3, array.Length);
        Assert.Equal(1, array[0]);
        Assert.Equal(2, array[1]);
        Assert.Equal(3, array[2]);
    }

    [NebulaUnitTest]
    public void TestCapacity()
    {
        using var buffer = new NetBuffer(256, usePool: false);
        
        Assert.Equal(256, buffer.Capacity);
    }

    [NebulaUnitTest]
    public void TestWrittenSpan()
    {
        using var buffer = new NetBuffer();
        
        NetWriter.WriteByte(buffer, 10);
        NetWriter.WriteByte(buffer, 20);
        
        var span = buffer.WrittenSpan;
        Assert.Equal(2, span.Length);
        Assert.Equal(10, span[0]);
        Assert.Equal(20, span[1]);
    }
}
