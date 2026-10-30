// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Users.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Auth;

/// <summary>R-14: sends the welcome mail of a created account and hands its outcome back to the creation.</summary>
public sealed class UserWelcomeMailHandler(IPasswordSetupService passwordSetup) : IDomainEventHandler<UserWelcomeRequestedEvent>
{
    public async Task HandleAsync(UserWelcomeRequestedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        domainEvent.Outcome = await passwordSetup.SendWelcomeAsync(
            domainEvent.UserId, domainEvent.Username, domainEvent.Email, ct).ConfigureAwait(false);
    }
}
