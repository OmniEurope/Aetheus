// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Mail;

/// <summary>Reconciles mail rows against heartbeat inventory and invalidates cached reads when data
/// changes.</summary>
public sealed class MailInventoryReconciliationHandler(IMailStateReconciler reconciler, IMemoryCache cache, IMailQuotaResync quotaResync)
    : IDomainEventHandler<MailInventoryReportedEvent>
{
    public async Task HandleAsync(MailInventoryReportedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var result = await reconciler.ReconcileAsync(domainEvent.ServerId, domainEvent.Mail, ct).ConfigureAwait(false);
        if (result.HasChanges)
            MailCacheKeys.Invalidate(cache, domainEvent.ServerId);
        // After the reconciliation, so accounts the inventory just discovered are resynced too.
        await quotaResync.ResyncAsync(domainEvent.ServerId, domainEvent.Mail.HelperVersion, ct).ConfigureAwait(false);
    }
}
