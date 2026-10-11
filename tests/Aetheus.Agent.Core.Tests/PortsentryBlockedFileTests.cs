// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The PortSentry blocked files as PortSentry 1.2 writes them (<c>WriteBlocked</c> in portsentry.c):
/// <c>&lt;epoch&gt; - &lt;MM/DD/YYYY HH:MM:SS&gt; Host: &lt;name&gt;/&lt;ip&gt; Port: &lt;port&gt; &lt;TCP|UDP&gt; Blocked</c>,
/// one file per mode next to <c>BLOCKED_FILE</c> (<c>/var/lib/portsentry/portsentry.blocked.atcp</c> on Debian).
/// The former parser split on '/' and took the first part ("&lt;epoch&gt; - &lt;MM&gt;"), so no real line ever matched.
/// </summary>
public sealed class PortsentryBlockedFileTests
{
    // 1728381045 = 2024-10-08T09:50:45Z; the date part is the host's local time (CEST here).
    private const string TcpLine = "1728381045 - 10/08/2024 11:50:45 Host: scanner.example.net/203.0.113.7 Port: 1080 TCP Blocked";
    private const string UdpLine = "1728381100 - 10/08/2024 11:51:40 Host: 198.51.100.4/198.51.100.4 Port: 161 UDP Blocked";

    [Fact]
    public void ARealTcpLine_GivesTheIp_TheProtocol_ThePort_AndTheEpochAsUtc()
    {
        var blocked = PortsentryCollector.ParseBlockedLine(TcpLine, "UDP");

        Assert.NotNull(blocked);
        Assert.Equal("203.0.113.7", blocked.IpAddress);
        Assert.Equal("TCP", blocked.Protocol);
        Assert.Equal(new DateTime(2024, 10, 8, 9, 50, 45, DateTimeKind.Utc), blocked.BlockedAt);
        Assert.Equal(DateTimeKind.Utc, blocked.BlockedAt.Kind);
        Assert.Contains("1080", blocked.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealUdpLine_WithoutHostResolution_GivesTheIp()
    {
        var blocked = PortsentryCollector.ParseBlockedLine(UdpLine, "TCP");

        Assert.NotNull(blocked);
        Assert.Equal("198.51.100.4", blocked.IpAddress);
        Assert.Equal("UDP", blocked.Protocol);
        Assert.Equal(new DateTime(2024, 10, 8, 9, 51, 40, DateTimeKind.Utc), blocked.BlockedAt);
    }

    [Fact]
    public void AnIpv6Attacker_IsRead()
    {
        var blocked = PortsentryCollector.ParseBlockedLine(
            "1728381045 - 10/08/2024 11:50:45 Host: 2001:db8::7/2001:db8::7 Port: 31337 TCP Blocked", "TCP");

        Assert.Equal("2001:db8::7", blocked?.IpAddress);
    }

    [Fact]
    public void ALineWithADateButNoEpoch_UsesTheHostLocalDate()
    {
        var blocked = PortsentryCollector.ParseBlockedLine("10/08/2024 11:50:45 Host: 203.0.113.7/203.0.113.7 Port: 1080 TCP Blocked", "TCP");

        Assert.NotNull(blocked);
        Assert.Equal(new DateTime(2024, 10, 8, 11, 50, 45, DateTimeKind.Local).ToUniversalTime(), blocked.BlockedAt);
    }

    [Fact]
    public void ABareAddress_IsBlocked_WithAnUnknownDate()
    {
        var blocked = PortsentryCollector.ParseBlockedLine("203.0.113.66", "TCP");

        Assert.NotNull(blocked);
        Assert.Equal("203.0.113.66", blocked.IpAddress);
        Assert.Equal(DateTime.MinValue, blocked.BlockedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# comment")]
    [InlineData("1728381045 - 10/08/2024 11:50:45 Host: scanner.example.net/not-an-ip Port: 1080 TCP Blocked")]
    [InlineData("garbage line")]
    public void ANonBlockedLine_IsSkipped(string line) =>
        Assert.Null(PortsentryCollector.ParseBlockedLine(line, "TCP"));

    [Theory]
    [InlineData("BLOCKED_FILE=\"/var/lib/portsentry/portsentry.blocked\"\n", "/var/lib/portsentry/portsentry.blocked")]
    [InlineData("# BLOCKED_FILE=\"/x\"\nBLOCKED_FILE=/usr/local/psionic/portsentry/portsentry.blocked\n", "/usr/local/psionic/portsentry/portsentry.blocked")]
    [InlineData("TCP_PORTS=\"1,11\"\n", PortsentryCollector.DefaultBlockedFileBase)]
    public void TheBlockedFile_IsTheConfiguredOne_ElseTheDebianDefault(string config, string expected) =>
        Assert.Equal(expected, PortsentryCollector.BlockedFileBase(config));

    [Fact]
    public async Task Collect_ReadsEveryModeFileOfTheConfiguredBlockedFile()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("systemctl", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "active\n", string.Empty));
        shell.RunExecAsync("dpkg", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "ii  portsentry  1.2-14  amd64  port scan detector\n", string.Empty));
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/usr/sbin/portsentry"] = string.Empty,
            ["/etc/portsentry/portsentry.conf"] = "BLOCKED_FILE=\"/var/lib/portsentry/portsentry.blocked\"\nTCP_PORTS=\"1,11\"\n",
            ["/var/lib/portsentry/portsentry.blocked.atcp"] = TcpLine + "\n",
            ["/var/lib/portsentry/portsentry.blocked.udp"] = UdpLine + "\n"
        };

        var result = await new PortsentryCollector(
            NullLogger<PortsentryCollector>.Instance, shell,
            fileExists: files.ContainsKey,
            readAllText: path => files[path],
            readAllTextAsync: (path, _) => Task.FromResult(files[path])).CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.BlockedCount);
        Assert.Equal(["203.0.113.7/TCP", "198.51.100.4/UDP"], result.BlockedIps.Select(b => $"{b.IpAddress}/{b.Protocol}"));
        Assert.DoesNotContain(result.BlockedIps, b => b.BlockedAt == DateTime.MinValue);
    }

    [Fact]
    public async Task Collect_ReadsTheModeTheDebianPackageWritesToItsDefaultsFile()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, string.Empty, string.Empty));
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/usr/sbin/portsentry"] = string.Empty,
            ["/etc/portsentry/portsentry.conf"] = "TCP_PORTS=\"1,11\"\n",
            ["/etc/default/portsentry"] = "TCP_MODE=\"atcp\"\nUDP_MODE=\"audp\"\n"
        };

        var result = await new PortsentryCollector(
            NullLogger<PortsentryCollector>.Instance, shell,
            fileExists: files.ContainsKey,
            readAllText: path => files[path],
            readAllTextAsync: (path, _) => Task.FromResult(files[path])).CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal("atcp", result.Mode);
    }
}
