namespace NebulaTests.Unit.Core.Serialization;

using Nebula.Testing.Unit;
using Xunit;
using Nebula;
using Nebula.Serialization;
using Nebula.Serialization.Serializers;

/// <summary>
/// Regression tests for NotifyOnChange dedup on resent properties. The server
/// re-sends unacked properties every tick, so ImportProperty must detect that a
/// duplicate carries the same value - including INetValue types (UUID, NetId),
/// which have VariantType.Object but live in dedicated PropertyCache union
/// fields where RefValue is null.
/// </summary>
[NebulaUnitTest]
public class PropertyCacheEqualsTests
{
    private static PropertyCache UuidCache(UUID value)
    {
        var cache = new PropertyCache();
        cache.Type = SerialVariantType.Object;
        cache.UUIDValue = value;
        return cache;
    }

    private static PropertyCache NetIdCache(NetId value)
    {
        var cache = new PropertyCache();
        cache.Type = SerialVariantType.Object;
        cache.NetIdValue = value;
        return cache;
    }

    [NebulaUnitTest]
    public void TestUUIDSameValueIsEqual()
    {
        var uuid = UUID.NewUUID();
        var a = UuidCache(uuid);
        var b = UuidCache(uuid);

        Assert.True(NetPropertiesSerializer.PropertyCacheEquals("Nebula.UUID", ref a, ref b));
    }

    [NebulaUnitTest]
    public void TestUUIDDifferentValueIsNotEqual()
    {
        var a = UuidCache(UUID.NewUUID());
        var b = UuidCache(UUID.NewUUID());

        Assert.False(NetPropertiesSerializer.PropertyCacheEquals("Nebula.UUID", ref a, ref b));
    }

    [NebulaUnitTest]
    public void TestUUIDFirstImportAgainstDefaultCacheIsNotEqual()
    {
        // A property that has never been imported has a default cache (Type = Nil).
        // The first real value must register as a change so OnNetChange fires once.
        var oldValue = new PropertyCache();
        var newValue = UuidCache(UUID.NewUUID());

        Assert.False(NetPropertiesSerializer.PropertyCacheEquals("Nebula.UUID", ref oldValue, ref newValue));
    }

    [NebulaUnitTest]
    public void TestNetIdSameValueIsEqual()
    {
        var a = NetIdCache(new NetId(42));
        var b = NetIdCache(new NetId(42));

        Assert.True(NetPropertiesSerializer.PropertyCacheEquals("Nebula.NetId", ref a, ref b));
    }

    [NebulaUnitTest]
    public void TestNetIdDifferentValueIsNotEqual()
    {
        var a = NetIdCache(new NetId(42));
        var b = NetIdCache(new NetId(43));

        Assert.False(NetPropertiesSerializer.PropertyCacheEquals("Nebula.NetId", ref a, ref b));
    }

    [NebulaUnitTest]
    public void TestUnknownCustomTypeFallsBackToBoxedEquality()
    {
        // Custom INetValue types core doesn't know are boxed into RefValue;
        // boxed struct Equals gives value semantics.
        var a = new PropertyCache { Type = SerialVariantType.Object, RefValue = 7 };
        var b = new PropertyCache { Type = SerialVariantType.Object, RefValue = 7 };
        var c = new PropertyCache { Type = SerialVariantType.Object, RefValue = 8 };

        Assert.True(NetPropertiesSerializer.PropertyCacheEquals("Some.Custom.Type", ref a, ref b));
        Assert.False(NetPropertiesSerializer.PropertyCacheEquals("Some.Custom.Type", ref a, ref c));
    }
}
