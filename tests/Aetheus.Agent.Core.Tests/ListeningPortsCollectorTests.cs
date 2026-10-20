// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary> The scan reads TCP and UDP, and keeps them apart. Both platform probes are stubbed in
/// every test so the assertions hold wherever the suite runs; the collector picks one pair by OS
/// and the other pair is simply never called. </summary>
public class ListeningPortsCollectorTests
{
    private readonly IShellRunner _shell = Substitute.For<IShellRunner>();

    private ListeningPortsCollector Build() =>
        new(NullLogger<ListeningPortsCollector>.Instance, _shell);

    /// <summary>Answers the probe whose argv contains <paramref name="marker"/>.</summary>
    private void Probe(string marker, string stdout, int exitCode = 0)
    {
        _shell
            .RunExecAsync(
                Arg.Any<string>(),
                Arg.Is<IReadOnlyList<string>>(args =>
                    args.Any(arg => arg.Contains(marker, StringComparison.Ordinal))),
                Arg.Any<CancellationToken>(),
                Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(exitCode, stdout, string.Empty));
    }

    private void NoDocker() => Probe("ps", string.Empty, exitCode: 1);

    [Fact]
    public async Task CollectAsync_ReportsTheSameNumberOnBothProtocols()
    {
        NoDocker();
        Probe("-ltnpH", "LISTEN 0 4096 0.0.0.0:53 0.0.0.0:* users:((\"nginx\",pid=1,fd=6))\n");
        Probe("-lunpH", "UNCONN 0 0 0.0.0.0:53 0.0.0.0:* users:((\"dnsmasq\",pid=2,fd=4))\n");
        Probe("Get-NetTCPConnection", "\"LocalAddress\",\"LocalPort\"\n\"0.0.0.0\",\"53\"\n");
        Probe("Get-NetUDPEndpoint", "\"LocalAddress\",\"LocalPort\"\n\"0.0.0.0\",\"53\"\n");

        var observed = await Build().CollectAsync(CancellationToken.None);

        // 53/tcp and 53/udp are two occupied ports, not one: collapsing them would hide whichever the
        // dictionary saw second, and would let a UDP listener refuse a TCP deployment.
        Assert.Equal(2, observed.Count);
        Assert.Contains(observed, port => port is { Port: 53, Protocol: "tcp" });
        Assert.Contains(observed, port => port is { Port: 53, Protocol: "udp" });
    }

    [Fact]
    public async Task CollectAsync_KeepsTcpWhenTheUdpProbeFails()
    {
        NoDocker();
        Probe("-ltnpH", "LISTEN 0 4096 0.0.0.0:8080 0.0.0.0:*\n");
        Probe("Get-NetTCPConnection", "\"LocalAddress\",\"LocalPort\"\n\"0.0.0.0\",\"8080\"\n");
        Probe("-lunpH", string.Empty, exitCode: 2);
        Probe("Get-NetUDPEndpoint", string.Empty, exitCode: 2);

        var observed = await Build().CollectAsync(CancellationToken.None);

        // A host without the UDP probe (older ss, cmdlet unavailable) still reports its TCP ports:
        // discarding the whole scan would erase every observation the backend already had.
        var only = Assert.Single(observed);
        Assert.Equal(8080, only.Port);
        Assert.Equal("tcp", only.Protocol);
    }

    [Fact]
    public async Task CollectAsync_NamesTheUdpHolderFromItsOwnDockerMapping()
    {
        Probe("-ltnpH", string.Empty);
        Probe("Get-NetTCPConnection", "\"LocalAddress\",\"LocalPort\"\n");
        Probe("-lunpH", "UNCONN 0 0 0.0.0.0:5353 0.0.0.0:*\n");
        Probe("Get-NetUDPEndpoint", "\"LocalAddress\",\"LocalPort\"\n\"0.0.0.0\",\"5353\"\n");
        Probe("ps", "resolver|0.0.0.0:5353->53/udp\nweb|0.0.0.0:8080->80/tcp\n");

        var observed = await Build().CollectAsync(CancellationToken.None);

        var only = Assert.Single(observed);
        Assert.Equal("resolver", only.Holder);
        Assert.Equal("udp", only.Protocol);
    }
}
