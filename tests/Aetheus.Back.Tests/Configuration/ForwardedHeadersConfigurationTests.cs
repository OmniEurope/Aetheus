// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Back.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Tests.Configuration;

/// <summary>R-52: X-Forwarded-* is honoured only from loopback and the declared proxy networks, run
/// through the real ForwardedHeadersMiddleware with the options Program.cs installs.</summary>
public sealed class ForwardedHeadersConfigurationTests
{
    private const string ClaimedClient = "203.0.113.77";

    [Theory]
    [InlineData("198.51.100.20")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.10")]
    public async Task UndeclaredSource_KeepsSocketAddressAndScheme(string socketAddress)
    {
        var context = await RunAsync(Build([]), socketAddress);

        Assert.Equal(IPAddress.Parse(socketAddress), context.Connection.RemoteIpAddress);
        Assert.Equal("http", context.Request.Scheme);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("172.18.0.1")]
    [InlineData("172.17.0.1")]
    [InlineData("::ffff:172.18.0.1")]
    public async Task LoopbackOrDockerBridgeByDefault_HonoursForwardedClientAndScheme(string socketAddress)
    {
        var context = await RunAsync(Build([]), socketAddress);

        Assert.Equal(IPAddress.Parse(ClaimedClient), context.Connection.RemoteIpAddress);
        Assert.Equal("https", context.Request.Scheme);
    }

    [Fact]
    public async Task ConfiguredNetworks_ReplaceTheDockerDefaultButKeepLoopback()
    {
        var configuration = Build(new() { [ForwardedHeadersConfiguration.KnownNetworksKey + ":0"] = "192.168.32.0/20" });

        var declared = await RunAsync(configuration, "192.168.32.1");
        var formerDefault = await RunAsync(configuration, "172.18.0.1");
        var loopback = await RunAsync(configuration, "127.0.0.1");

        Assert.Equal(IPAddress.Parse(ClaimedClient), declared.Connection.RemoteIpAddress);
        Assert.Equal(IPAddress.Parse("172.18.0.1"), formerDefault.Connection.RemoteIpAddress);
        Assert.Equal(IPAddress.Parse(ClaimedClient), loopback.Connection.RemoteIpAddress);
    }

    // Production host (2026-11-15): Docker allocates its bridges from 10.0.x.0/24, the backend saw the
    // Apache relay as ::ffff:10.0.13.1, dropped X-Forwarded-Proto and the NuGet index advertised http://.
    [Theory]
    [InlineData("::ffff:10.0.13.1")]
    [InlineData("10.0.13.1")]
    public async Task ThePeerOnTheContainersOwnNetwork_IsTrustedByDefault(string socketAddress)
    {
        var context = await RunAsync(Build([]), socketAddress, "10.0.13.0/24");

        Assert.Equal(IPAddress.Parse(ClaimedClient), context.Connection.RemoteIpAddress);
        Assert.Equal("https", context.Request.Scheme);
    }

    [Fact]
    public async Task TheContainersNetwork_DoesNotWidenToOtherPrivateRanges_NorSurviveAConfiguredList()
    {
        var neighbour = await RunAsync(Build([]), "10.0.14.1", "10.0.13.0/24");
        var configured = await RunAsync(
            Build(new() { [ForwardedHeadersConfiguration.KnownNetworksKey + ":0"] = "192.168.32.0/20" }), "10.0.13.1", "10.0.13.0/24");

        Assert.Equal("http", neighbour.Request.Scheme);
        Assert.Equal(IPAddress.Parse("10.0.13.1"), configured.Connection.RemoteIpAddress);
    }

    // Real interfaces of the machine running the test: in the Linux suite that is a Docker container, so the
    // subnet found holds the container's own address, which is where its gateway (the Apache relay) sits.
    [Fact]
    public void InterfaceNetworks_EachHoldOneOfTheMachinesOwnAddresses()
    {
        var own = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && adapter.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(unicast => unicast.PrefixLength is > 0 and < 32)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .ToList();

        var networks = ForwardedHeadersConfiguration.InterfaceNetworks();

        // In a Docker container (the Linux suite) the bridge interface always exists: never a vacuous pass there.
        if (OperatingSystem.IsLinux() && File.Exists("/.dockerenv")) Assert.NotEmpty(networks);
        Assert.Equal(own.Count > 0, networks.Count > 0);
        Assert.All(networks, network => Assert.Contains(own, network.Contains));
        Assert.All(own, address => Assert.Contains(networks, network => network.Contains(address)));
    }

    [Theory]
    [InlineData("10.0.13.5", 24, "10.0.13.0/24")]
    [InlineData("172.18.200.9", 16, "172.18.0.0/16")]
    [InlineData("192.168.33.77", 20, "192.168.32.0/20")]
    public void SubnetOf_ClearsTheHostBits(string address, int prefixLength, string expected)
        => Assert.Equal(System.Net.IPNetwork.Parse(expected), ForwardedHeadersConfiguration.SubnetOf(IPAddress.Parse(address), prefixLength));

    [Theory]
    [InlineData("not-a-network")]
    [InlineData("172.16.0.0/40")]
    public void InvalidConfiguredNetwork_Throws(string entry)
    {
        var configuration = Build(new() { [ForwardedHeadersConfiguration.KnownNetworksKey + ":0"] = entry });

        var error = Assert.Throws<InvalidOperationException>(() => ForwardedHeadersConfiguration.CreateOptions(configuration, () => []));
        Assert.Contains(ForwardedHeadersConfiguration.KnownNetworksKey, error.Message, StringComparison.Ordinal);
    }

    private static async Task<HttpContext> RunAsync(IConfiguration configuration, string socketAddress, params string[] containerNetworks)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(socketAddress);
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-For"] = ClaimedClient;
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(ForwardedHeadersConfiguration.CreateOptions(
                configuration, () => containerNetworks.Select(network => System.Net.IPNetwork.Parse(network)))));

        await middleware.Invoke(context);
        return context;
    }

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
