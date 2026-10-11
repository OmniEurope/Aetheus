// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services.Email;

/// <summary>
/// Recette R1-16: sends e-mails through the platform sender (the no-reply account of the managed mail
/// domain by default, or the custom SMTP server of the platform settings). Never throws for an SMTP
/// failure: each message gets its own <see cref="EmailSendResult"/>, with the server's text verbatim.
/// </summary>
public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default);

    /// <summary>Sends the messages over one SMTP connection; the results follow the order of the messages.</summary>
    Task<IReadOnlyList<EmailSendResult>> SendBatchAsync(IReadOnlyList<EmailMessage> messages, CancellationToken ct = default);
}
