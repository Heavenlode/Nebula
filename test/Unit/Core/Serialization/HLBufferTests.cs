namespace NebulaTests.Unit.Core.Serialization;

using NebulaTests.Unit;
using Xunit;
using Nebula.Serialization;
using System;

public class HLBufferTests
{

    [GodotFact]
    public void TestDefaultConstructor()
    {
        var buffer = new HLBuffer();
        
        Assert.NotNull(buffer);
        Assert.NotNull(buffer.bytes);
        Assert.Empty(buffer.bytes);
    }

    [GodotFact]
    public void TestByteArrayConstructor()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var buffer = new HLBuffer(bytes);
        
        Assert.NotNull(buffer);
        Assert.Equal(bytes, buffer.bytes);
        Assert.Equal(5, buffer.bytes.Length);
    }

    [GodotFact]
    public void TestIsPointerEnd_EmptyBuffer()
    {
        var buffer = new HLBuffer();
        
        Assert.True(buffer.IsPointerEnd);
    }

    [GodotFact]
    public void TestIsPointerEnd_AtStart()
    {
        var buffer = new HLBuffer(new byte[] { 1, 2, 3 });
        
        Assert.False(buffer.IsPointerEnd);
    }

    [GodotFact]
    public void TestLength()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var buffer = new HLBuffer(bytes);
        
        Assert.Equal(5, buffer.Length);
    }

    [GodotFact]
    public void TestRemainingBytes_AtStart()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var buffer = new HLBuffer(bytes);
        
        var remaining = buffer.RemainingBytes;
        Assert.Equal(5, remaining.Length);
        Assert.Equal(1, remaining[0]);
    }

    [GodotFact]
    public void TestConsistencyBufferSizeLimit()
    {
        Assert.Equal(256, HLBuffer.CONSISTENCY_BUFFER_SIZE_LIMIT);
    }

    [GodotFact]
    public void TestBufferReusePattern()
    {
        var buffer = new HLBuffer();
        
        HLBytes.Pack(buffer, 42);
        HLBytes.Pack(buffer, 3.14f);
        
        buffer.ResetPointer();
        
        var intResult = HLBytes.UnpackInt32(buffer);
        var floatResult = HLBytes.UnpackFloat(buffer);
        
        Assert.Equal(42, intResult);
        Assert.True(Math.Abs(floatResult - 3.14f) < 0.0001);
    }

    [GodotFact]
    public void TestResetPointer()
    {
        var buffer = new HLBuffer();
        
        HLBytes.Pack(buffer, (byte)1);
        HLBytes.Pack(buffer, (byte)2);
        HLBytes.Pack(buffer, (byte)3);
        
        buffer.ResetPointer();
        var first = HLBytes.UnpackByte(buffer);
        var second = HLBytes.UnpackByte(buffer);
        
        Assert.Equal(1, first);
        Assert.Equal(2, second);
        
        buffer.ResetPointer();
        var firstAgain = HLBytes.UnpackByte(buffer);
        
        Assert.Equal(1, firstAgain);
    }
}
