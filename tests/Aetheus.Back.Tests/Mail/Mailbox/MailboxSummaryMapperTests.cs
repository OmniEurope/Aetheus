// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail.Mailbox;
using MailKit;
using MimeKit;

namespace Aetheus.Back.Tests.Mail;

/// <summary>A mailbox row shows the envelope when the server sent one, falls back to the internal date,
/// and reports the seen flag, the attachments and the size the summary carries.</summary>
public sealed class MailboxSummaryMapperTests
{
    [Fact]
    public void ACompleteSummary_FillsEveryColumn()
    {
        var envelope = new Envelope { Subject = "Invoice", Date = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero) };
        envelope.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        envelope.To.Add(new MailboxAddress(string.Empty, "bob@example.com"));
        var attachment = new BodyPartBasic(new ContentType("application", "pdf"), "2")
        {
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "invoice.pdf" },
        };
        var body = new BodyPartMultipart(new ContentType("multipart", "mixed"), string.Empty);
        body.BodyParts.Add(attachment);
        var summary = new MessageSummary(0)
        {
            UniqueId = new UniqueId(42),
            Envelope = envelope,
            Flags = MessageFlags.Seen | MessageFlags.Answered,
            Body = body,
            Size = 2048,
        };

        var row = MailboxSummaryMapper.ToSummary(summary);

        Assert.Equal(new MailboxSummary(42, "Alice <alice@example.com>", "bob@example.com", "Invoice",
            new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), true, true, 2048), row);
    }

    [Fact]
    public void ABareSummary_FallsBackToTheInternalDateAndEmptyColumns()
    {
        var internalDate = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var summary = new MessageSummary(0) { UniqueId = new UniqueId(7), InternalDate = internalDate, Flags = MessageFlags.Flagged };

        var row = MailboxSummaryMapper.ToSummary(summary);

        Assert.Equal(new MailboxSummary(7, string.Empty, string.Empty, string.Empty, internalDate, false, false, 0), row);
    }

    [Fact]
    public void NoDateAtAll_IsTheMinimumDate()
    {
        var row = MailboxSummaryMapper.ToSummary(new MessageSummary(0) { UniqueId = new UniqueId(8) });

        Assert.Equal(DateTimeOffset.MinValue, row.Date);
        Assert.False(row.Seen);
    }
}
