// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Servers;

/// <summary>Persists sudoers-drift notifications for active administrators, including servers
/// without project followers. Also publishes <c>alert.triggered</c> for configured mail and webhook
/// channels.</summary>
public sealed class SudoersDriftNotificationHandler(
    IUserNotificationService userNotifications,
    INotificationService notifications,
    IAlertOccurrenceService occurrences) : IDomainEventHandler<SudoersDriftDetectedEvent>
{
    internal const string EventType = "alert.triggered";

    public async Task HandleAsync(SudoersDriftDetectedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var alertId = await occurrences.RecordHostAlertAsync(
            "Sec-Audit", domainEvent.ServerId, domainEvent.ServerName, "SudoersDrift", "Critical",
            domainEvent.Message, domainEvent.DetectedAt, ct).ConfigureAwait(false);
        var payload = new
        {
            AlertId = alertId,
            RuleName = "Sec-Audit",
            domainEvent.ServerId,
            domainEvent.ServerName,
            Metric = "SudoersDrift",
            Severity = "Critical",
            domainEvent.Files,
            domainEvent.Message,
            domainEvent.IsReminder,
            TriggeredAt = domainEvent.DetectedAt
        };
        var subject = domainEvent.IsReminder
            ? $"Sec-Audit: sudoers drift still present on server {domainEvent.ServerName}"
            : $"Sec-Audit: sudoers drift on server {domainEvent.ServerName}";
        await userNotifications.RecordAdministratorEventAsync(
            EventType, subject, JsonSerializer.Serialize(payload), ct).ConfigureAwait(false);
        await notifications.SendEventAsync(EventType, payload, ct).ConfigureAwait(false);
    }
}
