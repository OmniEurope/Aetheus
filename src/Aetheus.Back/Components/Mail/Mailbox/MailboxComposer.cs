// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;
using MimeKit;
using MimeKit.Utils;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// Recette R1-17. Builds what a reply or a forward starts from (recipients, subject, the original
/// quoted) and the MIME message finally sent. Pure: the service gives it the original and the time.
/// The quote's introduction is written in the reader's language (French or English), since it becomes
/// part of the message.
/// </summary>
public static class MailboxComposer
{
    private sealed record QuoteTexts(string Wrote, string Forwarded, string From, string Date, string Subject, string To, string Cc);

    private static readonly QuoteTexts s_english = new(
        "On {0}, {1} wrote:", "---------- Forwarded message ----------", "From:", "Date:", "Subject:", "To:", "Cc:");

    private static readonly QuoteTexts s_french = new(
        "Le {0}, {1} a écrit :", "---------- Message transféré ----------", "De :", "Date :", "Objet :", "À :", "Cc :");

    public static MailboxDraftDto BuildDraft(MailboxComposeMode mode, MimeMessage original, string ownAddress, string? language)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(ownAddress);
        if (mode == MailboxComposeMode.New) return new MailboxDraftDto();

        var texts = string.Equals(language, "fr", StringComparison.OrdinalIgnoreCase) ? s_french : s_english;
        var culture = texts == s_french ? CultureInfo.GetCultureInfo("fr-FR") : CultureInfo.InvariantCulture;
        var date = original.Date.ToString("yyyy-MM-dd HH:mm zzz", culture);
        var originalText = TextOf(original);

        if (mode == MailboxComposeMode.Forward)
        {
            var header = new StringBuilder()
                .AppendLine().AppendLine()
                .AppendLine(texts.Forwarded)
                .AppendLine($"{texts.From} {MailboxAddresses.Format(original.From)}")
                .AppendLine($"{texts.Date} {date}")
                .AppendLine($"{texts.Subject} {original.Subject}")
                .AppendLine($"{texts.To} {MailboxAddresses.Format(original.To)}");
            if (original.Cc.Count > 0) header.AppendLine($"{texts.Cc} {MailboxAddresses.Format(original.Cc)}");
            header.AppendLine().Append(originalText);
            return new MailboxDraftDto
            {
                Subject = Prefixed("Fwd: ", original.Subject, "Fwd:", "Fw:", "Tr:"),
                Body = header.ToString(),
                ForwardedAttachments = Attachments(original)
            };
        }

        var replyTo = original.ReplyTo.Count > 0 ? original.ReplyTo.Mailboxes : original.From.Mailboxes;
        var to = Distinct(replyTo, ownAddress, []);
        var cc = new List<MailboxAddress>();
        if (mode == MailboxComposeMode.ReplyAll)
        {
            to = Distinct(to.Concat(original.To.Mailboxes), ownAddress, []);
            cc = Distinct(original.Cc.Mailboxes, ownAddress, to);
        }
        // Replying to one's own message (from the Sent folder) goes back to its recipients.
        if (to.Count == 0) to = Distinct(original.To.Mailboxes, ownAddress, []);

        var quoted = string.Join('\n', originalText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => line.Length == 0 ? ">" : "> " + line));
        return new MailboxDraftDto
        {
            To = MailboxAddresses.Format(to),
            Cc = MailboxAddresses.Format(cc),
            Subject = Prefixed("Re: ", original.Subject, "Re:"),
            Body = $"\n\n{string.Format(culture, texts.Wrote, date, MailboxAddresses.Format(original.From))}\n{quoted}\n"
        };
    }

    /// <summary>The message to submit. At least one recipient; a reply carries the threading headers.</summary>
    public static MimeMessage BuildMessage(
        MailboxSendRequest request,
        MailboxAddress from,
        MimeMessage? original,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(from);
        var message = new MimeMessage();
        message.From.Add(from);
        message.To.AddRange(MailboxAddresses.Parse(request.To, "To"));
        message.Cc.AddRange(MailboxAddresses.Parse(request.Cc, "Cc"));
        message.Bcc.AddRange(MailboxAddresses.Parse(request.Bcc, "Bcc"));
        if (message.To.Count + message.Cc.Count + message.Bcc.Count == 0)
            throw new BadRequestException("The message has no recipient.");
        message.Subject = request.Subject.Trim();
        message.Date = now;
        var domain = from.Address[(from.Address.IndexOf('@', StringComparison.Ordinal) + 1)..];
        message.MessageId = MimeUtils.GenerateMessageId(domain);

        if (original is not null && request.Mode is MailboxComposeMode.Reply or MailboxComposeMode.ReplyAll
            && !string.IsNullOrEmpty(original.MessageId))
        {
            message.InReplyTo = original.MessageId;
            foreach (var reference in original.References) message.References.Add(reference);
            message.References.Add(original.MessageId);
        }

        var body = new BodyBuilder { TextBody = request.Body.Replace("\r\n", "\n", StringComparison.Ordinal) };
        if (original is not null && request.Mode == MailboxComposeMode.Forward)
        {
            foreach (var attachment in original.Attachments)
                body.Attachments.Add(attachment);
        }
        MailboxOutgoingAttachments.AddTo(body, request.Attachments);
        message.Body = body.ToMessageBody();
        return message;
    }

    /// <summary>The text a quote is made of: the text part, or the text drawn from the HTML part.</summary>
    public static string TextOf(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!string.IsNullOrWhiteSpace(message.TextBody)) return message.TextBody.TrimEnd();
        return message.HtmlBody is { } html ? MailboxHtmlSanitizer.ToText(html) : string.Empty;
    }

    public static List<MailboxAttachmentDto> Attachments(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Attachments.Select((entity, index) => new MailboxAttachmentDto
        {
            Index = index,
            FileName = FileNameOf(entity, index),
            ContentType = entity.ContentType.MimeType,
            Size = SizeOf(entity)
        }).ToList();
    }

    public static string FileNameOf(MimeEntity entity, int index)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var name = entity switch
        {
            MimePart part => part.FileName,
            MessagePart message => message.Message?.Subject is { Length: > 0 } subject ? subject + ".eml" : null,
            _ => null
        };
        return string.IsNullOrWhiteSpace(name) ? $"attachment-{index + 1}" : Path.GetFileName(name);
    }

    private static long SizeOf(MimeEntity entity)
    {
        if (entity is not MimePart { Content: not null } part) return 0;
        using var stream = new MemoryStream();
        part.Content.DecodeTo(stream);
        return stream.Length;
    }

    private static string Prefixed(string prefix, string? subject, params string[] existing)
    {
        var value = (subject ?? string.Empty).Trim();
        return existing.Any(marker => value.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) ? value : prefix + value;
    }

    private static List<MailboxAddress> Distinct(IEnumerable<MailboxAddress> addresses, string ownAddress, IReadOnlyList<MailboxAddress> exclude)
    {
        var seen = new HashSet<string>(exclude.Select(address => address.Address), StringComparer.OrdinalIgnoreCase) { ownAddress };
        return addresses.Where(address => seen.Add(address.Address)).ToList();
    }
}
