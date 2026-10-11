// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services.Email;

/// <summary>
/// What became of one message. <see cref="Message"/> carries the SMTP failure text verbatim, or why
/// nothing was attempted; for a sent message it names the sender that carried it.
/// </summary>
public sealed record EmailSendResult(EmailSendStatus Status, string Message)
{
    public static EmailSendResult Sent(string fromAddress) =>
        new(EmailSendStatus.Sent, $"Accepted by the SMTP server, sent from {fromAddress}.");

    public static EmailSendResult NotConfigured(string reason) => new(EmailSendStatus.NotConfigured, reason);

    public static EmailSendResult Failed(string error) => new(EmailSendStatus.Failed, error);
}
