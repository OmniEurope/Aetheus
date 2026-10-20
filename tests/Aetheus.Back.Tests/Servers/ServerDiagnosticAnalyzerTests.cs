// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Servers;

/// <summary>The "why is this server offline?" synthesis: token problems are reported before heartbeat
/// problems, and the compatibility verdict maps onto a tri-state flag.</summary>
public sealed class ServerDiagnosticAnalyzerTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServerDiagnosticAnalyzer Analyzer(Server? server, IAgentCompatibilityPolicy? policy = null)
    {
        var repo = Substitute.For<IServerRepository>();
        repo.FindServerWithTokensAsync(1, Arg.Any<CancellationToken>()).Returns(server);
        return new ServerDiagnosticAnalyzer(repo, new FakeTimeProvider(new DateTimeOffset(Now)), policy);
    }

    private static Server ServerWith(DateTime lastHeartbeat, params ServerToken[] tokens)
    {
        var server = new Server { Id = 1, Name = "vps", Hostname = "vps", LastHeartbeat = lastHeartbeat };
        foreach (var token in tokens) server.Tokens.Add(token);
        return server;
    }

    private static ServerToken Token(DateTime expiresAt, bool revoked = false, int createdDaysAgo = 1) =>
        new() { TokenHash = Guid.NewGuid().ToString("N"), ExpiresAt = expiresAt, IsRevoked = revoked, CreatedAt = Now.AddDays(-createdDaysAgo) };

    private static IAgentCompatibilityPolicy PolicyReturning(AgentCompatibilityStatus status)
    {
        var policy = Substitute.For<IAgentCompatibilityPolicy>();
        policy.Evaluate(Arg.Any<ServerDto>()).Returns(new AgentCompatibilityDto { Status = status });
        return policy;
    }

    [Fact]
    public async Task UnknownServer_YieldsNoDiagnostic() =>
        Assert.Null(await Analyzer(server: null).DiagnoseAsync(1, Ct));

    [Fact]
    public async Task RecentHeartbeatAndValidToken_IsHealthy()
    {
        var server = ServerWith(Now.AddSeconds(-20), Token(Now.AddDays(10)));

        var dto = await Analyzer(server).DiagnoseAsync(1, Ct);

        Assert.True(dto!.TokenValid);
        Assert.Equal(20, dto.SecondsSinceLastHeartbeat);
        Assert.Equal(10, dto.TokenDaysRemaining);
        Assert.Equal("Healthy - last heartbeat 20 s ago.", dto.Summary);
    }

    [Theory]
    [InlineData(61, "No heartbeat for 1 m 1 s")]
    [InlineData(300, "No heartbeat for 5 m 0 s")]
    [InlineData(7200, "No heartbeat for 2 h 0 m")]
    [InlineData(90000, "No heartbeat for 1 d 1 h")]
    public async Task StaleHeartbeat_IsReportedWithAReadableSpan(int secondsAgo, string expectedStart)
    {
        var server = ServerWith(Now.AddSeconds(-secondsAgo), Token(Now.AddDays(10)));

        var dto = await Analyzer(server).DiagnoseAsync(1, Ct);

        Assert.StartsWith(expectedStart, dto!.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredToken_IsReportedBeforeAnyHeartbeatProblem()
    {
        var server = ServerWith(Now.AddHours(-3), Token(Now.AddDays(-2)));

        var dto = await Analyzer(server).DiagnoseAsync(1, Ct);

        Assert.False(dto!.TokenValid);
        Assert.Equal(-2, dto.TokenDaysRemaining);
        Assert.Equal("Agent token expired 2 d 0 h ago - re-enroll the agent.", dto.Summary);
    }

    [Fact]
    public async Task RenewedToken_TheNewestActiveOneDecidesTheExpiry()
    {
        var server = ServerWith(
            Now.AddSeconds(-5),
            Token(Now.AddDays(3), revoked: true, createdDaysAgo: 10),
            Token(Now.AddDays(20), createdDaysAgo: 2),
            Token(Now.AddDays(7), createdDaysAgo: 5));

        var dto = await Analyzer(server).DiagnoseAsync(1, Ct);

        Assert.True(dto!.TokenValid);
        Assert.Equal(20, dto.TokenDaysRemaining);
    }

    [Fact]
    public async Task NoTokenEver_AsksToEnrollTheAgent()
    {
        var dto = await Analyzer(ServerWith(Now.AddSeconds(-5))).DiagnoseAsync(1, Ct);

        Assert.False(dto!.TokenValid);
        Assert.Null(dto.TokenDaysRemaining);
        Assert.Equal("No agent token has ever been issued for this server - enroll the agent.", dto.Summary);
    }

    [Fact]
    public async Task ValidTokenButNeverReported_AsksToCheckTheAgentService()
    {
        var dto = await Analyzer(ServerWith(DateTime.MinValue, Token(Now.AddDays(10)))).DiagnoseAsync(1, Ct);

        Assert.Null(dto!.LastHeartbeatUtc);
        Assert.Null(dto.SecondsSinceLastHeartbeat);
        Assert.StartsWith("The agent has never reported.", dto.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAPolicy_CompatibilityIsKnownOnlyOnceTheAgentReported()
    {
        var reported = await Analyzer(ServerWith(Now.AddSeconds(-5), Token(Now.AddDays(1)))).DiagnoseAsync(1, Ct);
        var silent = await Analyzer(ServerWith(DateTime.MinValue, Token(Now.AddDays(1)))).DiagnoseAsync(1, Ct);

        Assert.True(reported!.VersionsCompatible);
        Assert.Null(silent!.VersionsCompatible);
    }

    [Theory]
    [InlineData(AgentCompatibilityStatus.UpdateRequired, false)]
    [InlineData(AgentCompatibilityStatus.Unknown, null)]
    [InlineData(AgentCompatibilityStatus.UpToDate, true)]
    [InlineData(AgentCompatibilityStatus.UpdateRecommended, true)]
    public async Task CompatibilityVerdict_MapsOntoTheTriStateFlag(AgentCompatibilityStatus status, bool? expected)
    {
        var server = ServerWith(Now.AddSeconds(-5), Token(Now.AddDays(1)));

        var dto = await Analyzer(server, PolicyReturning(status)).DiagnoseAsync(1, Ct);

        Assert.Equal(expected, dto!.VersionsCompatible);
        Assert.Equal(status, dto.AgentCompatibility!.Status);
    }
}
