// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text;
using System.Text.Json;
using Aetheus.Back.Services.Email;

namespace Aetheus.Back.Components.Notifications;

public sealed class NotificationEmailer(IEmailSender sender) : INotificationEmailer
{
    internal const string NoAddressMessage = "The recipient's account has no e-mail address; nothing was sent.";
    private const string SubjectPrefix = "[Aetheus] ";

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public async Task<IReadOnlyList<EmailSendResult>> SendAsync(
        IReadOnlyList<NotificationEmail> emails, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(emails);
        var addressed = emails
            .Select((email, index) => (email, index))
            .Where(item => !string.IsNullOrWhiteSpace(item.email.To))
            .ToList();
        var sent = addressed.Count == 0
            ? []
            : await sender.SendBatchAsync(addressed.Select(item => Build(item.email)).ToList(), ct).ConfigureAwait(false);

        var results = Enumerable.Repeat(EmailSendResult.NotConfigured(NoAddressMessage), emails.Count).ToArray();
        for (var i = 0; i < addressed.Count; i++)
            results[addressed[i].index] = sent[i];
        return results;
    }

    public Task<EmailSendResult> SendChannelTestAsync(string? to, string channelName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(to))
            return Task.FromResult(EmailSendResult.NotConfigured(
                "Your account has no e-mail address: an administrator sets it in the user administration; nothing was sent."));

        var text = $"This is a test of the Aetheus notification channel \"{channelName}\".\n"
            + "If you read it, e-mail notifications reach this address.";
        return sender.SendAsync(new EmailMessage(
            to.Trim(),
            SubjectPrefix + "Notification channel test",
            text,
            Html(SubjectPrefix + "Notification channel test", text, payload: null)), ct);
    }

    internal static EmailMessage Build(NotificationEmail email)
    {
        var payload = Indent(email.PayloadJson);
        var text = new StringBuilder()
            .AppendLine(email.Subject)
            .AppendLine()
            .Append("Event: ").AppendLine(email.EventType)
            .AppendLine()
            .AppendLine(payload)
            .ToString();
        return new EmailMessage(
            email.To!.Trim(),
            SubjectPrefix + email.Subject,
            text,
            Html(email.Subject, $"Event: {email.EventType}", payload));
    }

    private static string Html(string title, string text, string? payload)
    {
        var html = new StringBuilder()
            .Append("<!DOCTYPE html><html><body>")
            .Append("<h2>").Append(WebUtility.HtmlEncode(title)).Append("</h2>")
            .Append("<p>").Append(WebUtility.HtmlEncode(text).Replace("\n", "<br>", StringComparison.Ordinal)).Append("</p>");
        if (!string.IsNullOrWhiteSpace(payload))
            html.Append("<pre>").Append(WebUtility.HtmlEncode(payload)).Append("</pre>");
        return html.Append("</body></html>").ToString();
    }

    private static string Indent(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, IndentedJson);
        }
        catch (JsonException)
        {
            // Mandatory: a payload that is not JSON is still worth reading, as it came.
            return json;
        }
    }
}
