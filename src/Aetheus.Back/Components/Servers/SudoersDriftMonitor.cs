// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Servers;

/// <summary>Compare reported sudoers hashes with the stored baseline, recapturing it on confirmed
/// agent updates because installation re-renders drop-ins. Persist drift fingerprints to
/// deduplicate alerts, dispatch <see cref="SudoersDriftDetectedEvent"/> for durable records, and
/// repeat alerts after <see cref="ReminderInterval"/> while drift persists.</summary>
internal sealed class SudoersDriftMonitor(
    IHubContext<AlertHub> alertHub,
    TimeProvider timeProvider,
    IDomainEventDispatcher? domainEvents,
    ILogger logger)
{
    /// <summary>How often a drift that lasts is alerted again; within it the fingerprint deduplicates.</summary>
    internal static readonly TimeSpan ReminderInterval = TimeSpan.FromHours(6);

    /// <param name="confirmedAgentVersion">The agent version this same heartbeat confirmed as an
    /// update target, or null when the heartbeat confirmed no update.</param>
    /// <returns>Whether the server's baseline or alerted fingerprint changed and must be saved.</returns>
    public async Task<bool> CheckAsync(
        Server server,
        ServerHeartbeatDto heartbeat,
        string? confirmedAgentVersion,
        CancellationToken ct)
    {
        var reported = heartbeat.SudoersHashes;
        if (confirmedAgentVersion is not null)
            return RecaptureAfterAgentUpdate(server, reported, confirmedAgentVersion);
        if (reported.Count == 0) return false;

        var baseline = ReadBaseline(server.SudoersBaseline);
        if (baseline is null)
        {
            // First-ever report (or an unreadable baseline): capture it, no alert.
            return Apply(server, JsonSerializer.Serialize(reported), fingerprint: null);
        }

        var drifted = reported
            .Where(kv => baseline.TryGetValue(kv.Key, out var baseHash)
                && !string.Equals(baseHash, kv.Value, StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();
        if (drifted.Count == 0)
        {
            // Back to the baseline: a later drift is a new state and alerts again.
            return Apply(server, server.SudoersBaseline, fingerprint: null);
        }

        var fingerprint = Fingerprint(drifted);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var isReminder = string.Equals(server.SudoersDriftAlertedFingerprint, fingerprint, StringComparison.Ordinal);
        // A fingerprint alerted before the alert date existed (null) is due: it is re-raised once now.
        if (isReminder && server.SudoersDriftAlertedAt is { } alertedAt && now - alertedAt < ReminderInterval)
            return false;
        server.SudoersDriftAlertedFingerprint = fingerprint;
        server.SudoersDriftAlertedAt = now;

        await RaiseAsync(server, string.Join(", ", drifted.Select(kv => kv.Key)), isReminder, now, ct)
            .ConfigureAwait(false);
        return true;
    }

    private async Task RaiseAsync(Server server, string files, bool isReminder, DateTime now, CancellationToken ct)
    {
        logger.LogWarning(
            "Sudoers drift on server {ServerId} ({ServerName}): {Files} differ from the baseline{Reminder}",
            server.Id, server.Name, files, isReminder ? " (still present, reminder)" : string.Empty);
        var message = $"Sudoers drop-in changed out-of-band: {files}. "
                      + "Verify the change was authorized; the install baseline no longer matches.";
        await alertHub.Clients.Group(HubGroups.Alerts).SendAsync("AlertTriggered", new AlertTriggeredDto
        {
            RuleName = "Sec-Audit",
            ServerId = server.Id,
            ServerName = server.Name,
            Metric = "SudoersDrift",
            Severity = "Critical",
            Message = message,
            TriggeredAt = now
        }, ct).ConfigureAwait(false);
        // Awaited, observer semantics: a handler failure is logged by the dispatcher and the heartbeat
        // goes on; the next reminder raises it again.
        if (domainEvents is not null)
        {
            await domainEvents.DispatchAsync(
                new SudoersDriftDetectedEvent(server.Id, server.Name, files, message, isReminder, now), ct)
                .ConfigureAwait(false);
        }
    }

    private bool RecaptureAfterAgentUpdate(
        Server server, Dictionary<string, string> reported, string confirmedAgentVersion)
    {
        // When this heartbeat carries no hashes (inventory not ready right after the restart), clearing
        // the baseline makes the next report capture it as a first report instead of alerting on it.
        var changed = Apply(
            server,
            reported.Count > 0 ? JsonSerializer.Serialize(reported) : null,
            fingerprint: null);
        logger.LogInformation(
            "Sudoers baseline re-captured after agent update to {AgentVersion} on server {ServerId}: {Outcome}",
            confirmedAgentVersion,
            server.Id,
            reported.Count > 0
                ? $"{reported.Count} drop-in(s) recorded"
                : "no hashes reported yet, the next report becomes the baseline");
        return changed;
    }

    private static bool Apply(Server server, string? baseline, string? fingerprint)
    {
        if (string.Equals(server.SudoersBaseline, baseline, StringComparison.Ordinal)
            && string.Equals(server.SudoersDriftAlertedFingerprint, fingerprint, StringComparison.Ordinal))
            return false;
        server.SudoersBaseline = baseline;
        server.SudoersDriftAlertedFingerprint = fingerprint;
        server.SudoersDriftAlertedAt = null;
        return true;
    }

    private static Dictionary<string, string>? ReadBaseline(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Fingerprint(IEnumerable<KeyValuePair<string, string>> drifted)
    {
        var canonical = string.Join('\n', drifted.Select(kv => $"{kv.Key}={kv.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
