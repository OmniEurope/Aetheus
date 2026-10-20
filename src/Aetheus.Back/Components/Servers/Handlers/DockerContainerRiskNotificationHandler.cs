// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Servers.Handlers;

/// <summary>Records a container with host-escape settings as an administrator notification and publishes
/// <c>alert.triggered</c> to the configured mail and webhook channels, like a sudoers drift.</summary>
public sealed class DockerContainerRiskNotificationHandler(
    IUserNotificationService userNotifications,
    INotificationService notifications) : IDomainEventHandler<DockerContainerRiskDetectedEvent>
{
    public async Task HandleAsync(DockerContainerRiskDetectedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var payload = new
        {
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
