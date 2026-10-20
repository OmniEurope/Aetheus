// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Services.Email;

namespace Aetheus.Back.Components.Notifications;

/// <summary>
/// Writes each notification mail, and the channel test, from its editable template (recette R1-75) in its
/// recipient's language and colours (recette R1-74), then sends through the platform sender.
/// </summary>
public sealed class NotificationEmailer(IEmailSender sender, IMailTemplateRenderer templates, IConfiguration? configuration = null)
    : INotificationEmailer
{
    internal const string NoAddressMessage = "The recipient's account has no e-mail address; nothing was sent.";
    private string? FrontOrigin => configuration?["Cors:Origins:0"] is { Length: > 0 } front
        ? front : configuration?["Cors:FrontendUrl"];

    public async Task<IReadOnlyList<EmailSendResult>> SendAsync(
        IReadOnlyList<NotificationEmail> emails, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(emails);
        var addressed = emails
            .Select((email, index) => (email, index))
            .Where(item => !string.IsNullOrWhiteSpace(item.email.To))
            .ToList();
        var messages = new List<EmailMessage>(addressed.Count);
        var styles = new Dictionary<string, MailRecipientStyle>(StringComparer.OrdinalIgnoreCase);
        foreach (var (email, _) in addressed)
            messages.Add(await BuildAsync(email, styles, ct).ConfigureAwait(false));
        var sent = messages.Count == 0 ? [] : await sender.SendBatchAsync(messages, ct).ConfigureAwait(false);

        var results = Enumerable.Repeat(EmailSendResult.NotConfigured(NoAddressMessage), emails.Count).ToArray();
        for (var i = 0; i < addressed.Count; i++)
            results[addressed[i].index] = sent[i];
        return results;
    }

    public async Task<EmailSendResult> SendChannelTestAsync(string? to, string channelName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(to))
            return EmailSendResult.NotConfigured(
                "Your account has no e-mail address: an administrator sets it in the user administration; nothing was sent.");

        var address = to.Trim();
        var style = await templates.ResolveRecipientAsync(address, ct).ConfigureAwait(false);
        var mail = await templates.RenderAsync(MailTemplateKind.NotificationChannelTest,
            MailTemplateData.Text(NotificationEmailComposer.ChannelTestValues(channelName, FrontOrigin)), style, ct).ConfigureAwait(false);
        return await sender.SendAsync(new EmailMessage(address, mail.Subject, mail.Text, mail.Html), ct).ConfigureAwait(false);
    }

    /// <summary>One recipient's mail; a recipient's style is read once per batch.</summary>
    private async Task<EmailMessage> BuildAsync(NotificationEmail email, Dictionary<string, MailRecipientStyle> styles, CancellationToken ct)
    {
        var address = email.To!.Trim();
        if (!styles.TryGetValue(address, out var style))
        {
            style = await templates.ResolveRecipientAsync(address, ct).ConfigureAwait(false);
            styles[address] = style;
        }

        var data = NotificationEmailComposer.Build(email, FrontOrigin, style.Language);
        var mail = await templates.RenderAsync(MailTemplateKind.NotificationEvent, data, style, ct).ConfigureAwait(false);
        return new EmailMessage(address, mail.Subject, mail.Text, mail.Html);
    }
}
