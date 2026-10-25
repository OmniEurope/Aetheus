// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Servers;

/// <summary>Persists OOM-kill notifications for active administrators, so the kill stays visible after
/// the live toast, and publishes <c>alert.triggered</c> for configured mail and webhook channels.</summary>
public sealed class OomKillNotificationHandler(
    IUserNotificationService userNotifications,
    INotificationService notifications,
    IAlertOccurrenceService occurrences) : IDomainEventHandler<OomKillDetectedEvent>
{
    internal const string EventType = "alert.triggered";

    public async Task HandleAsync(OomKillDetectedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var alertId = await occurrences.RecordHostAlertAsync(
            "OOM", domainEvent.ServerId, domainEvent.ServerName, "OomKill", "Critical", domainEvent.Message, domainEvent.DetectedAt, ct)
            .ConfigureAwait(false);
        var payload = new
        {
            AlertId = alertId,
            RuleName = "OOM",
            domainEvent.ServerId,
            domainEvent.ServerName,
            Metric = "OomKill",
            Severity = "Critical",
            domainEvent.KillCount,
            domainEvent.Victims,
            domainEvent.Message,
            TriggeredAt = domainEvent.DetectedAt
        };
        await userNotifications.RecordAdministratorEventAsync(
            EventType, $"OOM kill on server {domainEvent.ServerName}", JsonSerializer.Serialize(payload), ct)
            .ConfigureAwait(false);
        await notifications.SendEventAsync(EventType, payload, ct).ConfigureAwait(false);
    }
}
