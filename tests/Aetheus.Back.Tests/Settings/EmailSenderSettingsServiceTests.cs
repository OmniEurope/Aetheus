// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Settings;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Aetheus.Back.Services.Email;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>The custom SMTP server is saved with its password encrypted, never returned.</summary>
public sealed class EmailSenderSettingsServiceTests
{
    private readonly ISettingsRepository _repo = Substitute.For<ISettingsRepository>();
    private readonly IEmailSenderConfigurationResolver _resolver = Substitute.For<IEmailSenderConfigurationResolver>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly List<AppSetting> _stored = [];
    private readonly List<Secret> _secrets = [];
    private readonly EmailSenderSettingsService _sut;

    public EmailSenderSettingsServiceTests()
    {
        _encryption.EncryptValue(Arg.Any<string>()).Returns(call => "enc:" + call.Arg<string>());
        _repo.GetAllSettingsAsync(Arg.Any<CancellationToken>()).Returns(_ => _stored.ToList());
        _repo.FindSettingByKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _stored.FirstOrDefault(setting => setting.Key == call.Arg<string>()));
        _repo.AddSettingAsync(Arg.Do<AppSetting>(_stored.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repo.FindSecretByKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _secrets.FirstOrDefault(secret => secret.Key == call.Arg<string>()));
        _repo.AddSecretAsync(Arg.Do<Secret>(_secrets.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _resolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(new EmailSenderResolution
        {
            Source = EmailSenderSource.ManagedNoReply,
            Configuration = new EmailSenderConfiguration("mail.example.org", 587, SmtpTlsMode.StartTls, "no-reply@example.org", "pw", "no-reply@example.org"),
            NoReplyAddress = "no-reply@example.org",
            NoReplyServerId = 3
        });
        _sut = new EmailSenderSettingsService(_repo, _resolver, _encryption, Substitute.For<IAuditService>(), TimeProvider.System);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Get_WithNothingSaved_ShowsTheDefaultNoReplySender()
    {
        var dto = await _sut.GetAsync(Ct);

        Assert.False(dto.UseCustom);
        Assert.Equal(587, dto.Port);
        Assert.False(dto.HasPassword);
        Assert.Equal(EmailSenderSource.ManagedNoReply, dto.EffectiveSource);
        Assert.Equal("no-reply@example.org", dto.EffectiveFromAddress);
        Assert.Equal("mail.example.org", dto.EffectiveHost);
        Assert.True(dto.CanPrepareNoReply);
    }

    [Fact]
    public async Task Update_StoresTheServer_AndThePasswordOnlyEncrypted()
    {
        var dto = await _sut.UpdateAsync(new UpdateEmailSenderSettingsRequest
        {
            UseCustom = true,
            Host = " smtp.provider.net ",
            Port = 465,
            TlsMode = SmtpTlsMode.SslOnConnect,
            Username = "aetheus",
            Password = "smtp-secret",
            FromAddress = "alerts@provider.net"
        }, Ct);

        Assert.Equal("Custom", _stored.Single(s => s.Key == EmailSenderSettingKeys.Mode).Value);
        Assert.Equal("smtp.provider.net", _stored.Single(s => s.Key == EmailSenderSettingKeys.Host).Value);
        Assert.Equal("465", _stored.Single(s => s.Key == EmailSenderSettingKeys.Port).Value);
        Assert.Equal("SslOnConnect", _stored.Single(s => s.Key == EmailSenderSettingKeys.TlsMode).Value);
        Assert.DoesNotContain(_stored, setting => setting.Value.Contains("smtp-secret", StringComparison.Ordinal));
        Assert.Equal("enc:smtp-secret", _secrets.Single(s => s.Key == EmailSenderSettingKeys.SmtpPasswordSecret).EncryptedValue);
        Assert.True(dto.HasPassword);
        Assert.True(dto.UseCustom);
    }

    [Fact]
    public async Task Update_WithoutPassword_KeepsTheStoredOne()
    {
        _secrets.Add(new Secret { Key = EmailSenderSettingKeys.SmtpPasswordSecret, EncryptedValue = "enc:old" });

        await _sut.UpdateAsync(new UpdateEmailSenderSettingsRequest
        {
            UseCustom = true,
            Host = "smtp.provider.net",
            Username = "aetheus",
            FromAddress = "alerts@provider.net"
        }, Ct);

        Assert.Equal("enc:old", _secrets.Single().EncryptedValue);
        _encryption.DidNotReceive().EncryptValue(Arg.Any<string>());
    }

    [Theory]
    [InlineData("", "alerts@provider.net")]
    [InlineData("smtp.provider.net", "not an address")]
    public async Task Update_CustomWithoutHostOrValidSender_IsRefused(string host, string from)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.UpdateAsync(new UpdateEmailSenderSettingsRequest
        {
            UseCustom = true,
            Host = host,
            FromAddress = from
        }, Ct));

        Assert.Empty(_stored);
    }

    [Fact]
    public async Task Update_BackToTheDefault_NeedsNoServer()
    {
        await _sut.UpdateAsync(new UpdateEmailSenderSettingsRequest { UseCustom = false }, Ct);

        Assert.Equal("Default", _stored.Single(s => s.Key == EmailSenderSettingKeys.Mode).Value);
    }
}
