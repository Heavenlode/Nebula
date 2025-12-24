namespace NebulaTests.Core.Serialization;

using GdUnit4;
using static GdUnit4.Assertions;
using Nebula.Serialization;
using System;

[TestSuite]
public class HLBufferTests
{
    [TestCase, RequireGodotRuntime]
    public void TestSimple()
    {
        AssertBool(true).IsTrue();
    }

    [TestCase, RequireGodotRuntime]
    public void TestDefaultConstructor()
    {
        var buffer = new HLBuffer();
        
        AssertObject(buffer).IsNotNull();
        AssertObject(buffer.bytes).IsNotNull();
        AssertInt(buffer.bytes.Length).IsEqual(0);
    }

    [TestCase, RequireGodotRuntime]
    public void TestByteArrayConstructor()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var buffer = new HLBuffer(bytes);
        
        AssertObject(buffer).IsNotNull();
        AssertObject(buffer.bytes).IsEqual(bytes);
        AssertInt(buffer.bytes.Length).IsEqual(5);
    }

    [TestCase, RequireGodotRuntime]
    public void TestIsPointerEnd_EmptyBuffer()
    {
        var buffer = new HLBuffer();
        
        AssertBool(buffer.IsPointerEnd).IsTrue();
    }

    [TestCase, RequireGodotRuntime]
    public void TestIsPointerEnd_AtStart()
    {
        var buffer = new HLBuffer(new byte[] { 1, 2, 3 });
        
        AssertBool(buffer.IsPointerEnd).IsFalse();
    }

    [TestCase, RequireGodotRuntime]
    public void TestLength()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var buffer = new HLBuffer(bytes);
        
        AssertInt(buffer.Length).IsEqual(5);
    }

    [TestCase, RequireGodotRuntime]
    public void TestRemainingBytes_AtStart()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var buffer = new HLBuffer(bytes);
        
        var remaining = buffer.RemainingBytes;
        AssertInt(remaining.Length).IsEqual(5);
        AssertInt(remaining[0]).IsEqual(1);
    }

    [TestCase, RequireGodotRuntime]
    public void TestConsistencyBufferSizeLimit()
    {
        AssertInt(HLBuffer.CONSISTENCY_BUFFER_SIZE_LIMIT).IsEqual(256);
    }

    [TestCase, RequireGodotRuntime]
    public void TestBufferReusePattern()
    {
        var buffer = new HLBuffer();
        
        // Pack some data
        HLBytes.Pack(buffer, 42);
        HLBytes.Pack(buffer, 3.14f);
        
        // Reset pointer to read the data
        buffer.ResetPointer();
        
        var intResult = HLBytes.UnpackInt32(buffer);
        var floatResult = HLBytes.UnpackFloat(buffer);
        
        AssertInt(intResult).IsEqual(42);
        AssertFloat(floatResult).IsEqualApprox(3.14f, 0.0001);
    }

    [TestCase, RequireGodotRuntime]
    public void TestResetPointer()
    {
        var buffer = new HLBuffer();
        
        HLBytes.Pack(buffer, (byte)1);
        HLBytes.Pack(buffer, (byte)2);
        HLBytes.Pack(buffer, (byte)3);
        
        // Read some data
        buffer.ResetPointer();
        var first = HLBytes.UnpackByte(buffer);
        var second = HLBytes.UnpackByte(buffer);
        
        AssertInt(first).IsEqual(1);
        AssertInt(second).IsEqual(2);
        
        // Reset and read again from the beginning
        buffer.ResetPointer();
        var firstAgain = HLBytes.UnpackByte(buffer);
        
        AssertInt(firstAgain).IsEqual(1);
    }
}
