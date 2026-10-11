// SPDX-License-Identifier: EUPL-1.2
using MimeKit;

namespace Aetheus.Back.Components.Mail.Mailbox;

// What IMailboxSession returns, independent of MailKit's own types.

public sealed record MailboxFolderInfo(string Path, string Name, MailboxFolderRole Role, int Total, int Unread);

public sealed record MailboxSummary(
    uint Uid, string From, string To, string Subject, DateTimeOffset Date, bool Seen, bool HasAttachments, long Size);

public sealed record MailboxPage(int Total, IReadOnlyList<MailboxSummary> Items);

public sealed record MailboxFetchedMessage(uint Uid, MimeMessage Message, bool Seen);

/// <summary>The safe HTML and the remote resources it no longer loads.</summary>
public sealed record MailboxSanitizedHtml(string Html, IReadOnlyList<string> BlockedRemoteUrls);

/// <summary>An attachment's bytes, as downloaded.</summary>
public sealed record MailboxAttachmentContent(string FileName, string ContentType, byte[] Content);
