// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Servers;

/// <summary>Records a container with host-escape settings as an administrator notification and publishes
/// <c>alert.triggered</c> to the configured mail and webhook channels, like a sudoers drift.</summary>
public sealed class DockerContainerRiskNotificationHandler(
    IUserNotificationService userNotifications,
    INotificationService notifications,
    IAlertOccurrenceService occurrences) : IDomainEventHandler<DockerContainerRiskDetectedEvent>
{
    public async Task HandleAsync(DockerContainerRiskDetectedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var alertId = await occurrences.RecordHostAlertAsync(
            DockerContainerRiskMonitor.RuleName, domainEvent.ServerId, domainEvent.ServerName, DockerContainerRiskMonitor.Metric,
            domainEvent.Severity, domainEvent.Message, domainEvent.DetectedAt, ct).ConfigureAwait(false);
        var payload = new
        {
            AlertId = alertId,
            RuleName = DockerContainerRiskMonitor.RuleName,
            domainEvent.ServerId,
            domainEvent.ServerName,
            Metric = DockerContainerRiskMonitor.Metric,
            domainEvent.Severity,
            domainEvent.ContainerName,
            domainEvent.Image,
            domainEvent.Risks,
            domainEvent.Message,
            domainEvent.IsChange,
            TriggeredAt = domainEvent.DetectedAt
        };
        var subject = domainEvent.IsChange
            ? $"Sec-Audit: container {domainEvent.ContainerName} changed its host access on server {domainEvent.ServerName}"
            : $"Sec-Audit: container {domainEvent.ContainerName} has host access on server {domainEvent.ServerName}";
        await userNotifications.RecordAdministratorEventAsync(
            SudoersDriftNotificationHandler.EventType, subject, JsonSerializer.Serialize(payload), ct).ConfigureAwait(false);
        await notifications.SendEventAsync(SudoersDriftNotificationHandler.EventType, payload, ct).ConfigureAwait(false);
    }
}
