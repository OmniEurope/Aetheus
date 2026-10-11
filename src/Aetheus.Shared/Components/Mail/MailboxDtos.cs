// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Mail;

// Recette R1-17: the mailbox of one account of a mail server Aetheus manages, read over IMAP and sent
// over SMTP submission from the backend. Aetheus does not know mailbox passwords: the reader types it
// to unlock the mailbox, the backend keeps it in memory only, for a bounded time, for that reader alone.
// No DTO here ever carries a password back.

/// <summary>Where an external mail client connects: the same for every mailbox of the server.</summary>
public sealed record MailClientSettingsDto
{
    [StringLength(253)]
    public string Host { get; init; } = string.Empty;
    public int ImapPort { get; init; }
    public int SubmissionPort { get; init; }
}

/// <summary>What the mailbox page needs before it opens a folder, and the settings of an external client.</summary>
public sealed record MailboxAccessDto
{
    public int AccountId { get; init; }
    [StringLength(254)]
    public string Email { get; init; } = string.Empty;
    [StringLength(253)]
    public string Host { get; init; } = string.Empty;
    public int ImapPort { get; init; }
    public int SubmissionPort { get; init; }
    /// <summary>The login of the mailbox: the full address.</summary>
    [StringLength(254)]
    public string Username { get; init; } = string.Empty;
    /// <summary>True while the backend holds the password this reader typed.</summary>
    public bool IsUnlocked { get; init; }
    /// <summary>When the held password is forgotten unless the mailbox is used again (UTC); null when locked.</summary>
    public DateTime? UnlockedUntil { get; init; }
}

/// <summary>The mailbox password, typed by the reader. Write-only: checked against the server, never stored.</summary>
public sealed record MailboxUnlockRequest
{
    [Required]
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = 1)]
    public string Password { get; init; } = string.Empty;
}

