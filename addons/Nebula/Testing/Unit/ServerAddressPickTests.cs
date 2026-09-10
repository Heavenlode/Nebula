using System.Net;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Which resolved address a client connects to. ENet cannot route a link-local IPv6 address
/// (its address has no scope id), and iOS lists exactly that first for a Bonjour name.
/// </summary>
[NebulaUnitTest]
public class ServerAddressPickTests
{
    private static readonly IPAddress LinkLocalV6 = IPAddress.Parse("fe80::1ceb:55eb:caf6:72d1");
    private static readonly IPAddress GlobalV6 = IPAddress.Parse("2001:1c00:1e32:e700::65");
    private static readonly IPAddress LanV4 = IPAddress.Parse("192.168.178.214");

    [NebulaUnitTest]
    public void PrefersIPv4_WhateverTheResolverOrder()
    {
        Assert.Equal(LanV4, NetRunner.PickServerAddress(new[] { LinkLocalV6, GlobalV6, LanV4 }));
        Assert.Equal(LanV4, NetRunner.PickServerAddress(new[] { LanV4, GlobalV6 }));
    }

    [NebulaUnitTest]
    public void FallsBackToGlobalIPv6_NeverLinkLocal()
    {
        Assert.Equal(GlobalV6, NetRunner.PickServerAddress(new[] { LinkLocalV6, GlobalV6 }));
        Assert.Null(NetRunner.PickServerAddress(new[] { LinkLocalV6 }));
        Assert.Null(NetRunner.PickServerAddress(System.Array.Empty<IPAddress>()));
    }
}
