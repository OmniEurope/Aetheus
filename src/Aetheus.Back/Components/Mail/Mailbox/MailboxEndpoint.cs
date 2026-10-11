// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// Where and as whom the backend opens one mailbox: IMAPS for reading, SMTP submission (STARTTLS) for
/// sending, the full address as the login.
/// </summary>
public sealed record MailboxEndpoint(string Host, int ImapPort, int SubmissionPort, string Username, string Password)
{
    /// <summary>Keeps the password out of logs and exception messages.</summary>
    public override string ToString() =>
        $"MailboxEndpoint {{ Host = {Host}, ImapPort = {ImapPort}, SubmissionPort = {SubmissionPort}, Username = {Username} }}";
}
