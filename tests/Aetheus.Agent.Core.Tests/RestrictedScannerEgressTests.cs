// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

public sealed class RestrictedScannerEgressTests
{
    [Fact]
    public async Task StartAsync_NetworkScannerWithoutAllowlistFailsClosed()
    {
        var runner = Substitute.For<IScannerProcessRunner>();
        var scanner = new ScannerManifestEntry { Execution = "container", Network = "bridge" };

        var exception = await Assert.ThrowsAsync<IOException>(() => RestrictedScannerEgress.StartAsync(
            scanner, new Dictionary<string, string>(), runner, (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken));

        Assert.Contains("allowlist", exception.Message, StringComparison.OrdinalIgnoreCase);
        await runner.DidNotReceive().RunAsync(Arg.Any<ProcessStartInfo>(), Arg.Any<int>(),
            Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("0.1.2.3", true)]
    [InlineData("10.20.30.40", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fec0::1", true)]
    [InlineData("ff02::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("169.253.1.1", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.169.1.1", false)]
    [InlineData("223.255.255.255", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void IsPrivateOrSpecial_RefusesEveryNonPublicRange(string address, bool expected) =>
        Assert.Equal(expected, RestrictedScannerEgress.IsPrivateOrSpecial(System.Net.IPAddress.Parse(address)));

    [Fact]
    public void TryParseProxyRequest_Connect_TargetsTheAuthorityAndKeepsTheHeader()
    {
        const string header = "CONNECT Registry.Example.COM.:8443 HTTP/1.1\r\nHost: x\r\n\r\n";

        var request = RestrictedScannerEgress.TryParseProxyRequest(header);

        Assert.Equal(new RestrictedScannerEgress.ProxyRequest(true, "registry.example.com", 8443, header), request);
    }

    [Fact]
    public void TryParseProxyRequest_ConnectWithoutPort_Uses443()
    {
        var request = RestrictedScannerEgress.TryParseProxyRequest("CONNECT api.example.com HTTP/1.1\r\n\r\n");

        Assert.Equal(443, request?.Port);
    }

    [Fact]
    public void TryParseProxyRequest_AbsoluteHttp_IsRewrittenToItsPath()
    {
        var request = RestrictedScannerEgress.TryParseProxyRequest(
            "GET http://Feed.Example.com/v3/index.json?x=1 HTTP/1.1\r\nHost: feed.example.com\r\n\r\n");

        Assert.Equal(
            new RestrictedScannerEgress.ProxyRequest(
                false, "feed.example.com", 80, "GET /v3/index.json?x=1 HTTP/1.1\r\nHost: feed.example.com\r\n\r\n"),
            request);
    }

    [Fact]
    public void TryParseProxyRequest_HttpOnAnotherPort_KeepsThatPort()
    {
        var request = RestrictedScannerEgress.TryParseProxyRequest("GET http://feed.example.com:8080/ HTTP/1.1\r\n\r\n");

        Assert.Equal(8080, request?.Port);
        Assert.StartsWith("GET / HTTP/1.1", request?.Header, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\r\nHost: x\r\n\r\n")]
    [InlineData("GET http://feed.example.com/\r\n\r\n")]
    [InlineData("GET https://feed.example.com/ HTTP/1.1\r\n\r\n")]
    [InlineData("GET /relative HTTP/1.1\r\n\r\n")]
    [InlineData("no line end")]
    public void TryParseProxyRequest_AnythingElse_IsRefused(string header) =>
        Assert.Null(RestrictedScannerEgress.TryParseProxyRequest(header));

    [Fact]
    public async Task ReadHeaderAsync_StopsAtTheBlankLineThatEndsTheHeader()
    {
        using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("CONNECT a:443 HTTP/1.1\r\nHost: a\r\n\r\nTLS-BYTES"));

        var header = await RestrictedScannerEgress.ReadHeaderAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal("CONNECT a:443 HTTP/1.1\r\nHost: a\r\n\r\n", header);
        Assert.Equal('T', (char)stream.ReadByte());
    }

    [Fact]
    public async Task ReadHeaderAsync_AClosedConnectionBeforeTheEnd_GivesNothing()
    {
        using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("CONNECT a:443 HTTP/1.1\r\n"));

        Assert.Null(await RestrictedScannerEgress.ReadHeaderAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadHeaderAsync_AHeaderBeyond32KiB_IsRefused()
    {
        using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nX: " + new string('a', 40_000) + "\r\n\r\n"));

        Assert.Null(await RestrictedScannerEgress.ReadHeaderAsync(stream, TestContext.Current.CancellationToken));
    }
}
