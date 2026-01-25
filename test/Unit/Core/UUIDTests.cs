namespace NebulaTests.Unit.Core;

using Nebula.Testing.Unit;
using Xunit;
using Nebula;
using Nebula.Serialization;
using System;

[NebulaUnitTest]
public class UUIDTests
{
    [NebulaUnitTest]
    public void TestDefaultConstructor()
    {
        var uuid = new UUID();
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
    public void TestGuidConstructor()
    {
        var guid = Guid.NewGuid();
        var uuid = new UUID(guid);
        
        Assert.Equal(guid, uuid.Guid);
    }

    [NebulaUnitTest]
    public void TestEmptyProperty()
    {
        var empty = UUID.Empty;
        Assert.True(empty.IsEmpty);
        Assert.Equal("00000000-0000-0000-0000-000000000000", empty.ToString());
    }

    [NebulaUnitTest]
    public void TestIsEmpty_NewUUID()
    {
        var uuid = new UUID();
        Assert.False(uuid.IsEmpty);
    }

    [NebulaUnitTest]
    public void TestIsEmpty_DefaultStruct()
    {
        UUID uuid = default;
        Assert.True(uuid.IsEmpty);
    }

    [NebulaUnitTest]
    public void TestNewUUID()
    {
        var uuid1 = UUID.NewUUID();
        var uuid2 = UUID.NewUUID();
        
        Assert.False(uuid1.IsEmpty);
        Assert.False(uuid2.IsEmpty);
        Assert.NotEqual(uuid1, uuid2);
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
    public void TestEquals_NonUUIDObject()
    {
        var uuid = new UUID();
        Assert.False(uuid.Equals("not a uuid"));
    }

    [NebulaUnitTest]
    public void TestEquals_BoxedUUID()
    {
        var guidString = "12345678-1234-1234-1234-123456789abc";
        var uuid1 = new UUID(guidString);
        object boxed = new UUID(guidString);
        
        Assert.True(uuid1.Equals(boxed));
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
    public void TestEqualityOperator_BothEmpty()
    {
        UUID uuid1 = default;
        UUID uuid2 = default;
        
        Assert.True(uuid1 == uuid2);
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
    public void TestNetworkSerialize_NonEmpty()
    {
        var uuid = new UUID("12345678-1234-1234-1234-123456789abc");
        using var buffer = new NetBuffer();
        
        // Use default for WorldRunner and NetPeer (they're not used for UUID serialization)
        UUID.NetworkSerialize(null, default, in uuid, buffer);
        
        Assert.True(buffer.WritePosition > 0);
        // First byte is 1 (not empty), then 16 bytes for the GUID
        Assert.Equal(17, buffer.WritePosition);
    }

    [NebulaUnitTest]
    public void TestNetworkSerialize_Empty()
    {
        UUID uuid = default;
        using var buffer = new NetBuffer();
        
        UUID.NetworkSerialize(null, default, in uuid, buffer);
        
        // Only 1 byte (the null flag)
        Assert.Equal(1, buffer.WritePosition);
    }

    [NebulaUnitTest]
    public void TestNetworkDeserialize_NonEmpty()
    {
        var original = new UUID("12345678-1234-1234-1234-123456789abc");
        using var buffer = new NetBuffer();
        
        UUID.NetworkSerialize(null, default, in original, buffer);
        buffer.ResetRead();
        
        var deserialized = UUID.NetworkDeserialize(null, default, buffer);
        
        Assert.False(deserialized.IsEmpty);
        Assert.Equal(original.ToString(), deserialized.ToString());
    }

    [NebulaUnitTest]
    public void TestNetworkDeserialize_Empty()
    {
        UUID uuid = default;
        using var buffer = new NetBuffer();
        
        UUID.NetworkSerialize(null, default, in uuid, buffer);
        buffer.ResetRead();
        
        var deserialized = UUID.NetworkDeserialize(null, default, buffer);
        
        Assert.True(deserialized.IsEmpty);
    }

    [NebulaUnitTest]
    public void TestNetworkSerializeDeserialize_RoundTrip()
    {
        var original = new UUID();
        using var buffer = new NetBuffer();
        
        UUID.NetworkSerialize(null, default, in original, buffer);
        buffer.ResetRead();
        
        var deserialized = UUID.NetworkDeserialize(null, default, buffer);
        
        Assert.Equal(original.ToString(), deserialized.ToString());
        Assert.Equal(original, deserialized);
    }

    [NebulaUnitTest]
    public void TestIEquatable()
    {
        var guidString = "12345678-1234-1234-1234-123456789abc";
        IEquatable<UUID> uuid1 = new UUID(guidString);
        var uuid2 = new UUID(guidString);
        
        Assert.True(uuid1.Equals(uuid2));
    }
}
