// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Sockets;
using Aetheus.Back.Components.Shared;

namespace Aetheus.Back.Tests;

/// <summary>
/// The fire-time host check of webhooks and notification channels, over an injected resolver: no DNS
/// query leaves the test.
/// </summary>
public class PublicHostCheckTests
{
    private static PublicHostCheck Resolving(params string[] addresses) =>
        new((_, _) => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray()));

    [Theory]
    [InlineData("93.184.216.34", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::1", false)]
    public async Task AnAddressLiteral_IsJudgedWithoutResolving(string host, bool expected)
    {
        var resolved = false;
        var check = new PublicHostCheck((_, _) =>
        {
            resolved = true;
            return Task.FromResult(Array.Empty<IPAddress>());
        });

        Assert.Equal(expected, await check.IsPublicAsync(host, TestContext.Current.CancellationToken));
        Assert.False(resolved);
    }

    [Fact]
    public async Task ANameResolvingToPublicAddressesOnly_IsPublic()
    {
        string? asked = null;
        var check = new PublicHostCheck((host, _) =>
        {
            asked = host;
            return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("2606:2800:220:1::1") });
        });

        Assert.True(await check.IsPublicAsync("hooks.example.com", TestContext.Current.CancellationToken));
        Assert.Equal("hooks.example.com", asked);
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("93.184.216.34,192.168.1.10")]
    public async Task ANameWithAPrivateAnswer_IsRefused(string addresses) =>
        Assert.False(await Resolving(addresses.Split(',')).IsPublicAsync("hooks.example.com", TestContext.Current.CancellationToken));

    [Fact]
    public async Task ANameWithNoAnswer_IsRefused() =>
        Assert.False(await Resolving().IsPublicAsync("hooks.example.com", TestContext.Current.CancellationToken));

    [Fact]
    public async Task ANameThatDoesNotResolve_IsRefused()
    {
        var check = new PublicHostCheck((_, _) => throw new SocketException((int)SocketError.HostNotFound));

        Assert.False(await check.IsPublicAsync("missing.example.com", TestContext.Current.CancellationToken));
    }
}
