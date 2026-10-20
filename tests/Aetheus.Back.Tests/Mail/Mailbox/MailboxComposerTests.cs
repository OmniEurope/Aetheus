// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail.Mailbox;
using Aetheus.Back.Exceptions;
using MimeKit;

namespace Aetheus.Back.Tests;

/// <summary>Address lists, reply / reply-all / forward drafts and the message finally
/// sent.</summary>
public sealed class MailboxComposerTests
{
    private const string Own = "me@example.org";
    private static readonly DateTimeOffset s_now = new(2026, 10, 10, 9, 30, 0, TimeSpan.Zero);

    private static MimeMessage Original()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        message.To.Add(new MailboxAddress(string.Empty, Own));
        message.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        message.Cc.Add(new MailboxAddress("Carol", "carol@example.com"));
        message.Cc.Add(new MailboxAddress(string.Empty, "ME@example.org"));
        message.Subject = "Planning";
        message.MessageId = "orig-1@example.com";
        message.References.Add("root-0@example.com");
        message.Date = new DateTimeOffset(2026, 10, 9, 14, 5, 0, TimeSpan.Zero);
        var body = new BodyBuilder { TextBody = "Line one\n\nLine two" };
        body.Attachments.Add("report.pdf", [1, 2, 3], new ContentType("application", "pdf"));
        message.Body = body.ToMessageBody();
        return message;
    }

    [Fact]
    public void Parse_accepts_comma_and_semicolon_lists_with_names()
    {
        var parsed = MailboxAddresses.Parse("Alice <alice@example.com>; bob@example.com, ", "To");

        Assert.Equal(["alice@example.com", "bob@example.com"], parsed.Select(address => address.Address));
        Assert.Equal("Alice", parsed[0].Name);
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("alice@example.com, bob")]
    public void Parse_refuses_a_token_that_is_not_an_address_by_name(string value)
    {
        var exception = Assert.Throws<BadRequestException>(() => MailboxAddresses.Parse(value, "Cc"));

        Assert.Contains("Cc", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_of_an_empty_field_is_an_empty_list()
    {
        Assert.Empty(MailboxAddresses.Parse("  ", "Bcc"));
        Assert.Empty(MailboxAddresses.Parse(null, "Bcc"));
    }

    [Fact]
    public void Format_writes_named_and_bare_addresses()
    {
        var list = new InternetAddressList { new MailboxAddress("Alice", "alice@example.com"), new MailboxAddress(string.Empty, "b@example.com") };

        Assert.Equal("Alice <alice@example.com>, b@example.com", MailboxAddresses.Format(list));
        Assert.Equal(string.Empty, MailboxAddresses.Format(null));
    }

    [Fact]
    public void Reply_goes_to_the_sender_only_with_a_quoted_body()
    {
        var draft = MailboxComposer.BuildDraft(MailboxComposeMode.Reply, Original(), Own, "en");

        Assert.Equal("Alice <alice@example.com>", draft.To);
        Assert.Equal(string.Empty, draft.Cc);
        Assert.Equal("Re: Planning", draft.Subject);
        Assert.Contains("wrote:", draft.Body, StringComparison.Ordinal);
        Assert.Contains("> Line one", draft.Body, StringComparison.Ordinal);
        Assert.Contains("\n>\n", draft.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Reply_prefers_reply_to_and_does_not_stack_prefixes()
    {
        var original = Original();
        original.ReplyTo.Add(new MailboxAddress("List", "list@example.com"));
        original.Subject = "RE: Planning";

        var draft = MailboxComposer.BuildDraft(MailboxComposeMode.Reply, original, Own, "fr");

        Assert.Equal("List <list@example.com>", draft.To);
        Assert.Equal("RE: Planning", draft.Subject);
        Assert.Contains("a écrit :", draft.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Reply_all_keeps_everyone_but_the_own_address()
    {
        var draft = MailboxComposer.BuildDraft(MailboxComposeMode.ReplyAll, Original(), Own, "en");

        Assert.Equal("Alice <alice@example.com>, Bob <bob@example.com>", draft.To);
        Assert.Equal("Carol <carol@example.com>", draft.Cc);
        Assert.DoesNotContain(Own, draft.To + draft.Cc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forward_carries_the_original_headers_and_attachments_without_recipients()
    {
        var draft = MailboxComposer.BuildDraft(MailboxComposeMode.Forward, Original(), Own, "en");

        Assert.Equal(string.Empty, draft.To);
        Assert.Equal("Fwd: Planning", draft.Subject);
        Assert.Contains("Forwarded message", draft.Body, StringComparison.Ordinal);
        Assert.Contains("From: Alice <alice@example.com>", draft.Body, StringComparison.Ordinal);
        Assert.Contains("Subject: Planning", draft.Body, StringComparison.Ordinal);
        Assert.Contains("Line two", draft.Body, StringComparison.Ordinal);
        var attachment = Assert.Single(draft.ForwardedAttachments);
        Assert.Equal("report.pdf", attachment.FileName);
        Assert.Equal(3, attachment.Size);
    }

    [Fact]
    public void A_reply_carries_in_reply_to_and_the_references_chain()
    {
        var request = new MailboxSendRequest { Mode = MailboxComposeMode.Reply, To = "alice@example.com", Subject = " Re: Planning ", Body = "Yes\r\nok" };

        var message = MailboxComposer.BuildMessage(request, new MailboxAddress(string.Empty, Own), Original(), s_now);

        Assert.Equal("orig-1@example.com", message.InReplyTo);
        Assert.Equal(["root-0@example.com", "orig-1@example.com"], message.References);
        Assert.Equal("Re: Planning", message.Subject);
        Assert.Equal(s_now, message.Date);
        Assert.EndsWith("@example.org", message.MessageId, StringComparison.Ordinal);
        Assert.Equal("Yes\nok", message.TextBody!.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd());
        Assert.Empty(message.Attachments);
    }

    [Fact]
    public void A_forward_sends_the_original_attachments_again_without_threading_headers()
    {
        var request = new MailboxSendRequest { Mode = MailboxComposeMode.Forward, To = "dave@example.com", Subject = "Fwd: Planning", Body = "FYI" };

        var message = MailboxComposer.BuildMessage(request, new MailboxAddress(string.Empty, Own), Original(), s_now);

        Assert.Null(message.InReplyTo);
        Assert.Empty(message.References);
        Assert.Equal("report.pdf", Assert.Single(message.Attachments).ContentDisposition?.FileName);
    }

    [Fact]
    public void A_message_without_any_recipient_is_refused()
    {
        var request = new MailboxSendRequest { Mode = MailboxComposeMode.New, Subject = "x", Body = "y" };

        Assert.Throws<BadRequestException>(() =>
            MailboxComposer.BuildMessage(request, new MailboxAddress(string.Empty, Own), null, s_now));
    }

    [Fact]
    public void Bcc_recipients_alone_are_enough()
    {
        var request = new MailboxSendRequest { Mode = MailboxComposeMode.New, Bcc = "hidden@example.com", Subject = "x", Body = "y" };

        var message = MailboxComposer.BuildMessage(request, new MailboxAddress(string.Empty, Own), null, s_now);

        Assert.Equal("hidden@example.com", Assert.Single(message.Bcc.Mailboxes).Address);
        Assert.Empty(message.To);
    }

    [Fact]
    public void A_new_message_draft_is_empty()
    {
        var draft = MailboxComposer.BuildDraft(MailboxComposeMode.New, Original(), Own, "en");

        Assert.Equal(new MailboxDraftDto().Subject, draft.Subject);
        Assert.Empty(draft.To);
    }
}
