// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using MimeKit;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// Recette R1-38: the files a writer attaches to a message. The limits of <see cref="MailboxLimits"/> are
/// checked again here, whatever the browser checked. The bytes are only copied into the MIME message, never
/// opened, parsed or rendered. The browser's content type does not travel: the type written in the message
/// comes from the sanitized name's extension, and is only a label for the recipient's client.
/// </summary>
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
        var raw = fileName ?? string.Empty;
        var lastSeparator = raw.LastIndexOfAny(s_separators);
        if (lastSeparator >= 0) raw = raw[(lastSeparator + 1)..];

        var builder = new StringBuilder(raw.Length);
        foreach (var character in raw.Normalize(NormalizationForm.FormC))
        {
            if (char.IsControl(character) || character is '"' or '*' or ':' or '<' or '>' or '?' or '|'
                || char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Format)
                builder.Append('_');
            else
                builder.Append(character);
        }

        var name = builder.ToString().Trim().Trim('.', ' ');
        if (name.Length == 0 || name.All(character => character == '_')) return $"attachment-{index + 1}";
        if (name.Length <= MailboxLimits.MaxAttachmentNameLength) return name;

        var extension = Path.GetExtension(name);
        if (extension.Length >= MailboxLimits.MaxAttachmentNameLength / 2) extension = string.Empty;
        var keep = MailboxLimits.MaxAttachmentNameLength - extension.Length;
        if (char.IsHighSurrogate(name[keep - 1])) keep--;
        return name[..keep].TrimEnd('.', ' ') + extension;
    }
}
