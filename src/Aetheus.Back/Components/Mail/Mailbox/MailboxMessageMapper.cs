// SPDX-License-Identifier: EUPL-1.2
using MimeKit;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>Maps sanitized HTML, plain text and attachment positions for display, withholding
/// remote images unless already fetched.</summary>
public static class MailboxMessageMapper
{
    /// <summary>Inline images larger than this, together, are not drawn: the page stays light.</summary>
    public const long MaxInlineImageBytes = 5L * 1024 * 1024;

    private static readonly HashSet<string> s_inlineImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"
    };

    /// <summary>The remote image URLs the HTML part refers to, the ones "show images" fetches.</summary>
    public static IReadOnlyList<string> RemoteImageUrls(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.HtmlBody is { } html
            ? MailboxHtmlSanitizer.Sanitize(html, new Dictionary<string, string>()).BlockedRemoteUrls
            : [];
    }

    public static MailboxMessageDto ToDto(
        string folder,
        MailboxFetchedMessage fetched,
        IReadOnlyDictionary<string, string>? remoteImages)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(fetched);
        var message = fetched.Message;
        string? html = null;
        var blocked = 0;
        if (message.HtmlBody is { } rawHtml)
        {
            var sanitized = MailboxHtmlSanitizer.Sanitize(rawHtml, InlineImages(message), remoteImages);
            html = sanitized.Html;
            blocked = sanitized.BlockedRemoteUrls.Count;
        }

        return new MailboxMessageDto
        {
            Uid = fetched.Uid,
            Folder = folder,
            From = MailboxAddresses.Format(message.From),
            To = MailboxAddresses.Format(message.To),
            Cc = MailboxAddresses.Format(message.Cc),
            Subject = message.Subject ?? string.Empty,
            Date = message.Date.UtcDateTime,
            IsRead = fetched.Seen,
            Html = html,
            Text = MailboxComposer.TextOf(message),
            BlockedRemoteImages = blocked,
            Attachments = MailboxComposer.Attachments(message)
        };
    }

    public static MailboxMessageSummaryDto ToSummaryDto(MailboxSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new MailboxMessageSummaryDto
        {
            Uid = summary.Uid,
            From = summary.From,
            To = summary.To,
            Subject = summary.Subject,
            Date = summary.Date.UtcDateTime,
            IsRead = summary.Seen,
            HasAttachments = summary.HasAttachments,
            Size = summary.Size
        };
    }

    /// <summary>The message's own images, by Content-ID, as <c>data:</c> URLs (raster types only, bounded).</summary>
    public static Dictionary<string, string> InlineImages(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (string.IsNullOrEmpty(part.ContentId) || part.Content is null
                || !s_inlineImageTypes.Contains(part.ContentType.MimeType)) continue;
            using var stream = new MemoryStream();
            part.Content.DecodeTo(stream);
            if (total + stream.Length > MaxInlineImageBytes) continue;
            total += stream.Length;
            result[part.ContentId.Trim('<', '>')] =
                $"data:{part.ContentType.MimeType.ToLowerInvariant()};base64,{Convert.ToBase64String(stream.ToArray())}";
        }
        return result;
    }
}

