// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Mail.Mailbox;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Settings;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Aetheus.Back.Services.Email;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>The password the default sender logs in with follows every password set on the no-reply mailbox
/// through Aetheus: a mailbox password changed in the mail module once left the sender on the old value, and the
/// mail server then refused it (SMTP 535).</summary>
public sealed class MailSenderSecretSyncTests
{
    private const string NoReply = "no-reply@example.com";

    private readonly IMailRepository _repo = Substitute.For<IMailRepository>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly MailService _sut;

    public MailSenderSecretSyncTests()
    {
        var encryption = Substitute.For<IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>());
        var serverRepo = Substitute.For<IServerRepository>();
        serverRepo.FindServerAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Name = "srv", Hostname = "h", MailSetupAvailable = true });
        _repo.GetStateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MailState { HelperVersion = MailService.RetryTuningMinimumHelperVersion });
        _repo.GetDomainsAsync(1, Arg.Any<CancellationToken>()).Returns([new MailDomain { Id = 10, ServerId = 1, Name = "example.com" }]);
        _sut = new MailService(
            _repo, Substitute.For<IAuditService>(), encryption, serverRepo,
            Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>(), Substitute.For<IMailMasterSecrets>(),
            new MailSenderSecret(_settings));
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task AssertSecretStoredAsync(string address, string password) =>
        _settings.Received(1).CreateSecretAsync(
            Arg.Is<CreateSecretRequest>(secret =>
                secret.Key == EmailSenderSettingKeys.NoReplyPasswordSecret(address) && secret.Value == password),
            Arg.Any<CancellationToken>());

    [Fact]
    public async Task ChangingThePasswordOfTheNoReplyMailbox_UpdatesTheSenderSecretInTheSameOperation()
    {
        _repo.GetAccountAsync(1, Arg.Any<CancellationToken>()).Returns(
            new MailAccount { Id = 1, Email = NoReply, QuotaMb = 100, MailDomain = new MailDomain { ServerId = 1, Name = "example.com" } });

        await _sut.UpdateAccountAsync(1, 1, new UpdateMailAccountRequest { NewPassword = "typed-by-the-admin-1" }, Ct);

        await AssertSecretStoredAsync(NoReply, "typed-by-the-admin-1");
    }

    [Fact]
    public async Task CreatingTheNoReplyMailboxWithATypedPassword_StoresItForTheSender()
    {
        await _sut.CreateAccountAsync(1, new CreateMailAccountRequest { Email = NoReply, Password = "typed-by-the-admin-2", QuotaMb = 100 }, Ct);

        await AssertSecretStoredAsync(NoReply, "typed-by-the-admin-2");
    }

    [Fact]
    public async Task ChangingThePasswordOfAnotherMailbox_LeavesTheSenderSecretAlone()
    {
        _repo.GetAccountAsync(2, Arg.Any<CancellationToken>()).Returns(
            new MailAccount { Id = 2, Email = "alice@example.com", QuotaMb = 100, MailDomain = new MailDomain { ServerId = 1, Name = "example.com" } });

        await _sut.UpdateAccountAsync(1, 2, new UpdateMailAccountRequest { NewPassword = "alice-new-password" }, Ct);

        await _settings.DidNotReceive().CreateSecretAsync(Arg.Any<CreateSecretRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARefusedPasswordChange_KeepsTheStoredSenderSecret()
    {
        _repo.GetAccountAsync(1, Arg.Any<CancellationToken>()).Returns(
            new MailAccount { Id = 1, Email = NoReply, QuotaMb = 100, MailDomain = new MailDomain { ServerId = 1, Name = "example.com" } });

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.UpdateAccountAsync(1, 1, new UpdateMailAccountRequest { NewPassword = "short" }, Ct));

        await _settings.DidNotReceive().CreateSecretAsync(Arg.Any<CreateSecretRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("no-reply@example.com", true)]
    [InlineData("No-Reply@Example.com", true)]
    [InlineData("noreply@example.com", false)]
    [InlineData("x-no-reply@example.com", false)]
    public void OnlyTheNoReplyLocalPart_IsTheSenderAddress(string address, bool expected) =>
        Assert.Equal(expected, MailSenderSecret.IsSenderAddress(address));
}
