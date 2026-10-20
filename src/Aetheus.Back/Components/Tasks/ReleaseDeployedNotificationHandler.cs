// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Tasks;

/// <summary>Notify project subscribers when ReleaseDeployedEvent signals a deployed release,
/// without sending admin-channel deliveries. Non-strict dispatch prevents notification failure from
/// changing the deployment result.</summary>
public sealed class ReleaseDeployedNotificationHandler(IUserNotificationService userNotifications)
    : IDomainEventHandler<ReleaseDeployedEvent>
{
    public Task HandleAsync(ReleaseDeployedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return userNotifications.RecordReleaseDeployedAsync(
            domainEvent.ReleaseId, domainEvent.PipelineRunId, domainEvent.StageName, domainEvent.IsRollback, ct);
    }
}
