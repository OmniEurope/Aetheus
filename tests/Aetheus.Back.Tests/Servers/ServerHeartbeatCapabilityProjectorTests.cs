// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Servers;

/// <summary>Capability flags self-heal from every heartbeat: sudoers grants only from an authoritative
/// inventory, the rest (port observation, Docker, TLS, pipeline runner gate) from the beat itself.</summary>
public sealed class ServerHeartbeatCapabilityProjectorTests
{
    [Fact]
    public void Capabilities_AreStoredDistinctAndSorted()
    {
        var server = new Server();

        ServerHeartbeatCapabilityProjector.Apply(server, new ServerHeartbeatDto
        {
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilities = ["b", " ", "a", "b"],
        });

        Assert.Equal(AgentProtocol.CurrentVersion, server.AgentProtocolVersion);
        Assert.Equal("[\"a\",\"b\"]", server.AgentCapabilitiesJson);
    }

    [Fact]
    public void NonAuthoritativeInventory_KeepsTheGrantFlags()
    {
        var server = new Server { PackageManagementAvailable = true, MailSetupAvailable = true };

        ServerHeartbeatCapabilityProjector.Apply(server, new ServerHeartbeatDto { SudoersInventoryAvailable = null });

        Assert.True(server.PackageManagementAvailable);
        Assert.True(server.MailSetupAvailable);
    }

    [Fact]
    public void AuthoritativeInventory_DerivesEachGrantFlag()
    {
        var server = new Server { MailSetupAvailable = true };

        ServerHeartbeatCapabilityProjector.Apply(server, new ServerHeartbeatDto
        {
            SudoersInventoryAvailable = true,
            AgentCapabilities = [AgentCapabilities.Deployment],
            SudoersHashes = new() { ["aetheus-package"] = "h1", ["aetheus-firewall"] = "h2" },
        });

        Assert.True(server.PackageManagementAvailable);
        Assert.True(server.DeploymentTargetAvailable);
        Assert.True(server.FirewallManagementAvailable);
        Assert.False(server.MailSetupAvailable);
        Assert.False(server.TeamspeakSetupAvailable);
        Assert.False(server.PatchManagementAvailable);
    }

    [Fact]
    public void PortObservationDockerAndTls_FollowTheBeatBothWays()
    {
        var server = new Server();
        var on = new ServerHeartbeatDto
        {
            AgentCapabilities = [AgentCapabilities.PortObservation],
            DockerAvailable = true,
            InsecureTls = true,
        };

        ServerHeartbeatCapabilityProjector.Apply(server, on);
        Assert.True(server.PortObservationAvailable);
        Assert.True(server.DockerAvailable);
        Assert.True(server.InsecureTls);

        ServerHeartbeatCapabilityProjector.Apply(server, new ServerHeartbeatDto());
        Assert.False(server.PortObservationAvailable);
        Assert.False(server.DockerAvailable);
        Assert.False(server.InsecureTls);
    }

    [Fact]
    public void EligibleRunner_StaysEnabled()
    {
        var server = new Server { PipelineRunnerEnabled = true };

        ServerHeartbeatCapabilityProjector.Apply(server, new ServerHeartbeatDto
        {
            PipelineRunnerAvailable = true,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilities = [AgentCapabilities.PipelineBuild],
        });

        Assert.True(server.PipelineRunnerEnabled);
    }

    [Theory]
    [InlineData(false, AgentProtocol.CurrentVersion, true)]
    [InlineData(true, null, true)]
    [InlineData(true, AgentProtocol.CurrentVersion + 1, true)]
    [InlineData(true, AgentProtocol.CurrentVersion, false)]
    public void IneligibleRunner_IsDisabled(bool runnerAvailable, int? protocol, bool buildCapability)
    {
        var server = new Server { PipelineRunnerEnabled = true };

        ServerHeartbeatCapabilityProjector.Apply(server, new ServerHeartbeatDto
        {
            PipelineRunnerAvailable = runnerAvailable,
            AgentProtocolVersion = protocol,
            AgentCapabilities = buildCapability ? [AgentCapabilities.PipelineBuild] : [],
        });

        Assert.False(server.PipelineRunnerEnabled);
    }
}
