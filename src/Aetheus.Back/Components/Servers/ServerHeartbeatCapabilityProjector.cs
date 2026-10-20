// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

internal static class ServerHeartbeatCapabilityProjector
{
    public static void Apply(Server server, ServerHeartbeatDto heartbeat)
    {
        if (server.AgentProtocolVersion != heartbeat.AgentProtocolVersion)
            server.AgentProtocolVersion = heartbeat.AgentProtocolVersion;

        ApplyCapabilityList(server, heartbeat);
        ApplySudoersGrants(server, heartbeat);

        // Not gated on the sudoers inventory: the scan needs no grant, only an agent recent enough to
        // publish the capability, so the flag must self-heal from the capability list alone.
        var portObservation = HasCapability(heartbeat, AgentCapabilities.PortObservation);
        if (server.PortObservationAvailable != portObservation)
            server.PortObservationAvailable = portObservation;

        if (server.DockerAvailable != heartbeat.DockerAvailable)
            server.DockerAvailable = heartbeat.DockerAvailable;
        if (!IsPipelineRunnerEligible(heartbeat) && server.PipelineRunnerEnabled)
            server.PipelineRunnerEnabled = false;
        if (server.InsecureTls != heartbeat.InsecureTls)
            server.InsecureTls = heartbeat.InsecureTls;
    }

    private static void ApplyCapabilityList(Server server, ServerHeartbeatDto heartbeat)
    {
        var normalizedCapabilities = heartbeat.AgentCapabilities?
            .Where(capability => !string.IsNullOrWhiteSpace(capability))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList() ?? [];
        var capabilitiesJson = JsonSerializer.Serialize(normalizedCapabilities);
        if (server.AgentCapabilitiesJson != capabilitiesJson)
            server.AgentCapabilitiesJson = capabilitiesJson;
    }

    private static void ApplySudoersGrants(Server server, ServerHeartbeatDto heartbeat)
    {
        var sudoersInventoryAuthoritative = heartbeat.SudoersInventoryAvailable == true
            || heartbeat.SudoersHashes.Count > 0;
        if (!sudoersInventoryAuthoritative)
            return;

        server.PackageManagementAvailable = heartbeat.SudoersHashes.ContainsKey("aetheus-package");
        server.DeploymentTargetAvailable = HasCapability(heartbeat, AgentCapabilities.Deployment);
        server.MailSetupAvailable = heartbeat.SudoersHashes.ContainsKey("aetheus-mail");
        server.TeamspeakSetupAvailable = heartbeat.SudoersHashes.ContainsKey("aetheus-teamspeak");
        server.PatchManagementAvailable = heartbeat.SudoersHashes.ContainsKey("aetheus-patch");
        server.FirewallManagementAvailable = heartbeat.SudoersHashes.ContainsKey("aetheus-firewall");
    }

    private static bool IsPipelineRunnerEligible(ServerHeartbeatDto heartbeat) =>
        heartbeat.PipelineRunnerAvailable == true
        && heartbeat.AgentProtocolVersion is { } protocol
        && AgentProtocol.IsSupported(protocol)
        && HasCapability(heartbeat, AgentCapabilities.PipelineBuild);

    private static bool HasCapability(ServerHeartbeatDto heartbeat, string capability) =>
        heartbeat.AgentCapabilities?.Contains(capability, StringComparer.Ordinal) == true;
}
