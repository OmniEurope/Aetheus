// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Servers;

/// <summary>
/// PLAN-010 lot 1: keeps the last SSH snapshot of a server and raises a Sec-Audit alert when a
/// heartbeat reports a different one. The first snapshot is the baseline and raises nothing. An
/// agent that did not collect (legacy agent, Windows, no sshd) leaves the stored state untouched.
/// </summary>
internal sealed class SshConfigMonitor(
    IHubContext<AlertHub> alertHub,
    TimeProvider timeProvider,
    IDomainEventDispatcher? domainEvents,
    ILogger logger)
{
    internal const string Metric = "SshConfigChange";

    /// <returns>Whether the stored state changed and must be saved.</returns>
    public async Task<bool> CheckAsync(Server server, ServerHeartbeatDto heartbeat, CancellationToken ct)
    {
        if (heartbeat.SshConfig is not { Collected: true } reported) return false;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var stored = Read(server.SshConfigStateJson);
        if (stored is null)
        {
            server.SshConfigStateJson = JsonSerializer.Serialize(
                new SshConfigStatusDto { Snapshot = reported, ObservedAt = now, BaselineAt = now });
            logger.LogInformation("SSH configuration baseline recorded for server {ServerId}", server.Id);
            return true;
        }
        if (JsonSerializer.Serialize(stored.Snapshot) == JsonSerializer.Serialize(reported)) return false;

        var changes = SshConfigChangeDetector.Compare(stored.Snapshot, reported);
        var recent = changes
            .Select(c => new SshConfigChangeDto { DetectedAt = now, Severity = c.Severity, Message = c.Message })
            .Concat(stored.RecentChanges)
            .Take(SshConfigLimits.MaxStoredChanges)
            .ToList();
        server.SshConfigStateJson = JsonSerializer.Serialize(
            stored with { Snapshot = reported, ObservedAt = now, RecentChanges = recent });

        var alerts = changes.Where(c => c.IsAlert).ToList();
        if (alerts.Count > 0) await RaiseAsync(server, alerts, now, ct).ConfigureAwait(false);
        return true;
    }

    private async Task RaiseAsync(Server server, List<SshConfigChange> alerts, DateTime now, CancellationToken ct)
    {
        var severity = alerts.Any(a => a.Severity == SshConfigChange.Critical)
            ? SshConfigChange.Critical
            : SshConfigChange.Warning;
        var message = string.Join("; ", alerts.Select(a => a.Message));
        logger.LogWarning("SSH configuration changed on server {ServerId} ({ServerName}): {Changes}",
            server.Id, server.Name, message);
        await alertHub.Clients.Group(HubGroups.Alerts).SendAsync("AlertTriggered", new AlertTriggeredDto
        {
            RuleName = "Sec-Audit",
            ServerId = server.Id,
            ServerName = server.Name,
            Metric = Metric,
            Severity = severity,
            Message = message,
            TriggeredAt = now
        }, ct).ConfigureAwait(false);
        if (domainEvents is not null)
        {
            await domainEvents.DispatchAsync(
                new SshConfigChangedEvent(server.Id, server.Name, severity, message, now), ct).ConfigureAwait(false);
        }
    }

    internal static SshConfigStatusDto? Read(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<SshConfigStatusDto>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
