// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Use captured command output to verify listening-port parsing independently of process
/// execution, because a misparsed line can incorrectly reserve a port.</summary>
public class ListeningPortsParserTests
{
    private const string SsOutput = """
        LISTEN 0      4096         0.0.0.0:22        0.0.0.0:*    users:(("sshd",pid=812,fd=3))
        LISTEN 0      511          0.0.0.0:10031     0.0.0.0:*    users:(("docker-proxy",pid=2201,fd=4))
        LISTEN 0      4096       127.0.0.1:15432     0.0.0.0:*
        LISTEN 0      511             [::]:80           [::]:*    users:(("nginx",pid=901,fd=6))
        LISTEN 0      128  [fe80::1%eth0]:53           [::]:*
        """;

    [Fact]
    public void ParseSs_ReadsPortAddressAndProcess()
    {
        var parsed = ListeningPortsParser.ParseSs(SsOutput);

        Assert.Equal(5, parsed.Count);
        Assert.Contains(parsed, entry => entry is { Port: 22, Interface: "0.0.0.0", Process: "sshd" });
        Assert.Contains(parsed, entry => entry is { Port: 80, Interface: "[::]", Process: "nginx" });
        Assert.Contains(parsed, entry => entry is { Port: 53, Interface: "[fe80::1%eth0]" });
    }

    [Fact]
    public void ParseSs_KeepsLoopbackListeners()
    {
        // A loopback listener still occupies the host port for a container that wants to publish it,
        // so dropping it would report 15432 as free while `docker up` fails on it.
        var parsed = ListeningPortsParser.ParseSs(SsOutput);

        var loopback = Assert.Single(parsed, entry => entry.Port == 15432);
        Assert.Equal("127.0.0.1", loopback.Interface);
        Assert.Null(loopback.Process);
    }

    [Fact]
    public void ParseSs_IgnoresHeaderAndGarbage()
    {
        var parsed = ListeningPortsParser.ParseSs(
            "State Recv-Q Send-Q Local-Address:Port Peer-Address:Port\nnonsense\n\n");

        Assert.Empty(parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseSs_EmptyInput_YieldsNothing(string? output)
        => Assert.Empty(ListeningPortsParser.ParseSs(output));

    [Fact]
    public void ParseEndpointCsv_ReadsAddressAndPort()
    {
        var parsed = ListeningPortsParser.ParseEndpointCsv("""
            "LocalAddress","LocalPort"
            "0.0.0.0","445"
            "::","5985"
            "127.0.0.1","15432"
            """);

        Assert.Equal(3, parsed.Count);
        Assert.Contains(parsed, entry => entry is { Port: 445, Interface: "0.0.0.0" });
        Assert.Contains(parsed, entry => entry is { Port: 5985, Interface: "::" });
    }

    [Fact]
    public void ParseEndpointCsv_SkipsRowsWithoutAUsablePort()
    {
        var parsed = ListeningPortsParser.ParseEndpointCsv("""
            "LocalAddress","LocalPort"
            "0.0.0.0","not-a-port"
            "0.0.0.0","0"
            "0.0.0.0","70000"
            "0.0.0.0","8080"
            """);

        var only = Assert.Single(parsed);
        Assert.Equal(8080, only.Port);
    }

    [Fact]
    public void ParseDockerPortMap_KeepsPublishedHostPortsOnly()
    {
        var map = ListeningPortsParser.ParseDockerPortMap("""
            portfolio-prod-front|0.0.0.0:10031->80/tcp, [::]:10031->80/tcp
            aetheus-back|0.0.0.0:10041->8080/tcp
            worker|9000/tcp
            """);

        Assert.Equal(2, map.Count);
        Assert.Equal("portfolio-prod-front", map[(10031, "tcp")]);
        Assert.Equal("aetheus-back", map[(10041, "tcp")]);
        // 9000 is a container port with no host mapping: invisible to the host, so not an occupied port.
        Assert.DoesNotContain((9000, "tcp"), map.Keys);
    }

    [Fact]
    public void ParseDockerPortMap_KeepsTcpAndUdpOnTheSameNumberApart()
    {
        var map = ListeningPortsParser.ParseDockerPortMap(
            "resolver|0.0.0.0:53->53/udp, 0.0.0.0:53->53/tcp\ndns-ui|0.0.0.0:5353->53/udp");

        Assert.Equal("resolver", map[(53, "udp")]);
        Assert.Equal("resolver", map[(53, "tcp")]);
        Assert.Equal("dns-ui", map[(5353, "udp")]);
        Assert.DoesNotContain((5353, "tcp"), map.Keys);
    }

    [Fact]
    public void ParseDockerPortMap_IgnoresMalformedLines()
    {
        var map = ListeningPortsParser.ParseDockerPortMap("no-separator\n|orphan\n\n");

        Assert.Empty(map);
    }
}
