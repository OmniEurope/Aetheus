// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Back.Components.Mail.Mailbox;
using Aetheus.Back.Exceptions;
using Microsoft.AspNetCore.Mvc;
using MimeKit;

namespace Aetheus.Back.Tests;

/// <summary>The limits, names and MIME parts of the files a writer attaches to a message.</summary>
public sealed class MailboxOutgoingAttachmentsTests
{
    private const string Own = "me@example.org";
    private static readonly DateTimeOffset s_now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private static MailboxOutgoingAttachment File(string name, int size, byte fill = 0x41) =>
        new() { FileName = name, Content = Enumerable.Repeat(fill, size).ToArray() };

    private static MimeMessage Build(MailboxComposeMode mode, MimeMessage? original, params MailboxOutgoingAttachment[] attachments) =>
        MailboxComposer.BuildMessage(
            new MailboxSendRequest { Mode = mode, To = "alice@example.com", Subject = "s", Body = "b", Attachments = [.. attachments] },
            new MailboxAddress(string.Empty, Own), original, s_now);

    [Fact]
    public void The_limits_are_ten_files_ten_megabytes_each_and_twenty_in_total()
    {
        Assert.Equal((10, 10 * 1024 * 1024, 20 * 1024 * 1024),
            (MailboxLimits.MaxAttachmentCount, MailboxLimits.MaxAttachmentBytes, MailboxLimits.MaxAttachmentsTotalBytes));
    }

    [Fact]
    public void Ten_files_pass_and_an_eleventh_is_refused()
    {
        var ten = Enumerable.Range(1, 10).Select(index => File($"f{index}.txt", 10)).ToList();

        MailboxOutgoingAttachments.Validate(ten);
        var error = Assert.Throws<BadRequestException>(() => MailboxOutgoingAttachments.Validate([.. ten, File("f11.txt", 10)]));
        Assert.Contains("10", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_of_exactly_ten_megabytes_passes_and_one_byte_more_is_refused()
    {
        MailboxOutgoingAttachments.Validate([File("max.bin", MailboxLimits.MaxAttachmentBytes)]);

        Assert.Throws<BadRequestException>(() =>
            MailboxOutgoingAttachments.Validate([File("over.bin", MailboxLimits.MaxAttachmentBytes + 1)]));
    }

    [Fact]
    public void Twenty_megabytes_in_total_pass_and_one_byte_more_is_refused()
    {
        MailboxOutgoingAttachments.Validate([File("a.bin", MailboxLimits.MaxAttachmentBytes), File("b.bin", MailboxLimits.MaxAttachmentBytes)]);

        Assert.Throws<BadRequestException>(() => MailboxOutgoingAttachments.Validate(
            [File("a.bin", MailboxLimits.MaxAttachmentBytes), File("b.bin", MailboxLimits.MaxAttachmentBytes), File("c.bin", 1)]));
    }

    [Fact]
    public void Building_the_message_enforces_the_limits_too()
    {
        Assert.Throws<BadRequestException>(() => Build(MailboxComposeMode.New, null,
            File("a.bin", MailboxLimits.MaxAttachmentBytes), File("b.bin", MailboxLimits.MaxAttachmentBytes), File("c.bin", 1)));
    }

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\me\\secret.docx", "secret.docx")]
    [InlineData("in\u0000voice\r\n.pdf", "in_voice__.pdf")]
    [InlineData("a<b>c:d\"e|f?g*.txt", "a_b_c_d_e_f_g_.txt")]
    [InlineData("evil\u202Etxt.exe", "evil_txt.exe")]
    [InlineData("  .hidden. ", "hidden")]
    [InlineData("...", "attachment-3")]
    [InlineData("", "attachment-3")]
    [InlineData(null, "attachment-3")]
    [InlineData("dir/", "attachment-3")]
    public void File_names_are_sanitized(string? name, string expected)
    {
        Assert.Equal(expected, MailboxOutgoingAttachments.SanitizeFileName(name, 2));
    }

    [Fact]
    public void A_name_too_long_is_cut_and_keeps_its_extension()
    {
        var sanitized = MailboxOutgoingAttachments.SanitizeFileName(new string('x', 400) + ".pdf", 0);

        Assert.Equal(MailboxLimits.MaxAttachmentNameLength, sanitized.Length);
        Assert.EndsWith("x.pdf", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_file_becomes_a_base64_attachment_with_its_bytes_and_a_type_from_its_extension()
    {
        var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0xFF };

        var message = Build(MailboxComposeMode.New, null,
            new MailboxOutgoingAttachment { FileName = "folder/report.pdf", Content = bytes },
            File("notes", 3));

        var parts = message.Attachments.Cast<MimePart>().ToList();
        Assert.Equal(2, parts.Count);
        Assert.All(parts, part =>
        {
            Assert.Equal(ContentEncoding.Base64, part.ContentTransferEncoding);
            Assert.True(part.IsAttachment);
        });
        Assert.Equal(("report.pdf", "application/pdf"), (parts[0].FileName, parts[0].ContentType.MimeType));
        Assert.Equal(("notes", "application/octet-stream"), (parts[1].FileName, parts[1].ContentType.MimeType));
        using var decoded = new MemoryStream();
        parts[0].Content!.DecodeTo(decoded, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, decoded.ToArray());
        Assert.Equal("b", message.TextBody!.TrimEnd());
    }

    [Fact]
    public void The_message_survives_a_round_trip_through_mime_with_its_attachment()
    {
        var message = Build(MailboxComposeMode.New, null, File("data.csv", 5000, 0x33));
        using var wire = new MemoryStream();
        message.WriteTo(wire, TestContext.Current.CancellationToken);
        wire.Position = 0;

        var parsed = MimeMessage.Load(wire, TestContext.Current.CancellationToken);

        var part = Assert.IsAssignableFrom<MimePart>(Assert.Single(parsed.Attachments));
        Assert.Equal("data.csv", part.FileName);
        using var decoded = new MemoryStream();
        part.Content!.DecodeTo(decoded, TestContext.Current.CancellationToken);
        Assert.Equal(5000, decoded.Length);
    }

    [Fact]
    public void A_forward_carries_the_original_attachments_and_the_writer_files()
    {
        var original = new MimeMessage();
        original.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        original.To.Add(new MailboxAddress(string.Empty, Own));
        original.Subject = "Planning";
        var body = new BodyBuilder { TextBody = "see attached" };
        body.Attachments.Add("report.pdf", [1, 2, 3]);
        original.Body = body.ToMessageBody();

        var message = Build(MailboxComposeMode.Forward, original, File("mine.txt", 4));

        Assert.Equal(["report.pdf", "mine.txt"], message.Attachments.Select(entity => entity.ContentDisposition?.FileName).ToArray());
    }

    [Fact]
    public void The_send_endpoint_reads_enough_for_twenty_megabytes_in_base64_and_no_more_than_its_limit()
    {
        var limit = typeof(MailboxController).GetMethod(nameof(MailboxController.Send))!
            .GetCustomAttribute<RequestSizeLimitAttribute>();

        Assert.Equal(MailboxLimits.MaxSendRequestBytes, ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)limit!).MaxRequestBodySize);
        Assert.True(MailboxLimits.MaxSendRequestBytes > (MailboxLimits.MaxAttachmentsTotalBytes + 2L) / 3 * 4 + (2L * MailboxLimits.MaxBodyLength));
        Assert.True(MailboxLimits.MaxSendRequestBytes < 40L * 1024 * 1024);
    }
}
