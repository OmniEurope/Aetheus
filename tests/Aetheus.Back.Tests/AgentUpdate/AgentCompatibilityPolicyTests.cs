// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Servers;

namespace Aetheus.Back.Tests;

public class AgentCompatibilityPolicyTests
{
    private readonly AgentCompatibilityPolicy _sut = new(new TestReleaseCatalog());

    [Fact]
    public void Evaluate_NoHeartbeat_IsUnknown()
    {
        var result = _sut.Evaluate(new ServerDto());

        Assert.Equal(AgentCompatibilityStatus.Unknown, result.Status);
        Assert.Equal(AgentCompatibilityReason.NoHeartbeat, result.Reason);
    }

    [Fact]
    public void Evaluate_CurrentContractAndCapabilities_IsUpToDate()
    {
        var result = _sut.Evaluate(CurrentServer());

        Assert.Equal(AgentCompatibilityStatus.UpToDate, result.Status);
        Assert.Empty(result.MissingCapabilities);
    }

    [Fact]
    public void Evaluate_SupportedOlderSemVer_IsRecommended()
    {
        var server = CurrentServer() with { AgentVersion = "0.9.0" };

        var result = _sut.Evaluate(server);

        Assert.Equal(AgentCompatibilityStatus.UpdateRecommended, result.Status);
        Assert.Equal(AgentCompatibilityReason.OlderSoftware, result.Reason);
    }

    /// <summary>Verifies numeric version-component ordering and prerelease precedence, including
    /// 1.3.0 after 1.2.2553 and nightly builds below their releases.</summary>
    [Theory]
    [InlineData("1.2.2553", "1.3.0", AgentCompatibilityStatus.UpdateRecommended)]
    [InlineData("1.3.0", "1.3.1", AgentCompatibilityStatus.UpdateRecommended)]
    [InlineData("1.3.0-nightly", "1.3.0", AgentCompatibilityStatus.UpdateRecommended)]
    [InlineData("1.3.1", "1.3.0", AgentCompatibilityStatus.UpToDate)]
    // A new series restarts its number at 0: 1.4.0 is above 1.3.16, and not below the 1.4.x agents.
    [InlineData("1.3.16", "1.4.0", AgentCompatibilityStatus.UpdateRecommended)]
    [InlineData("1.4.0", "1.3.16", AgentCompatibilityStatus.UpToDate)]
    [InlineData("1.4.1", "1.4.0", AgentCompatibilityStatus.UpToDate)]
    public void Evaluate_ReleaseNumberVersions_CompareNumerically(string installed, string released, AgentCompatibilityStatus expected)
    {
        var policy = new AgentCompatibilityPolicy(new TestReleaseCatalog(released));

        var result = policy.Evaluate(CurrentServer() with { AgentVersion = installed });

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void Evaluate_UnsupportedProtocol_IsRequired()
    {
        var server = CurrentServer() with { AgentProtocolVersion = 0 };

        var result = _sut.Evaluate(server);

        Assert.Equal(AgentCompatibilityStatus.UpdateRequired, result.Status);
        Assert.Equal(AgentCompatibilityReason.UnsupportedProtocol, result.Reason);
    }

    [Fact]
    public void Evaluate_PreviousProtocol_IsRequired()
    {
        var server = CurrentServer() with { AgentProtocolVersion = AgentProtocol.CurrentVersion - 1 };

        var result = _sut.Evaluate(server);

        Assert.Equal(AgentCompatibilityStatus.UpdateRequired, result.Status);
        Assert.Equal(AgentCompatibilityReason.UnsupportedProtocol, result.Reason);
    }

    [Fact]
    public void Evaluate_MissingRequiredCapability_IsRequired()
    {
        var server = CurrentServer() with
        {
            PipelineRunnerEnabled = true,
            AgentCapabilities = [AgentCapabilities.SelfUpdate]
        };

        var result = _sut.Evaluate(server);

        Assert.Equal(AgentCompatibilityStatus.UpdateRequired, result.Status);
        Assert.Contains(AgentCapabilities.PipelineBuild, result.MissingCapabilities);
    }

    [Fact]
    public void CurrentProtocolFixture_DeserializesAdditively()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "agent-protocol", "v2-heartbeat.json");
        var heartbeat = JsonSerializer.Deserialize<ServerHeartbeatDto>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(heartbeat);
        Assert.Equal(AgentProtocol.CurrentVersion, heartbeat.AgentProtocolVersion);
        Assert.Contains(AgentCapabilities.SelfUpdate, heartbeat.AgentCapabilities!);
    }

    [Fact]
    public void AgentUpdateWaitingResponse_DeserializesWithFrozenNMinusOneNonNullableTaskId()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "agent-protocol",
            "agent-update-waiting-v2.json");

        var response = JsonSerializer.Deserialize<NMinusOneAgentUpdateResponse>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(response);
        Assert.Equal(42, response.RequestId);
        Assert.Equal(0, response.TaskId);
        Assert.Equal("WaitingForIdle", response.Status);
    }

    [Fact]
    public void CanExecute_AnOnlineAgentOnTheCurrentProtocolWithTheCapability_CanRunIt() =>
        Assert.True(_sut.CanExecute(CurrentServer(), AgentCapabilities.SelfUpdate));

    [Fact]
    public void CanExecute_AnOfflineAgent_CannotRunAnything() =>
        Assert.False(_sut.CanExecute(CurrentServer() with { Status = ServerStatus.Offline }, AgentCapabilities.SelfUpdate));

    [Fact]
    public void CanExecute_AnAgentThatNeverReportedItsProtocol_CannotRunAnything() =>
        Assert.False(_sut.CanExecute(CurrentServer() with { AgentProtocolVersion = null }, AgentCapabilities.SelfUpdate));

    [Fact]
    public void CanExecute_AnAgentOnAnUnsupportedProtocol_CannotRunAnything() =>
        Assert.False(_sut.CanExecute(CurrentServer() with { AgentProtocolVersion = 0 }, AgentCapabilities.SelfUpdate));

    [Fact]
    public void CanExecute_AnAgentWithoutTheCapability_CannotRunIt() =>
        Assert.False(_sut.CanExecute(CurrentServer(), AgentCapabilities.Deployment));

    private static ServerDto CurrentServer() => new()
    {
        AgentVersion = "1.0.0",
        AgentProtocolVersion = AgentProtocol.CurrentVersion,
        AgentCapabilities = [AgentCapabilities.SelfUpdate],
        LastHeartbeat = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc),
        Status = ServerStatus.Online
    };

    private sealed class TestReleaseCatalog(string softwareVersion = "1.0.0") : IAgentReleaseCatalog
    {
        public AgentReleaseManifestDto Current { get; } = new()
        {
            SoftwareVersion = softwareVersion,
            ProtocolVersion = AgentProtocol.CurrentVersion,
            MinimumSupportedProtocol = AgentProtocol.MinimumSupportedVersion,
            MaximumSupportedProtocol = AgentProtocol.MaximumSupportedVersion,
            SoftwareCapabilities = [.. AgentCapabilities.SoftwareCapabilities],
            Commit = "test"
        };
    }

    private sealed record NMinusOneAgentUpdateResponse
    {
        public int RequestId { get; init; }
        public int TaskId { get; init; }
        public string Status { get; init; } = string.Empty;
        public string TargetVersion { get; init; } = string.Empty;
        public int BlockingTaskCount { get; init; }
        public bool Created { get; init; }
    }
}
