// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Servers;

/// <summary>Persists SSH configuration changes as administrator notifications and publishes
/// <c>alert.triggered</c> for the configured mail and webhook channels, like sudoers drift.</summary>
public sealed class SshConfigChangeNotificationHandler(
    IUserNotificationService userNotifications,
    INotificationService notifications,
    IAlertOccurrenceService occurrences) : IDomainEventHandler<SshConfigChangedEvent>
{
    internal const string EventType = "alert.triggered";

    public async Task HandleAsync(SshConfigChangedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var alertId = await occurrences.RecordHostAlertAsync(
            "Sec-Audit", domainEvent.ServerId, domainEvent.ServerName, SshConfigMonitor.Metric,
            domainEvent.Severity, domainEvent.Message, domainEvent.DetectedAt, ct).ConfigureAwait(false);
        var payload = new
        {
            AlertId = alertId,
            RuleName = "Sec-Audit",
            domainEvent.ServerId,
            domainEvent.ServerName,
            Metric = SshConfigMonitor.Metric,
            domainEvent.Severity,
            domainEvent.Message,
            domainEvent.Changes,
            TriggeredAt = domainEvent.DetectedAt
        };
        var subject = $"Sec-Audit: SSH configuration changed on server {domainEvent.ServerName}";
        await userNotifications.RecordAdministratorEventAsync(
            EventType, subject, JsonSerializer.Serialize(payload), ct).ConfigureAwait(false);
        await notifications.SendEventAsync(EventType, payload, ct).ConfigureAwait(false);
    }
}
