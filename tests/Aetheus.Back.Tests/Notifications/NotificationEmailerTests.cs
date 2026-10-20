// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Services.Email;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Notifications become HTML and plain-text e-mails, one per addressed user.</summary>
public sealed class NotificationEmailerTests
{
    private readonly IEmailSender _sender = Substitute.For<IEmailSender>();
    private readonly NotificationEmailer _sut;

    public NotificationEmailerTests() => _sut = new NotificationEmailer(_sender, NotificationMailKit.Templates());

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UsersWithoutAddress_AreNotConfigured_AndTheOthersKeepTheirOrder()
    {
        _sender.SendBatchAsync(Arg.Any<IReadOnlyList<EmailMessage>>(), Arg.Any<CancellationToken>())
            .Returns([EmailSendResult.Sent("no-reply@x.org"), EmailSendResult.Failed("550 rejected")]);

        var results = await _sut.SendAsync(
        [
            new NotificationEmail("alice@x.org", "pipeline.failed in project Shop", "pipeline.failed", "{\"ProjectId\":10}"),
            new NotificationEmail(null, "pipeline.failed in project Shop", "pipeline.failed", "{}"),
            new NotificationEmail("carol@x.org", "pipeline.failed in project Shop", "pipeline.failed", "{}")
        ], Ct);

        Assert.Equal(EmailSendStatus.Sent, results[0].Status);
        Assert.Equal(EmailSendStatus.NotConfigured, results[1].Status);
        Assert.Equal(NotificationEmailer.NoAddressMessage, results[1].Message);
        Assert.Equal(EmailSendStatus.Failed, results[2].Status);
        await _sender.Received(1).SendBatchAsync(
            Arg.Is<IReadOnlyList<EmailMessage>>(messages =>
                messages.Count == 2 && messages[0].To == "alice@x.org" && messages[1].To == "carol@x.org"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NobodyAddressed_SendsNothing()
    {
        var results = await _sut.SendAsync([new NotificationEmail(" ", "s", "e", "{}")], Ct);

        Assert.Equal(EmailSendStatus.NotConfigured, Assert.Single(results).Status);
        await _sender.DidNotReceive().SendBatchAsync(Arg.Any<IReadOnlyList<EmailMessage>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Build_CarriesTextAndEncodedHtml()
    {
        var message = NotificationMailKit.Build(
            new NotificationEmail("alice@x.org", "release <b>1.0</b> deployed", "release.deployed", "{\"Version\":\"<1.0>\"}"));

        Assert.Equal("[Aetheus] Release deployed: release <b>1.0</b> deployed", message.Subject);
        Assert.Contains("Source event\trelease.deployed", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("Version\t<1.0>", message.TextBody, StringComparison.Ordinal);
        Assert.NotNull(message.HtmlBody);
        Assert.Contains("release &lt;b&gt;1.0&lt;/b&gt; deployed", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>1.0</b>", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&lt;1.0&gt;", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChannelTest_WithoutAddress_IsNotConfigured_AndSendsNothing()
    {
        var result = await _sut.SendChannelTestAsync(null, "mail", Ct);

        Assert.Equal(EmailSendStatus.NotConfigured, result.Status);
        await _sender.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChannelTest_GoesToTheRequester_WithTheSenderOutcome()
    {
        _sender.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(EmailSendResult.Failed("Connection refused"));

        var result = await _sut.SendChannelTestAsync("admin@x.org", "mail", Ct);

        Assert.Equal("Connection refused", result.Message);
        await _sender.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.To == "admin@x.org" && m.HtmlBody != null && m.Subject.Contains("\"mail\"") && m.TextBody.Contains("mail")),
            Arg.Any<CancellationToken>());
    }
}
