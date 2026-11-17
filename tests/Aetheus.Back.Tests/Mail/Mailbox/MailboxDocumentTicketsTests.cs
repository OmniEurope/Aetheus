// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail.Mailbox;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests;

/// <summary>The message document tickets are random, single-use, short-lived and bounded; the document's
/// policy applies the inline styles and nothing else.</summary>
public sealed class MailboxDocumentTicketsTests
{
    private static readonly MailboxCredentialKey s_alice = new("Bearer:1", 7, 3);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
    private readonly MailboxDocumentTickets _tickets;

    public MailboxDocumentTicketsTests() => _tickets = new MailboxDocumentTickets(_time);

    [Fact]
    public void A_ticket_is_redeemed_once_with_its_key_and_document()
    {
        var ticket = _tickets.Issue(s_alice, "<p>x</p>");

        Assert.Equal(43, ticket.Length); // 32 random bytes, base64url
        Assert.Equal(s_alice, _tickets.Peek(ticket)?.Key);
        var redeemed = _tickets.Redeem(ticket);
        Assert.Equal("<p>x</p>", redeemed?.Document);
        Assert.Null(_tickets.Redeem(ticket));
        Assert.Null(_tickets.Peek(ticket));
    }

    [Fact]
    public void Two_tickets_for_the_same_message_differ()
    {
        Assert.NotEqual(_tickets.Issue(s_alice, "a"), _tickets.Issue(s_alice, "a"));
    }

    [Fact]
    public void An_expired_ticket_is_refused()
    {
        var ticket = _tickets.Issue(s_alice, "<p>x</p>");

        _time.Advance(MailboxDocumentTickets.Lifetime);

        Assert.Null(_tickets.Peek(ticket));
        Assert.Null(_tickets.Redeem(ticket));
    }

    [Fact]
    public void An_unknown_or_empty_ticket_is_refused()
    {
        Assert.Null(_tickets.Peek("nope"));
        Assert.Null(_tickets.Redeem(string.Empty));
    }

    [Fact]
    public void The_oldest_ticket_goes_first_past_the_bound()
    {
        var first = _tickets.Issue(s_alice, "first");
        _time.Advance(TimeSpan.FromMilliseconds(1));
        for (var i = 0; i < MailboxDocumentTickets.MaxEntries; i++) _tickets.Issue(s_alice, "next");

        Assert.Null(_tickets.Peek(first));
    }

    [Fact]
    public void The_document_policy_allows_inline_styles_and_data_images_only_framed_by_the_interface()
    {
        var policy = new MailboxDocumentPolicy(["https://app.example.com/", "not a url", "https://app.example.com", "ftp://x.example"]);

        Assert.Equal(
            "default-src 'none'; style-src-attr 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'; " +
            "frame-ancestors https://app.example.com",
            policy.ContentSecurityPolicy);
        Assert.EndsWith("frame-ancestors 'none'", new MailboxDocumentPolicy([]).ContentSecurityPolicy, StringComparison.Ordinal);
    }
}
