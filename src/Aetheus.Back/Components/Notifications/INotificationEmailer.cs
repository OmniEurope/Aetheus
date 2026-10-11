// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.Email;

namespace Aetheus.Back.Components.Notifications;

/// <summary>
/// Recette R1-16: turns notifications into e-mails (plain text and HTML) and sends them through the
/// platform sender. Every result says what really happened, the SMTP text verbatim on a failure.
/// </summary>
public interface INotificationEmailer
{
    /// <summary>The results follow the order of <paramref name="emails"/>.</summary>
    Task<IReadOnlyList<EmailSendResult>> SendAsync(IReadOnlyList<NotificationEmail> emails, CancellationToken ct = default);

    /// <summary>The test message of an E-mail channel, to the address of the user who asked for it.</summary>
    Task<EmailSendResult> SendChannelTestAsync(string? to, string channelName, CancellationToken ct = default);
}
