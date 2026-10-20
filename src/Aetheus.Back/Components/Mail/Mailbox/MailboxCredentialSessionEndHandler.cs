// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Users.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary> When an Aetheus user's session ends (logout, revoked refresh chain, self-service
/// password change, deactivation, deletion), the mailbox passwords they typed are forgotten at once
/// instead of waiting for the idle or maximum lifetime. Listens to an event of Users (L1), so
/// neither Auth (L2) nor Users reaches up into Mail (L5). </summary>
public sealed class MailboxCredentialSessionEndHandler(
    IMailboxCredentialStore credentials,
    ILogger<MailboxCredentialSessionEndHandler> logger) : IDomainEventHandler<UserSessionEndedEvent>
{
    public Task HandleAsync(UserSessionEndedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var forgotten = credentials.ForgetUser(domainEvent.UserId);
        if (forgotten > 0)
            logger.LogInformation(
                "Forgot {Count} held mailbox password(s) of user {UserId} at session end ({Reason})",
                forgotten, domainEvent.UserId, domainEvent.Reason);
        return Task.CompletedTask;
    }
}
