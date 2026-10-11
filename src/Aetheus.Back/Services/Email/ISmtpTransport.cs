// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services.Email;

/// <summary>
/// The SMTP wire, kept behind an interface so the sender is tested without a server. A failure is
/// thrown as the transport's own exception; <see cref="EmailSender"/> turns its text into the result.
/// </summary>
public interface ISmtpTransport
{
    /// <summary>Connects, secures and (when a user name is set) authenticates.</summary>
    Task<ISmtpSession> ConnectAsync(EmailSenderConfiguration configuration, CancellationToken ct);
}

/// <summary>An open, authenticated SMTP connection; disposing it says QUIT.</summary>
public interface ISmtpSession : IAsyncDisposable
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}
