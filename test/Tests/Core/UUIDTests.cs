namespace NebulaTests.Core;

using GdUnit4;
using static GdUnit4.Assertions;
using Nebula;
using System;

[TestSuite]
public class UUIDTests
{
    [TestCase, RequireGodotRuntime]
    public void TestSimple()
    {
        // Just test that the test suite loads
        AssertBool(true).IsTrue();
    }

    [TestCase, RequireGodotRuntime]
    public void TestDefaultConstructor()
    {
        var uuid = new UUID();
        AssertObject(uuid).IsNotNull();
        AssertObject(uuid.Guid).IsNotEqual(Guid.Empty);
    }

    [TestCase, RequireGodotRuntime]
    public void TestStringConstructor()
    {
        var guidString = "12345678-1234-1234-1234-123456789abc";
        var uuid = new UUID(guidString);
        
        AssertString(uuid.ToString()).IsEqual(guidString);
    }

    [TestCase, RequireGodotRuntime]
    public void TestByteArrayConstructor()
    {
        var guid = Guid.NewGuid();
        var bytes = guid.ToByteArray();
        var uuid = new UUID(bytes);
        
        AssertObject(uuid.Guid).IsEqual(guid);
    }

    [TestCase, RequireGodotRuntime]
    public void TestEmptyProperty()
    {
        var empty = UUID.Empty;
        AssertObject(empty).IsNotNull();
        AssertString(empty.ToString()).IsEqual("00000000-0000-0000-0000-000000000000");
    }

    [TestCase, RequireGodotRuntime]
    public void TestToString()
    {
        var guidString = "12345678-1234-1234-1234-123456789abc";
        var uuid = new UUID(guidString);
        
        AssertString(uuid.ToString()).IsEqual(guidString);
    }

    [TestCase, RequireGodotRuntime]
    public void TestEquals_SameUUID()
    {
        var guidString = "12345678-1234-1234-1234-123456789abc";
        var uuid1 = new UUID(guidString);
        var uuid2 = new UUID(guidString);
        
        AssertBool(uuid1.Equals(uuid2)).IsTrue();
    }

    [TestCase, RequireGodotRuntime]
    public void TestEquals_DifferentUUID()
    {
        var uuid1 = new UUID();
        var uuid2 = new UUID();
        
        AssertBool(uuid1.Equals(uuid2)).IsFalse();
    }

    [TestCase, RequireGodotRuntime]
    public void TestEquals_NullObject()
    {
        var uuid = new UUID();
        AssertBool(uuid.Equals(null)).IsFalse();
    }

    [TestCase, RequireGodotRuntime]
    public void TestEquals_NonUUIDObject()
    {
        var uuid = new UUID();
        AssertBool(uuid.Equals("not a uuid")).IsFalse();
    }

    [TestCase, RequireGodotRuntime]
    public void TestGetHashCode()
    {
        var guidString = "12345678-1234-1234-1234-123456789abc";
        var uuid1 = new UUID(guidString);
        var uuid2 = new UUID(guidString);
        
        AssertInt(uuid1.GetHashCode()).IsEqual(uuid2.GetHashCode());
    }

    [TestCase, RequireGodotRuntime]
    public void TestEqualityOperator_SameUUID()
    {
        var guidString = "12345678-1234-1234-1234-123456789abc";
        var uuid1 = new UUID(guidString);
        var uuid2 = new UUID(guidString);
        
        AssertBool(uuid1 == uuid2).IsTrue();
    }

    [TestCase, RequireGodotRuntime]
    public void TestEqualityOperator_DifferentUUID()
    {
        var uuid1 = new UUID();
        var uuid2 = new UUID();

        // This is because by default new UUIDs are random
        AssertBool(uuid1 == uuid2).IsFalse();
    }

    [TestCase, RequireGodotRuntime]
    public void TestEqualityOperator_BothNull()
    {
        UUID uuid1 = null;
        UUID uuid2 = null;
        
        AssertBool(uuid1 == uuid2).IsTrue();
    }

    [TestCase, RequireGodotRuntime]
    public void TestEqualityOperator_OneNull()
    {
        var uuid1 = new UUID();
        UUID uuid2 = null;
        
        AssertBool(uuid1 == uuid2).IsFalse();
    }

    [TestCase, RequireGodotRuntime]
    public void TestInequalityOperator_SameUUID()
    {
        var guidString = "12345678-1234-1234-1234-123456789abc";
        var uuid1 = new UUID(guidString);
        var uuid2 = new UUID(guidString);
        
        AssertBool(uuid1 != uuid2).IsFalse();
    }

    [TestCase, RequireGodotRuntime]
    public void TestInequalityOperator_DifferentUUID()
    {
        var uuid1 = new UUID();
        var uuid2 = new UUID();
        
        AssertBool(uuid1 != uuid2).IsTrue();
    }

    [TestCase, RequireGodotRuntime]
    public void TestToByteArray()
    {
        var guidString = "12345678-1234-1234-1234-123456789abc";
        var uuid = new UUID(guidString);
        var bytes = uuid.ToByteArray();
        
        AssertInt(bytes.Length).IsEqual(16);
        AssertObject(new Guid(bytes)).IsEqual(Guid.Parse(guidString));
    }

    [TestCase, RequireGodotRuntime]
    public void TestNetworkSerialize_NonNull()
    {
        var uuid = new UUID("12345678-1234-1234-1234-123456789abc");
        var buffer = UUID.NetworkSerialize(null, null, uuid);
        
        AssertObject(buffer).IsNotNull();
        // Buffer should contain: 1 byte flag (1) + 16 bytes for UUID
        AssertInt(buffer.Pointer).IsGreater(0);
    }

    [TestCase, RequireGodotRuntime]
    public void TestNetworkSerialize_Null()
    {
        var buffer = UUID.NetworkSerialize(null, null, null);
        
        AssertObject(buffer).IsNotNull();
        // Buffer should contain just the null flag (0)
        AssertInt(buffer.Pointer).IsEqual(1);
    }

    [TestCase, RequireGodotRuntime]
    public void TestNetworkDeserialize_NonNull()
    {
        var original = new UUID("12345678-1234-1234-1234-123456789abc");
        var buffer = UUID.NetworkSerialize(null, null, original);
        
        // Reset pointer to read from the beginning
        buffer.ResetPointer();
        
        var deserialized = UUID.NetworkDeserialize(null, null, buffer, new Godot.Variant());
        
        AssertObject(deserialized).IsNotNull();
        AssertString(deserialized.ToString()).IsEqual(original.ToString());
    }

    [TestCase, RequireGodotRuntime]
    public void TestNetworkDeserialize_Null()
    {
        var buffer = UUID.NetworkSerialize(null, null, null);
        
        // Reset pointer to read from the beginning
        buffer.ResetPointer();
        
        var deserialized = UUID.NetworkDeserialize(null, null, buffer, new Godot.Variant());
        
        AssertObject(deserialized).IsNull();
    }

    [TestCase, RequireGodotRuntime]
    public void TestNetworkSerializeDeserialize_RoundTrip()
    {
        var original = new UUID();
        var buffer = UUID.NetworkSerialize(null, null, original);
        
        // Reset pointer to read from the beginning
        buffer.ResetPointer();
        
        var deserialized = UUID.NetworkDeserialize(null, null, buffer, new Godot.Variant());
        
        AssertString(deserialized.ToString()).IsEqual(original.ToString());
    }
}

