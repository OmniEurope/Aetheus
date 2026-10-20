// SPDX-License-Identifier: EUPL-1.2
using MailKit;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>The list row of one IMAP message, read from the summary the server returned; extracted from
/// <see cref="MailKitMailboxConnector"/> so it can be checked without a server.</summary>
internal static class MailboxSummaryMapper
{
    public static MailboxSummary ToSummary(IMessageSummary summary) => new(
        summary.UniqueId.Id,
        MailboxAddresses.Format(summary.Envelope?.From),
        MailboxAddresses.Format(summary.Envelope?.To),
        summary.Envelope?.Subject ?? string.Empty,
        summary.Envelope?.Date ?? summary.InternalDate ?? DateTimeOffset.MinValue,
        IsSeen(summary),
        summary.Attachments?.Any() == true,
        (long)(summary.Size ?? 0));

    public static bool IsSeen(IMessageSummary summary) =>
        summary.Flags is { } flags && (flags & MessageFlags.Seen) != 0;
}
