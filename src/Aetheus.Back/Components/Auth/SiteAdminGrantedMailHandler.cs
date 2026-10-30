// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Users.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Auth;

/// <summary>Mails the person whose account the user or role administration just made site Admin.</summary>
public sealed class SiteAdminGrantedMailHandler(ISiteAdminGrantMail mail) : IDomainEventHandler<SiteAdminGrantedEvent>
{
    public Task HandleAsync(SiteAdminGrantedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return mail.SendAsync(domainEvent.UserId, domainEvent.Username, domainEvent.Email, ct);
    }
}
