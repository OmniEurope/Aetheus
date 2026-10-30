// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Auth;

/// <summary>Runs the administrator bootstrap after each proof of access to an account, and says whether it granted.</summary>
public sealed class SiteAdminBootstrapHandler(ISiteAdminBootstrap bootstrap) : IDomainEventHandler<AccountAccessProvedEvent>
{
    public async Task HandleAsync(AccountAccessProvedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        domainEvent.GrantedSiteAdmin = await bootstrap.TryGrantAsync(
            domainEvent.UserId, domainEvent.Username, domainEvent.Email, ct).ConfigureAwait(false);
    }
}
