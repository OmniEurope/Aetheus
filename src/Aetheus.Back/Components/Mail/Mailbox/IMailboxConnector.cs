// SPDX-License-Identifier: EUPL-1.2
using MimeKit;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// The IMAP and SMTP wire of the mailbox, behind an interface so the service is tested without a
/// server. A refused login is thrown as <see cref="ConflictException"/>, an unreachable server as
/// <see cref="BadRequestException"/>, each with the server's own reason.
/// </summary>
public interface IMailboxConnector
{
    /// <summary>Connects over IMAPS and logs in.</summary>
    Task<IMailboxSession> OpenAsync(MailboxEndpoint endpoint, CancellationToken ct);

    /// <summary>Submits the message over SMTP submission (STARTTLS, SMTP AUTH as the mailbox).</summary>
    Task SendAsync(MailboxEndpoint endpoint, MimeMessage message, CancellationToken ct);
}
