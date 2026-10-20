// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Servers.Handlers;

/// <summary>Persists sudoers-drift notifications for active administrators, including servers
/// without project followers. Also publishes <c>alert.triggered</c> for configured mail and webhook
/// channels.</summary>
public sealed class SudoersDriftNotificationHandler(
    IUserNotificationService userNotifications,
    INotificationService notifications) : IDomainEventHandler<SudoersDriftDetectedEvent>
{
    internal const string EventType = "alert.triggered";

    public async Task HandleAsync(SudoersDriftDetectedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var payload = new
        {
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
