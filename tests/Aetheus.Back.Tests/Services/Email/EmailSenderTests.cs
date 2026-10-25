// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.Email;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

/// <summary>The sender reports every outcome, with the SMTP text verbatim.</summary>
public sealed class EmailSenderTests
{
    private static readonly EmailSenderConfiguration Configuration =
        new("mail.example.org", 587, SmtpTlsMode.StartTls, "no-reply@example.org", "pw", "no-reply@example.org");

    private readonly IEmailSenderConfigurationResolver _resolver = Substitute.For<IEmailSenderConfigurationResolver>();
    private readonly ISmtpTransport _transport = Substitute.For<ISmtpTransport>();
    private readonly ISmtpSession _session = Substitute.For<ISmtpSession>();
    private readonly EmailSender _sut;

    public EmailSenderTests()
    {
        _resolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(new EmailSenderResolution { Source = EmailSenderSource.ManagedNoReply, Configuration = Configuration });
        _transport.ConnectAsync(Configuration, Arg.Any<CancellationToken>()).Returns(_session);
        _sut = new EmailSender(_resolver, _transport, NullLogger<EmailSender>.Instance);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EmailMessage Message(string to) => new(to, "Subject", "Text", "<p>Html</p>");

    [Fact]
    public async Task NoSender_NothingIsAttempted_AndTheReasonIsReturned()
    {
        _resolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(new EmailSenderResolution { Problem = EmailSenderProblem.NoMailDomain });

        var result = await _sut.SendAsync(Message("a@x.org"), Ct);

        Assert.Equal(EmailSendStatus.NotConfigured, result.Status);
        Assert.Contains("manages no mail domain", result.Message, StringComparison.Ordinal);
        await _transport.DidNotReceive().ConnectAsync(Arg.Any<EmailSenderConfiguration>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Batch_SharesOneConnection_AndEveryAcceptedMessageIsSent()
    {
        var results = await _sut.SendBatchAsync([Message("a@x.org"), Message("b@x.org")], Ct);

        Assert.All(results, result => Assert.Equal(EmailSendStatus.Sent, result.Status));
        Assert.Contains("no-reply@example.org", results[0].Message, StringComparison.Ordinal);
        await _transport.Received(1).ConnectAsync(Configuration, Arg.Any<CancellationToken>());
        await _session.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.To == "a@x.org"), Arg.Any<CancellationToken>());
        await _session.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.To == "b@x.org"), Arg.Any<CancellationToken>());
        await _session.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ConnectionRefused_EveryMessageFails_WithTheServerTextVerbatim()
    {
        _transport.ConnectAsync(Configuration, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("535: 5.7.8 Error: authentication failed"));

        var results = await _sut.SendBatchAsync([Message("a@x.org"), Message("b@x.org")], Ct);

        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(EmailSendStatus.Failed, result.Status);
            Assert.Equal("535: 5.7.8 Error: authentication failed", result.Message);
        });
    }

    [Fact]
    public async Task OneRefusedRecipient_DoesNotStopTheOthers()
    {
        _session.SendAsync(Arg.Is<EmailMessage>(m => m.To == "bad@x.org"), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("550 5.1.1 <bad@x.org>: Recipient address rejected"));

        var results = await _sut.SendBatchAsync([Message("bad@x.org"), Message("good@x.org")], Ct);

        Assert.Equal(EmailSendStatus.Failed, results[0].Status);
        Assert.Equal("550 5.1.1 <bad@x.org>: Recipient address rejected", results[0].Message);
        Assert.Equal(EmailSendStatus.Sent, results[1].Status);
    }

    [Fact]
    public async Task CallerCancellation_IsPropagated_NotReportedAsAFailure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _transport.ConnectAsync(Configuration, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _sut.SendAsync(Message("a@x.org"), cts.Token));
    }

    private EmailSender SenderWithRepair(INoReplyCredentialRepair repair) =>
        new(_resolver, _transport, NullLogger<EmailSender>.Instance, repair);

    [Fact]
    public async Task ManagedSender_RefusedPassword_Repairs_AndStillReportsTheRefusalVerbatim()
    {
        var repair = Substitute.For<INoReplyCredentialRepair>();
        _transport.ConnectAsync(Configuration, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AuthenticationException("535: 5.7.8 Error: authentication failed: (reason unavailable)"));

        var result = await SenderWithRepair(repair).SendAsync(Message("a@x.org"), Ct);

        Assert.Equal(EmailSendStatus.Failed, result.Status);
        Assert.Equal("535: 5.7.8 Error: authentication failed: (reason unavailable)", result.Message);
        await repair.Received(1).TryRepairAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManagedSender_OtherConnectionFailure_DoesNotRepair()
    {
        var repair = Substitute.For<INoReplyCredentialRepair>();
        _transport.ConnectAsync(Configuration, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Connection refused"));

        await SenderWithRepair(repair).SendAsync(Message("a@x.org"), Ct);

        await repair.DidNotReceive().TryRepairAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CustomSender_RefusedPassword_IsNeverRepaired()
    {
        // The password of a custom SMTP server is the administrator's own: Aetheus must not replace anything.
        var repair = Substitute.For<INoReplyCredentialRepair>();
        _resolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(new EmailSenderResolution { Source = EmailSenderSource.Custom, Configuration = Configuration });
        _transport.ConnectAsync(Configuration, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AuthenticationException("535 authentication failed"));

        var result = await SenderWithRepair(repair).SendAsync(Message("a@x.org"), Ct);

        Assert.Equal(EmailSendStatus.Failed, result.Status);
        await repair.DidNotReceive().TryRepairAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedRepair_DoesNotHideTheRefusal()
    {
        var repair = Substitute.For<INoReplyCredentialRepair>();
        repair.TryRepairAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("database down"));
        _transport.ConnectAsync(Configuration, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AuthenticationException("535 authentication failed"));

        var result = await SenderWithRepair(repair).SendAsync(Message("a@x.org"), Ct);

        Assert.Equal(EmailSendStatus.Failed, result.Status);
        Assert.Equal("535 authentication failed", result.Message);
    }

    [Fact]
    public async Task EmptyBatch_ResolvesNothing()
    {
        var results = await _sut.SendBatchAsync([], Ct);

        Assert.Empty(results);
        await _resolver.DidNotReceive().ResolveAsync(Arg.Any<CancellationToken>());
    }
}
