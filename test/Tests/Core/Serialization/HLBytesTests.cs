namespace NebulaTests.Core.Serialization;

using GdUnit4;
using static GdUnit4.Assertions;
using Nebula.Serialization;
using Nebula.Utility.Tools;
using Godot;
using System;

[TestSuite]
public class HLBytesTests
{
    [Before]
    public void Setup()
    {
        // Initialize the Debugger singleton by adding it to the scene tree
        // This triggers _EnterTree() which sets Debugger.Instance
        if (Debugger.Instance == null)
        {
            var debugger = AutoFree(new Debugger());
            SceneTree tree = Engine.GetMainLoop() as SceneTree;
            tree?.Root.AddChild(debugger);
        }
    }

    [TestCase, RequireGodotRuntime]
    public void TestSimple()
    {
        AssertBool(true).IsTrue();
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackByte()
    {
        var buffer = new HLBuffer();
        byte value = 42;
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackByte(buffer);
        AssertInt(result).IsEqual(value);
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackInt32()
    {
        var buffer = new HLBuffer();
        int value = 123456;
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackInt32(buffer);
        AssertInt(result).IsEqual(value);
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackInt16()
    {
        var buffer = new HLBuffer();
        short value = 12345;
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackInt16(buffer);
        AssertInt(result).IsEqual(value);
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackInt64()
    {
        var buffer = new HLBuffer();
        long value = 9876543210L;
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackInt64(buffer);
        AssertThat(result).IsEqual(value);
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackFloat()
    {
        var buffer = new HLBuffer();
        float value = 3.14159f;
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackFloat(buffer);
        AssertFloat(result).IsEqualApprox(value, 0.0001);
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackBool_True()
    {
        var buffer = new HLBuffer();
        bool value = true;
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackBool(buffer);
        AssertBool(result).IsTrue();
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackBool_False()
    {
        var buffer = new HLBuffer();
        bool value = false;
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackBool(buffer);
        AssertBool(result).IsFalse();
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackString()
    {
        var buffer = new HLBuffer();
        string value = "Hello, Nebula!";
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackString(buffer);
        AssertString(result).IsEqual(value);
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackVector2()
    {
        var buffer = new HLBuffer();
        var value = new Vector2(1.5f, 2.5f);
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackVector2(buffer);
        AssertFloat(result.X).IsEqualApprox(value.X, 0.01);
        AssertFloat(result.Y).IsEqualApprox(value.Y, 0.01);
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackVector3()
    {
        var buffer = new HLBuffer();
        var value = new Vector3(1.5f, 2.5f, 3.5f);
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackVector3(buffer);
        AssertFloat(result.X).IsEqualApprox(value.X, 0.0001);
        AssertFloat(result.Y).IsEqualApprox(value.Y, 0.0001);
        AssertFloat(result.Z).IsEqualApprox(value.Z, 0.0001);
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackQuaternion()
    {
        var buffer = new HLBuffer();
        var value = new Quaternion(0.5f, 0.5f, 0.5f, 0.5f);
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackQuaternion(buffer);
        // Using larger tolerance due to Half precision
        AssertFloat(result.X).IsEqualApprox(value.X, 0.01);
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackByteArray()
    {
        var buffer = new HLBuffer();
        var value = new byte[] { 1, 2, 3, 4, 5 };
        
        HLBytes.Pack(buffer, value, packLength: true);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackByteArray(buffer);
        AssertInt(result.Length).IsEqual(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            AssertInt(result[i]).IsEqual(value[i]);
        }
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackInt32Array()
    {
        var buffer = new HLBuffer();
        var value = new int[] { 10, 20, 30, 40 };
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackInt32Array(buffer);
        AssertInt(result.Length).IsEqual(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            AssertInt(result[i]).IsEqual(value[i]);
        }
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackInt64Array()
    {
        var buffer = new HLBuffer();
        var value = new long[] { 100L, 200L, 300L };
        
        HLBytes.Pack(buffer, value);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackInt64Array(buffer);
        AssertInt(result.Length).IsEqual(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            AssertThat(result[i]).IsEqual(value[i]);
        }
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackVariant_Int()
    {
        var buffer = new HLBuffer();
        Variant value = 42;
        
        HLBytes.PackVariant(buffer, value, packType: true);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackVariant(buffer);
        AssertBool(result.HasValue).IsTrue();
        AssertThat(result.Value.AsInt64()).IsEqual(42L);
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackVariant_Float()
    {
        var buffer = new HLBuffer();
        Variant value = 3.14f;
        
        HLBytes.PackVariant(buffer, value, packType: true);
        var readBuffer = new HLBuffer(buffer.bytes);
        
        var result = HLBytes.UnpackVariant(readBuffer);
        AssertBool(result.HasValue).IsTrue();
        AssertFloat(result.Value.AsSingle()).IsEqualApprox(3.14f, 0.0001);
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackVariant_String()
    {
        var buffer = new HLBuffer();
        Variant value = "Test String";
        
        HLBytes.PackVariant(buffer, value, packType: true);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackVariant(buffer);
        AssertBool(result.HasValue).IsTrue();
        AssertString(result.Value.AsString()).IsEqual("Test String");
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackVariant_Bool()
    {
        var buffer = new HLBuffer();
        Variant value = true;
        
        HLBytes.PackVariant(buffer, value, packType: true);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackVariant(buffer);
        AssertBool(result.HasValue).IsTrue();
        AssertBool(result.Value.AsBool()).IsTrue();
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackVariant_Vector3()
    {
        var buffer = new HLBuffer();
        Variant value = new Vector3(1.0f, 2.0f, 3.0f);
        
        HLBytes.PackVariant(buffer, value, packType: true);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackVariant(buffer);
        AssertBool(result.HasValue).IsTrue();
        var vec = result.Value.As<Vector3>();
        AssertFloat(vec.X).IsEqualApprox(1.0f, 0.0001);
        AssertFloat(vec.Y).IsEqualApprox(2.0f, 0.0001);
        AssertFloat(vec.Z).IsEqualApprox(3.0f, 0.0001);
    }

    [TestCase, RequireGodotRuntime]
    public void TestPackUnpackArray()
    {
        var buffer = new HLBuffer();
        var array = new Godot.Collections.Array { 1, 2, 3 };
        
        HLBytes.PackArray(buffer, array);
        buffer.ResetPointer();
        
        var result = HLBytes.UnpackArray(buffer);
        AssertInt(result.Count).IsEqual(3);
    }

    [TestCase, RequireGodotRuntime]
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
        AssertInt(result.Count).IsEqual(2);
    }

    [TestCase, RequireGodotRuntime]
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
        
        AssertInt(int_result).IsEqual(42);
        AssertFloat(float_result).IsEqualApprox(3.14f, 0.0001);
        AssertString(string_result).IsEqual("test");
    }
}
