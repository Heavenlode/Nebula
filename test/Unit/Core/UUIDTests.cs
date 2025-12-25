namespace NebulaTests.Unit.Core;

using Nebula.Testing.Unit;
using Xunit;
using Nebula;
using System;

[NebulaUnitTest]
public class UUIDTests
{
    [NebulaUnitTest]
    public void TestDefaultConstructor()
    {
        var uuid = new UUID();
        Assert.NotNull(uuid);
        Assert.NotEqual(Guid.Empty, uuid.Guid);
    }

    [NebulaUnitTest]
    public void TestStringConstructor()
    {
        var guidString = "12345678-1234-1234-1234-123456789abc";
        var uuid = new UUID(guidString);
        
        Assert.Equal(guidString, uuid.ToString());
    }

    [NebulaUnitTest]
    public void TestByteArrayConstructor()
    {
        var guid = Guid.NewGuid();
        var bytes = guid.ToByteArray();
        var uuid = new UUID(bytes);
        
        Assert.Equal(guid, uuid.Guid);
    }

    [NebulaUnitTest]
    public void TestEmptyProperty()
    {
        var empty = UUID.Empty;
        Assert.NotNull(empty);
        Assert.Equal("00000000-0000-0000-0000-000000000000", empty.ToString());
    }

    [NebulaUnitTest]
    public void TestToString()
    {
        var guidString = "12345678-1234-1234-1234-123456789abc";
        var uuid = new UUID(guidString);
        
        Assert.Equal(guidString, uuid.ToString());
    }

    [NebulaUnitTest]
    public void TestEquals_SameUUID()
    {
        var guidString = "12345678-1234-1234-1234-123456789abc";
        var uuid1 = new UUID(guidString);
        var uuid2 = new UUID(guidString);
        
        Assert.True(uuid1.Equals(uuid2));
    }

    [NebulaUnitTest]
    public void TestEquals_DifferentUUID()
    {
        var uuid1 = new UUID();
        var uuid2 = new UUID();
        
        Assert.False(uuid1.Equals(uuid2));
    }

    [NebulaUnitTest]
    public void TestEquals_NullObject()
    {
        var uuid = new UUID();
        Assert.False(uuid.Equals(null));
    }

    [NebulaUnitTest]
    public void TestEquals_NonUUIDObject()
    {
        var uuid = new UUID();
        Assert.False(uuid.Equals("not a uuid"));
    }

    [NebulaUnitTest]
    public void TestGetHashCode()
    {
        var guidString = "12345678-1234-1234-1234-123456789abc";
        var uuid1 = new UUID(guidString);
        var uuid2 = new UUID(guidString);
        
        Assert.Equal(uuid1.GetHashCode(), uuid2.GetHashCode());
    }

    [NebulaUnitTest]
    public void TestEqualityOperator_SameUUID()
    {
        var guidString = "12345678-1234-1234-1234-123456789abc";
        var uuid1 = new UUID(guidString);
        var uuid2 = new UUID(guidString);
        
        Assert.True(uuid1 == uuid2);
    }

    [NebulaUnitTest]
    public void TestEqualityOperator_DifferentUUID()
    {
        var uuid1 = new UUID();
        var uuid2 = new UUID();

        Assert.False(uuid1 == uuid2);
    }

    [NebulaUnitTest]
    public void TestEqualityOperator_BothNull()
    {
        UUID uuid1 = null;
        UUID uuid2 = null;
        
        Assert.True(uuid1 == uuid2);
    }

    [NebulaUnitTest]
    public void TestEqualityOperator_OneNull()
    {
        var uuid1 = new UUID();
        UUID uuid2 = null;
        
        Assert.False(uuid1 == uuid2);
    }

    [NebulaUnitTest]
    public void TestInequalityOperator_SameUUID()
    {
        var guidString = "12345678-1234-1234-1234-123456789abc";
        var uuid1 = new UUID(guidString);
        var uuid2 = new UUID(guidString);
        
        Assert.False(uuid1 != uuid2);
    }

    [NebulaUnitTest]
    public void TestInequalityOperator_DifferentUUID()
    {
        var uuid1 = new UUID();
        var uuid2 = new UUID();
        
        Assert.True(uuid1 != uuid2);
    }

    [NebulaUnitTest]
    public void TestToByteArray()
    {
        var guidString = "12345678-1234-1234-1234-123456789abc";
        var uuid = new UUID(guidString);
        var bytes = uuid.ToByteArray();
        
        Assert.Equal(16, bytes.Length);
        Assert.Equal(Guid.Parse(guidString), new Guid(bytes));
    }

    [NebulaUnitTest]
    public void TestNetworkSerialize_NonNull()
    {
        var uuid = new UUID("12345678-1234-1234-1234-123456789abc");
        var buffer = UUID.NetworkSerialize(null, null, uuid);
        
        Assert.NotNull(buffer);
        Assert.True(buffer.Pointer > 0);
    }

    [NebulaUnitTest]
    public void TestNetworkSerialize_Null()
    {
        var buffer = UUID.NetworkSerialize(null, null, null);
        
        Assert.NotNull(buffer);
        Assert.Equal(1, buffer.Pointer);
    }

    [NebulaUnitTest]
    public void TestNetworkDeserialize_NonNull()
    {
        var original = new UUID("12345678-1234-1234-1234-123456789abc");
        var buffer = UUID.NetworkSerialize(null, null, original);
        
        buffer.ResetPointer();
        
        var deserialized = UUID.NetworkDeserialize(null, null, buffer, new Godot.Variant());
        
        Assert.NotNull(deserialized);
        Assert.Equal(original.ToString(), deserialized.ToString());
    }

    [NebulaUnitTest]
    public void TestNetworkDeserialize_Null()
    {
        var buffer = UUID.NetworkSerialize(null, null, null);
        
        buffer.ResetPointer();
        
        var deserialized = UUID.NetworkDeserialize(null, null, buffer, new Godot.Variant());
        
        Assert.Null(deserialized);
    }

    [NebulaUnitTest]
    public void TestNetworkSerializeDeserialize_RoundTrip()
    {
        var original = new UUID();
        var buffer = UUID.NetworkSerialize(null, null, original);
        
        buffer.ResetPointer();
        
        var deserialized = UUID.NetworkDeserialize(null, null, buffer, new Godot.Variant());
        
        Assert.Equal(original.ToString(), deserialized.ToString());
    }
}
