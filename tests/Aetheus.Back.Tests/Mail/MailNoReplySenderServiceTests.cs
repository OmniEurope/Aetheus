// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Settings;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services.Email;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Aetheus prepares the no-reply mailbox with a password it generates and
/// stores.</summary>
public sealed class MailNoReplySenderServiceTests
{
    private const string NoReply = "no-reply@example.org";

    private readonly IEmailSenderConfigurationResolver _resolver = Substitute.For<IEmailSenderConfigurationResolver>();
    private readonly IMailService _mail = Substitute.For<IMailService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IEmailSenderSettingsService _senderSettings = Substitute.For<IEmailSenderSettingsService>();
    private readonly MailNoReplySenderService _sut;

    public MailNoReplySenderServiceTests()
    {
        _senderSettings.GetAsync(Arg.Any<CancellationToken>()).Returns(new EmailSenderSettingsDto());
        _mail.GetAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _sut = new MailNoReplySenderService(_resolver, _mail, _settings, _senderSettings);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MissingMailbox_IsCreated_WithTheGeneratedPasswordStoredAsASecret()
    {
        _resolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(new EmailSenderResolution
        {
            Problem = EmailSenderProblem.NoReplyAccountMissing,
            NoReplyAddress = NoReply,
            NoReplyServerId = 3
        });
        CreateMailAccountRequest? created = null;
        await _mail.CreateAccountAsync(3, Arg.Do<CreateMailAccountRequest>(request => created = request), Arg.Any<CancellationToken>());

        await _sut.PrepareAsync(Ct);

        Assert.NotNull(created);
        Assert.Equal(NoReply, created!.Email);
        Assert.Equal(40, created.Password.Length);
        await _settings.Received(1).CreateSecretAsync(
            Arg.Is<CreateSecretRequest>(secret =>
                secret.Key == EmailSenderSettingKeys.NoReplyPasswordSecret(NoReply) && secret.Value == created.Password),
            Arg.Any<CancellationToken>());
        await _mail.DidNotReceive().UpdateAccountAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<UpdateMailAccountRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MailboxGoneFromTheServer_IsCreatedAgain_NotUpdated()
    {
        // Production state: the row exists but is inactive and flagged absent, so the resolver reports no account.
        _resolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(new EmailSenderResolution
        {
            Problem = EmailSenderProblem.NoReplyAccountMissing,
            NoReplyAddress = NoReply,
            NoReplyServerId = 3
        });
        _mail.GetAccountsAsync(3, Arg.Any<CancellationToken>()).Returns(
        [
            new MailAccountDto { Id = 12, Email = NoReply, IsActive = false, MissingSince = DateTime.UtcNow.AddDays(-1) }
        ]);

        await _sut.PrepareAsync(Ct);

        await _mail.Received(1).CreateAccountAsync(3,
            Arg.Is<CreateMailAccountRequest>(request => request.Email == NoReply && request.Password.Length == 40),
            Arg.Any<CancellationToken>());
        await _mail.DidNotReceive().UpdateAccountAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<UpdateMailAccountRequest>(), Arg.Any<CancellationToken>());
        await _settings.Received(1).CreateSecretAsync(Arg.Any<CreateSecretRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MailboxDisabledByHand_GetsANewPassword_AndIsEnabledAgain()
    {
        _resolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(new EmailSenderResolution
        {
            Problem = EmailSenderProblem.NoReplyAccountMissing,
            NoReplyAddress = NoReply,
            NoReplyServerId = 3
        });
        _mail.GetAccountsAsync(3, Arg.Any<CancellationToken>()).Returns(
        [
            new MailAccountDto { Id = 12, Email = "No-Reply@Example.org", IsActive = false }
        ]);

        await _sut.PrepareAsync(Ct);

        await _mail.Received(1).UpdateAccountAsync(3, 12,
            Arg.Is<UpdateMailAccountRequest>(request => request.IsActive == true && !string.IsNullOrEmpty(request.NewPassword)),
            Arg.Any<CancellationToken>());
        await _mail.DidNotReceive().CreateAccountAsync(Arg.Any<int>(), Arg.Any<CreateMailAccountRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistingMailbox_GetsANewPassword_ThatIsStored()
    {
        _resolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(new EmailSenderResolution
        {
            Problem = EmailSenderProblem.NoReplyPasswordUnknown,
            NoReplyAddress = NoReply,
            NoReplyServerId = 3,
            NoReplyAccountId = 12
        });
        UpdateMailAccountRequest? updated = null;
        await _mail.UpdateAccountAsync(3, 12, Arg.Do<UpdateMailAccountRequest>(request => updated = request), Arg.Any<CancellationToken>());

        await _sut.PrepareAsync(Ct);

        Assert.False(string.IsNullOrEmpty(updated?.NewPassword));
        await _settings.Received(1).CreateSecretAsync(
            Arg.Is<CreateSecretRequest>(secret => secret.Value == updated!.NewPassword),
            Arg.Any<CancellationToken>());
        await _mail.DidNotReceive().CreateAccountAsync(Arg.Any<int>(), Arg.Any<CreateMailAccountRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EachPreparation_GeneratesAFreshPassword()
    {
        _resolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(new EmailSenderResolution
        {
            NoReplyAddress = NoReply,
            NoReplyServerId = 3,
            NoReplyAccountId = 12
        });
        var passwords = new List<string>();
        await _mail.UpdateAccountAsync(3, 12, Arg.Do<UpdateMailAccountRequest>(request => passwords.Add(request.NewPassword!)), Arg.Any<CancellationToken>());

        await _sut.PrepareAsync(Ct);
        await _sut.PrepareAsync(Ct);

        Assert.Equal(2, passwords.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task NoMailDomain_IsRefused_AndNothingIsStored()
    {
        _resolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(new EmailSenderResolution { Problem = EmailSenderProblem.NoMailDomain });

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.PrepareAsync(Ct));

        await _settings.DidNotReceive().CreateSecretAsync(Arg.Any<CreateSecretRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefusedTask_LeavesThePreviousSecretUntouched()
    {
        _resolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(new EmailSenderResolution
        {
            NoReplyAddress = NoReply,
            NoReplyServerId = 3
        });
        _mail.CreateAccountAsync(3, Arg.Any<CreateMailAccountRequest>(), Arg.Any<CancellationToken>())
            .Returns<MailAccountDto>(_ => throw new BadRequestException("Mail management is not enabled on this server."));

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.PrepareAsync(Ct));

        await _settings.DidNotReceive().CreateSecretAsync(Arg.Any<CreateSecretRequest>(), Arg.Any<CancellationToken>());
    }
}
