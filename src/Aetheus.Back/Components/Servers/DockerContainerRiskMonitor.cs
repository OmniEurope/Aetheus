// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Servers;

/// <summary>
/// PLAN-010 lot 1: raises a Sec-Audit alert per container whose host-escape settings (privileged, Docker
/// socket, host PID or network, dangerous capabilities) its Aetheus role does not explain. The alerted
/// state of each container is kept on the server (<see cref="Server.DockerRiskAlertedJson"/>), as the
/// sudoers drift keeps its fingerprint: a container is alerted when first seen so and when its settings
/// change, and stays quiet while they do not. A container the agent could not inspect keeps its previous
/// state (unknown is never read as clean), and a heartbeat with no inspected container changes nothing.
/// </summary>
internal sealed class DockerContainerRiskMonitor(
    IHubContext<AlertHub> alertHub,
    TimeProvider timeProvider,
    IDomainEventDispatcher? domainEvents,
    ILogger logger)
{
    internal const string RuleName = "Sec-Audit";
    internal const string Metric = "DockerContainerRisk";

    /// <returns>Whether <see cref="Server.DockerRiskAlertedJson"/> changed and must be saved.</returns>
    public async Task<bool> CheckAsync(Server server, IReadOnlyList<DockerContainerDto> containers, CancellationToken ct)
    {
        if (!containers.Any(c => c.SecurityInspected)) return false;
        var previous = Read(server.DockerRiskAlertedJson);
        var current = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var raised = new List<(DockerContainerDto Container, DockerContainerRisks Unexpected, bool IsChange)>();
        foreach (var container in containers)
        {
            var key = string.IsNullOrEmpty(container.Name) ? container.ContainerId : container.Name;
            if (!container.SecurityInspected)
            {
                if (previous.TryGetValue(key, out var kept)) current[key] = kept;
                continue;
            }
            var unexpected = DockerContainerSecurity.UnexpectedRisks(container);
            if (unexpected == DockerContainerRisks.None) continue;
            var fingerprint = Fingerprint(unexpected, container.DangerousCapabilities);
            current[key] = fingerprint;
            if (!previous.TryGetValue(key, out var before))
                raised.Add((container, unexpected, false));
            else if (!string.Equals(before, fingerprint, StringComparison.Ordinal))
                raised.Add((container, unexpected, true));
        }

        var json = current.Count == 0 ? null : JsonSerializer.Serialize(current);
        var changed = !string.Equals(server.DockerRiskAlertedJson, json, StringComparison.Ordinal);
        server.DockerRiskAlertedJson = json;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var (container, unexpected, isChange) in raised)
            await RaiseAsync(server, container, unexpected, isChange, now, ct).ConfigureAwait(false);
        return changed;
    }

    internal static string Fingerprint(DockerContainerRisks unexpected, string dangerousCapabilities) =>
        $"{(int)unexpected}|{dangerousCapabilities}";

    internal static string Describe(DockerContainerRisks unexpected, string dangerousCapabilities)
    {
        var parts = new List<string>();
        if (unexpected.HasFlag(DockerContainerRisks.Privileged)) parts.Add("privileged");
        if (unexpected.HasFlag(DockerContainerRisks.DockerSocket)) parts.Add("Docker socket mounted");
        if (unexpected.HasFlag(DockerContainerRisks.HostPid)) parts.Add("host PID namespace");
        if (unexpected.HasFlag(DockerContainerRisks.HostNetwork)) parts.Add("host network");
        if (unexpected.HasFlag(DockerContainerRisks.DangerousCapabilities)) parts.Add($"capabilities {dangerousCapabilities}");
        return string.Join(", ", parts);
    }

    private async Task RaiseAsync(
        Server server, DockerContainerDto container, DockerContainerRisks unexpected, bool isChange, DateTime now,
        CancellationToken ct)
    {
        var risks = Describe(unexpected, container.DangerousCapabilities);
        var severity = DockerContainerSecurity.IsCritical(unexpected, container.DangerousCapabilities) ? "Critical" : "Warning";
        var message = $"Container {container.Name} ({container.Image}) {(isChange ? "now runs" : "runs")} with host access: "
                      + $"{risks}. Verify it is intended.";
        logger.LogWarning(
            "Docker container risk on server {ServerId} ({ServerName}): {Container} {Risks}{Change}",
            server.Id, server.Name, container.Name, risks, isChange ? " (changed)" : string.Empty);
        await alertHub.Clients.Group(HubGroups.Alerts).SendAsync("AlertTriggered", new AlertTriggeredDto
        {
            RuleName = RuleName,
            ServerId = server.Id,
            ServerName = server.Name,
            Metric = Metric,
            Severity = severity,
            Message = message,
            TriggeredAt = now
        }, ct).ConfigureAwait(false);
        // Awaited, observer semantics: a handler failure is logged by the dispatcher and the heartbeat goes on.
        if (domainEvents is not null)
        {
            await domainEvents.DispatchAsync(new DockerContainerRiskDetectedEvent(
                server.Id, server.Name, container.Name, container.Image, risks, severity, message, isChange, now), ct)
                .ConfigureAwait(false);
        }
    }

    private static Dictionary<string, string> Read(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }
}
