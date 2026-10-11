// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;
using Aetheus.Back.Services.Email;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Recette R1-16: which sender the platform e-mails leave from.</summary>
public sealed class EmailSenderConfigurationResolverTests
{
    private const string NoReply = "no-reply@example.org";

    private readonly IEmailSenderSettingsRepository _repo = Substitute.For<IEmailSenderSettingsRepository>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal);
    private readonly EmailSenderConfigurationResolver _sut;

    public EmailSenderConfigurationResolverTests()
    {
        _encryption.DecryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>()["enc:".Length..]);
        _repo.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(_settings);
        _sut = new EmailSenderConfigurationResolver(_repo, _encryption);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private void GivenManagedDomain(int? mailboxId = 12, string? password = "generated-secret")
    {
        _repo.FindDefaultMailDomainAsync(Arg.Any<CancellationToken>())
            .Returns(new ManagedMailDomain(3, "example.org", "mail.example.org"));
        _repo.FindActiveMailboxIdAsync(NoReply, Arg.Any<CancellationToken>()).Returns(mailboxId);
        _repo.GetEncryptedSecretAsync(EmailSenderSettingKeys.NoReplyPasswordSecret(NoReply), Arg.Any<CancellationToken>())
            .Returns(password is null ? null : "enc:" + password);
    }

    [Fact]
    public async Task Default_IsTheNoReplyMailboxOfTheManagedDomain_OnTheSubmissionPort()
    {
        GivenManagedDomain();

        var resolution = await _sut.ResolveAsync(Ct);

        Assert.Equal(EmailSenderSource.ManagedNoReply, resolution.Source);
        Assert.Equal(EmailSenderProblem.None, resolution.Problem);
        var configuration = Assert.IsType<EmailSenderConfiguration>(resolution.Configuration);
        Assert.Equal("mail.example.org", configuration.Host);
        Assert.Equal(587, configuration.Port);
        Assert.Equal(SmtpTlsMode.StartTls, configuration.TlsMode);
        Assert.Equal(NoReply, configuration.Username);
        Assert.Equal("generated-secret", configuration.Password);
        Assert.Equal(NoReply, configuration.FromAddress);
        Assert.Equal(3, resolution.NoReplyServerId);
        Assert.Equal(12, resolution.NoReplyAccountId);
    }

    [Fact]
    public async Task Default_WithoutAnyMailDomain_SendsNothing()
    {
        var resolution = await _sut.ResolveAsync(Ct);

        Assert.Equal(EmailSenderSource.None, resolution.Source);
        Assert.Equal(EmailSenderProblem.NoMailDomain, resolution.Problem);
        Assert.Null(resolution.Configuration);
        Assert.Null(resolution.NoReplyServerId);
    }

    [Fact]
    public async Task Default_WithoutTheMailbox_ReportsItMissing_AndKeepsTheServerToCreateItOn()
    {
        GivenManagedDomain(mailboxId: null);

        var resolution = await _sut.ResolveAsync(Ct);

        Assert.Null(resolution.Configuration);
        Assert.Equal(EmailSenderProblem.NoReplyAccountMissing, resolution.Problem);
        Assert.Equal(NoReply, resolution.ProblemDetail);
        Assert.Equal(3, resolution.NoReplyServerId);
        Assert.Contains(NoReply, resolution.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_WithoutAStoredPassword_NeverGuessesOne()
    {
        GivenManagedDomain(password: null);

        var resolution = await _sut.ResolveAsync(Ct);

        Assert.Null(resolution.Configuration);
        Assert.Equal(EmailSenderProblem.NoReplyPasswordUnknown, resolution.Problem);
        Assert.Equal(12, resolution.NoReplyAccountId);
    }

    [Fact]
    public async Task Custom_ReplacesTheDefault_WithItsOwnPortTlsAndPassword()
    {
        GivenManagedDomain();
        _settings[EmailSenderSettingKeys.Mode] = "Custom";
        _settings[EmailSenderSettingKeys.Host] = "smtp.provider.net";
        _settings[EmailSenderSettingKeys.Port] = "465";
        _settings[EmailSenderSettingKeys.TlsMode] = "SslOnConnect";
        _settings[EmailSenderSettingKeys.Username] = "aetheus";
        _settings[EmailSenderSettingKeys.FromAddress] = "alerts@provider.net";
        _repo.GetEncryptedSecretAsync(EmailSenderSettingKeys.SmtpPasswordSecret, Arg.Any<CancellationToken>())
            .Returns("enc:smtp-secret");

        var resolution = await _sut.ResolveAsync(Ct);

        Assert.Equal(EmailSenderSource.Custom, resolution.Source);
        Assert.Equal(
            new EmailSenderConfiguration("smtp.provider.net", 465, SmtpTlsMode.SslOnConnect, "aetheus", "smtp-secret", "alerts@provider.net"),
            resolution.Configuration);
        // The default stays described, so the settings page can still offer to prepare it.
        Assert.Equal(NoReply, resolution.NoReplyAddress);
    }

    [Fact]
    public async Task Custom_WithoutHost_IsIncomplete_AndDoesNotFallBackSilently()
    {
        GivenManagedDomain();
        _settings[EmailSenderSettingKeys.Mode] = "Custom";
        _settings[EmailSenderSettingKeys.FromAddress] = "alerts@provider.net";

        var resolution = await _sut.ResolveAsync(Ct);

        Assert.Equal(EmailSenderSource.None, resolution.Source);
        Assert.Equal(EmailSenderProblem.CustomIncomplete, resolution.Problem);
        Assert.Null(resolution.Configuration);
    }

    [Fact]
    public async Task Custom_WithUserButNoPassword_ReportsThePasswordMissing()
    {
        _settings[EmailSenderSettingKeys.Mode] = "Custom";
        _settings[EmailSenderSettingKeys.Host] = "smtp.provider.net";
        _settings[EmailSenderSettingKeys.Username] = "aetheus";
        _settings[EmailSenderSettingKeys.FromAddress] = "alerts@provider.net";

        var resolution = await _sut.ResolveAsync(Ct);

        Assert.Equal(EmailSenderProblem.CustomPasswordMissing, resolution.Problem);
        Assert.Null(resolution.Configuration);
    }

    [Fact]
    public async Task Custom_WithoutUser_SendsUnauthenticated_AndDefaultsToStartTlsOn587()
    {
        _settings[EmailSenderSettingKeys.Mode] = "custom";
        _settings[EmailSenderSettingKeys.Host] = "relay.lan";
        _settings[EmailSenderSettingKeys.Port] = "not-a-port";
        _settings[EmailSenderSettingKeys.TlsMode] = "Bogus";
        _settings[EmailSenderSettingKeys.FromAddress] = "aetheus@lan";

        var resolution = await _sut.ResolveAsync(Ct);

        Assert.Equal(new EmailSenderConfiguration("relay.lan", 587, SmtpTlsMode.StartTls, "", "", "aetheus@lan"), resolution.Configuration);
        await _repo.DidNotReceive().GetEncryptedSecretAsync(EmailSenderSettingKeys.SmtpPasswordSecret, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Configuration_ToString_NeverShowsThePassword()
    {
        var configuration = new EmailSenderConfiguration("h", 587, SmtpTlsMode.StartTls, "u", "top-secret", "f@x");

        Assert.DoesNotContain("top-secret", configuration.ToString(), StringComparison.Ordinal);
    }
}
