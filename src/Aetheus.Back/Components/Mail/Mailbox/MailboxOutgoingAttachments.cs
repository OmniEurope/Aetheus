// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using MimeKit;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>Recheck attachment limits and copy bytes into MIME without opening, parsing or
/// rendering them. Derive the MIME label from the sanitized filename extension rather than browser
/// content type.</summary>
public static class MailboxOutgoingAttachments
{
    private static readonly char[] s_separators = ['/', '\\'];

    /// <summary>Refuses a list over the count, a file over the size or a total over the limit.</summary>
    public static void Validate(IReadOnlyList<MailboxOutgoingAttachment>? attachments)
    {
        if (attachments is null || attachments.Count == 0) return;
        if (attachments.Count > MailboxLimits.MaxAttachmentCount)
            throw new BadRequestException($"A message carries at most {MailboxLimits.MaxAttachmentCount} attached files.");

        long total = 0;
        foreach (var attachment in attachments)
        {
            if (attachment is null) throw new BadRequestException("An attached file is missing.");
            var size = attachment.Content?.LongLength ?? 0;
            if (size > MailboxLimits.MaxAttachmentBytes)
                throw new BadRequestException($"An attached file exceeds {MailboxLimits.MaxAttachmentBytes / (1024 * 1024)} MB.");
            total += size;
        }
        if (total > MailboxLimits.MaxAttachmentsTotalBytes)
            throw new BadRequestException($"The attached files exceed {MailboxLimits.MaxAttachmentsTotalBytes / (1024 * 1024)} MB together.");
    }

    /// <summary>Adds each file as an attachment (base64, disposition attachment) under its sanitized name.</summary>
    public static void AddTo(BodyBuilder body, IReadOnlyList<MailboxOutgoingAttachment>? attachments)
    {
        ArgumentNullException.ThrowIfNull(body);
        Validate(attachments);
        if (attachments is null) return;
        for (var index = 0; index < attachments.Count; index++)
        {
            var name = SanitizeFileName(attachments[index].FileName, index);
            var part = new MimePart(ContentType.Parse(MimeTypes.GetMimeType(name)))
            {
                Content = new MimeContent(new MemoryStream(attachments[index].Content ?? [], writable: false)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = name
            };
            body.Attachments.Add(part);
        }
    }

    /// <summary>
    /// The last segment of the name (no folder, Windows or Unix), without control characters or the
    /// characters a file system refuses, without leading or trailing dots and spaces, and at most
    /// <see cref="MailboxLimits.MaxAttachmentNameLength"/> characters with its extension kept;
    /// <c>attachment-N</c> when nothing is left.
    /// </summary>
    public static string SanitizeFileName(string? fileName, int index)
    {
        var name = ReplaceRefusedCharacters(LastPathSegment(fileName ?? string.Empty)).Trim().Trim('.', ' ');
        if (name.Length == 0 || name.All(character => character == '_')) return $"attachment-{index + 1}";
        return name.Length <= MailboxLimits.MaxAttachmentNameLength ? name : Shorten(name);
    }

    private static string LastPathSegment(string raw)
    {
        var lastSeparator = raw.LastIndexOfAny(s_separators);
        return lastSeparator >= 0 ? raw[(lastSeparator + 1)..] : raw;
    }

    /// <summary>Each control, format or file-system-refused character becomes an underscore.</summary>
    private static string ReplaceRefusedCharacters(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        foreach (var character in raw.Normalize(NormalizationForm.FormC))
            builder.Append(IsRefusedCharacter(character) ? '_' : character);
        return builder.ToString();
    }

    private static bool IsRefusedCharacter(char character) =>
        char.IsControl(character) || character is '"' or '*' or ':' or '<' or '>' or '?' or '|'
        || char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Format;

    /// <summary>Cuts a too-long name to the limit, keeping its extension unless that would take half of it,
    /// and never splitting a surrogate pair.</summary>
    private static string Shorten(string name)
    {
        var extension = Path.GetExtension(name);
        if (extension.Length >= MailboxLimits.MaxAttachmentNameLength / 2) extension = string.Empty;
        var keep = MailboxLimits.MaxAttachmentNameLength - extension.Length;
        if (char.IsHighSurrogate(name[keep - 1])) keep--;
        return name[..keep].TrimEnd('.', ' ') + extension;
    }
}
