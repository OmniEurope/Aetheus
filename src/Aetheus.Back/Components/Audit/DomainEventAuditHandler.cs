// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Audit;

/// <summary>
/// Generic audit handler: logs every domain-event occurrence to <see cref="IAuditService"/>.
/// Register one closed instance per event type in <c>AddSharedDomainEvents</c> to opt that
/// event into automatic audit-trail recording without writing a dedicated handler.
/// </summary>
public sealed class DomainEventAuditHandler<TEvent>(IAuditService audit, ILogger<DomainEventAuditHandler<TEvent>> logger)
    : IDomainEventHandler<TEvent>
    where TEvent : IDomainEvent
{
    public async Task HandleAsync(TEvent domainEvent, CancellationToken ct = default)
    {
        if (domainEvent is null) return;

        var eventName = typeof(TEvent).Name;
        // record() ToString() yields "TypeName { Prop1 = ..., Prop2 = ... }" which is ideal for audit details.
        var details = domainEvent.ToString();

        try
        {
            await audit.LogAsync(
                action: "DomainEvent",
                entityType: eventName,
                entityId: null,
                details: details,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to write audit entry for domain event {Event}", eventName);
        }
    }
}
