// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.Email;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// The ports of the mail stack Aetheus installs (deploy/agent-host-config/mail): Dovecot serves IMAP
/// with <c>ssl = required</c> and its stock IMAPS listener on 993, the port <c>mail-manage check</c>
/// verifies; Postfix's submission service listens on 587 with TLS mandatory and SMTP AUTH.
/// </summary>
public static class MailboxPorts
{
    public const int Imaps = 993;
    public const int Submission = EmailSenderSettingKeys.ManagedSubmissionPort;
}