/// <summary>A folder to create at the top of the mailbox.</summary>
public sealed record MailboxCreateFolderRequest
{
    [Required]
    [StringLength(MailboxLimits.MaxFolderNameLength, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;
}

/// <summary>Marks a message read or unread.</summary>
public sealed record MailboxReadStateRequest
{
    public bool IsRead { get; init; }
}

public enum MailboxFolderRole
{
    Other = 0,
    Inbox = 1,
    Sent = 2,
    Drafts = 3,
    Trash = 4,
    Junk = 5,
    Archive = 6
}

public sealed record MailboxFolderDto
{
    /// <summary>The IMAP full name, the identifier every other call takes.</summary>
    [StringLength(500)]
    public string Path { get; init; } = string.Empty;
    [StringLength(250)]
    public string Name { get; init; } = string.Empty;
    public MailboxFolderRole Role { get; init; }
    public int Total { get; init; }
    public int Unread { get; init; }
}

public sealed record MailboxMessageSummaryDto
{
    public uint Uid { get; init; }
    [StringLength(500)]
    public string From { get; init; } = string.Empty;
    [StringLength(2000)]
    public string To { get; init; } = string.Empty;
    [StringLength(1000)]
    public string Subject { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public bool IsRead { get; init; }
    public bool HasAttachments { get; init; }
    public long Size { get; init; }
}

public sealed record MailboxAttachmentDto
{
    /// <summary>Position of the attachment in the message, the identifier of its download.</summary>
    public int Index { get; init; }
    [StringLength(500)]
    public string FileName { get; init; } = string.Empty;
    [StringLength(200)]
    public string ContentType { get; init; } = string.Empty;
    public long Size { get; init; }
}

public sealed record MailboxMessageDto
{
    public uint Uid { get; init; }
    [StringLength(500)]
    public string Folder { get; init; } = string.Empty;
    [StringLength(500)]
    public string From { get; init; } = string.Empty;
    [StringLength(4000)]
    public string To { get; init; } = string.Empty;
    [StringLength(4000)]
    public string Cc { get; init; } = string.Empty;
    [StringLength(1000)]
    public string Subject { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public bool IsRead { get; init; }
    /// <summary>Sanitized HTML (no script, no event handler, no form); null when the message has no HTML part.</summary>
    public string? Html { get; init; }
    /// <summary>The plain text part, or the text drawn from the HTML when there is none.</summary>
    public string Text { get; init; } = string.Empty;
    /// <summary>How many remote images were withheld; the page then offers "show images".</summary>
    public int BlockedRemoteImages { get; init; }
    public List<MailboxAttachmentDto> Attachments { get; init; } = [];
}

public enum MailboxComposeMode
{
    New = 0,
    Reply = 1,
    ReplyAll = 2,
    Forward = 3
}

/// <summary>The fields of a message to send. A forward sends the original attachments again, beside the files the writer attached.</summary>
public sealed record MailboxSendRequest
{
    public MailboxComposeMode Mode { get; init; }

    /// <summary>Comma or semicolon separated addresses.</summary>
    [StringLength(MailboxLimits.MaxAddressListLength)]
    public string To { get; init; } = string.Empty;
    [StringLength(MailboxLimits.MaxAddressListLength)]
    public string Cc { get; init; } = string.Empty;
    [StringLength(MailboxLimits.MaxAddressListLength)]
    public string Bcc { get; init; } = string.Empty;
    [StringLength(1000)]
    public string Subject { get; init; } = string.Empty;
    [StringLength(MailboxLimits.MaxBodyLength)]
    public string Body { get; init; } = string.Empty;

    /// <summary>The message replied to or forwarded (folder and UID); unused for <see cref="MailboxComposeMode.New"/>.</summary>
    [StringLength(500)]
    public string? SourceFolder { get; init; }
    public uint? SourceUid { get; init; }

    /// <summary>
    /// Recette R1-38: the files the writer attached, on top of what a forward sends again. Bounded by
    /// <see cref="MailboxLimits.MaxAttachmentCount"/>, <see cref="MailboxLimits.MaxAttachmentBytes"/> each and
    /// <see cref="MailboxLimits.MaxAttachmentsTotalBytes"/> together, checked again by the backend.
    /// </summary>
    [MaxLength(MailboxLimits.MaxAttachmentCount)]
    public List<MailboxOutgoingAttachment> Attachments { get; init; } = [];
}

/// <summary>
/// Recette R1-38: one file the writer attached. Only its name and bytes travel: the backend sanitizes the
/// name and derives the MIME type from its extension, never from what the browser said.
/// </summary>
public sealed record MailboxOutgoingAttachment
{
    [Required]
    [StringLength(MailboxLimits.MaxAttachmentNameLength, MinimumLength = 1)]
    public string FileName { get; init; } = string.Empty;

    [MaxLength(MailboxLimits.MaxAttachmentBytes)]
    public byte[] Content { get; init; } = [];
}

/// <summary>What the compose dialog starts from for a reply or a forward.</summary>
public sealed record MailboxDraftDto
{
    [StringLength(MailboxLimits.MaxAddressListLength)]
    public string To { get; init; } = string.Empty;
    [StringLength(MailboxLimits.MaxAddressListLength)]
    public string Cc { get; init; } = string.Empty;
    [StringLength(1000)]
    public string Subject { get; init; } = string.Empty;
    [StringLength(MailboxLimits.MaxBodyLength)]
    public string Body { get; init; } = string.Empty;
    /// <summary>The original attachments a forward sends again.</summary>
    public List<MailboxAttachmentDto> ForwardedAttachments { get; init; } = [];
}

/// <summary>
/// The message left; <see cref="SavedToSent"/> says whether its copy reached the Sent folder (the
/// server does not keep one on its own), with the server's reason when it did not.
/// </summary>
public sealed record MailboxSendResultDto
{
    public bool SavedToSent { get; init; }
    [StringLength(2000)]
    public string? SentCopyProblem { get; init; }
}

/// <summary>Whether the message went to the Trash folder or, already there, was erased.</summary>
public sealed record MailboxDeleteResultDto
{
    public bool Permanent { get; init; }
}

public static class MailboxLimits
{
    public const int MaxAddressListLength = 4000;
    public const int MaxBodyLength = 200_000;
    public const int MaxPageSize = 100;
    public const int MaxFolderNameLength = 100;

    // Recette R1-38: the files a writer attaches to a message, the one place these limits are set. The
    // compose dialog checks them before sending, the backend again before building the message.
    public const int MaxAttachmentCount = 10;
    public const int MaxAttachmentBytes = 10 * 1024 * 1024;
    public const int MaxAttachmentsTotalBytes = 20 * 1024 * 1024;
    public const int MaxAttachmentNameLength = 255;

    /// <summary>
    /// The largest send request the backend reads: the attachments travel as base64 in the JSON body (4 bytes
    /// for 3), plus room for the addresses and a body of <see cref="MaxBodyLength"/> characters fully escaped.
    /// </summary>
    public const long MaxSendRequestBytes = (MaxAttachmentsTotalBytes / 3L * 4L) + (4L * 1024 * 1024);
}
