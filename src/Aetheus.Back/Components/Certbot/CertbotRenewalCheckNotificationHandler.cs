// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Certbot;

/// <summary>Notifies about failed renewal rehearsals while certificates are still valid.</summary>
public sealed class CertbotRenewalCheckNotificationHandler(INotificationService notifications)
    : IDomainEventHandler<CertbotRenewalCheckFailedEvent>
{
    public const string EventType = "certbot.renewal-check.failed";

    public Task HandleAsync(CertbotRenewalCheckFailedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return notifications.SendEventAsync(EventType, new
        {
            domainEvent.ServerId,
            domainEvent.ServerName,
            domainEvent.CheckedAt
        }, ct);
    }
}
