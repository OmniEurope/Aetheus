// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Settings;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class SettingsServiceTests
{
    private readonly ISettingsRepository _repoMock = Substitute.For<ISettingsRepository>();
    private readonly IEncryptionService _encryptionMock = Substitute.For<IEncryptionService>();
    private readonly SettingsService _sut;

    public SettingsServiceTests()
    {
        _encryptionMock.EncryptValue(Arg.Any<string>()).Returns(args => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes((string)args[0])));
        _encryptionMock.DecryptValue(Arg.Any<string>()).Returns(args => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String((string)args[0])));
        _sut = new SettingsService(_repoMock, _encryptionMock, Substitute.For<IAuditService>(), TimeProvider.System);
    }

    [Fact]
    public async Task GetSettingsAsync_HidesTheEmailSenderRows_WhichHaveTheirOwnTab()
    {
        _repoMock.GetAllSettingsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new AppSetting { Key = "Theme", Value = "dark" },
            new AppSetting { Key = Aetheus.Back.Services.Email.EmailSenderSettingKeys.Host, Value = "smtp.x" }
        ]);

        var result = await _sut.GetSettingsAsync(ct: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result, setting => setting.Key.StartsWith("EmailSender:", StringComparison.Ordinal));
        Assert.Contains(result, setting => setting.Key == "Theme");
    }

    [Fact]
    public async Task GetSettingsAsync_ListsOnlyTheKeysASaveAccepts()
    {
        // The General tab showed internal markers and seed rows no code reads, each with a Save the
        // allow-list refused ("Setting key 'MetricsRetentionDays' is not allowed").
        _repoMock.GetAllSettingsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new AppSetting { Key = "Theme", Value = "dark" },
            new AppSetting { Key = "MetricsRetentionDays", Value = "30" },
            new AppSetting { Key = "SiteName", Value = "Aetheus" },
            new AppSetting { Key = "DemoDataSeeded:2026-07-14", Value = "2026-07-14T11:31:17Z" },
            new AppSetting { Key = "DeliveryTemplates:VersionReset:R1-20", Value = "2026-10-12T10:33:13Z" },
            new AppSetting { Key = "DailyRecap:LastSentDate", Value = "2026-10-28" },
            new AppSetting { Key = "CertificateAlerts:LeadDays", Value = "7,3,1" }
        ]);

        var result = await _sut.GetSettingsAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(["Theme", "Retention:LogDays", PipelineRunToEndSetting.Key], result.Select(s => s.Key));
        foreach (var setting in result)
            await _sut.UpdateSettingAsync(setting.Key, setting.Value, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetSettingsAsync_ReturnsMappedDtos_PlusOperationalDefaults()
    {
        var settings = new List<AppSetting>
        {
            new() { Key = "Theme", Value = "dark" },
            new() { Key = "Locale", Value = "fr" }
        };
        _repoMock.GetAllSettingsAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var result = await _sut.GetSettingsAsync(ct: TestContext.Current.CancellationToken);

        // L9QM: the persisted settings are mapped in order, then the operational default
        // Retention:LogDays is appended when not already present (so the admin Settings page always
        // exposes the log-retention control even before it has been saved).
        Assert.Equal("Theme", result[0].Key);
        Assert.Equal("dark", result[0].Value);
        Assert.Equal("Locale", result[1].Key);
        Assert.Contains(result, s => s.Key == "Retention:LogDays" && s.Value == "30");
        // "Run to end" is shown off until an administrator turns it on.
        Assert.Contains(result, s => s.Key == PipelineRunToEndSetting.Key && s.Value == "false");
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public async Task GetSettingsAsync_Empty_ReturnsOperationalDefaults()
    {
        _repoMock.GetAllSettingsAsync(Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.GetSettingsAsync(ct: TestContext.Current.CancellationToken);

        // L9QM: even with no persisted settings, the operational defaults surface so they are editable.
        Assert.Equal(2, result.Count);
        Assert.Equal("Retention:LogDays", result[0].Key);
        Assert.Equal("30", result[0].Value);
        Assert.Equal(PipelineRunToEndSetting.Key, result[1].Key);
        Assert.Equal("false", result[1].Value);
    }

    [Fact]
    public async Task UpdateSettingAsync_NewSetting_AddsToRepo()
    {
        _repoMock.FindSettingByKeyAsync("Locale", Arg.Any<CancellationToken>()).Returns((AppSetting?)null);

        await _sut.UpdateSettingAsync("Locale", "newValue", ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddSettingAsync(Arg.Is<AppSetting>(s => s.Key == "Locale" && s.Value == "newValue"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateSettingAsync_ExistingSetting_UpdatesValue()
    {
        var existing = new AppSetting { Key = "Theme", Value = "light" };
        _repoMock.FindSettingByKeyAsync("Theme", Arg.Any<CancellationToken>()).Returns(existing);

        await _sut.UpdateSettingAsync("Theme", "dark", ct: TestContext.Current.CancellationToken);

        Assert.Equal("dark", existing.Value);
        await _repoMock.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSecretsAsync_ReturnsMappedDtos()
    {
        var now = DateTime.UtcNow;
        var secrets = new List<Secret>
        {
            new() { Id = 1, Key = "API_KEY", CreatedAt = now, UpdatedAt = now }
        };
        _repoMock.GetAllSecretsAsync(Arg.Any<CancellationToken>()).Returns(secrets);

        var result = await _sut.GetSecretsAsync(ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
        Assert.Equal("API_KEY", result[0].Key);
        Assert.Equal(now, result[0].CreatedAt);
    }

    [Fact]
    public async Task CreateSecretAsync_NewSecret_CreatesAndReturnsDto()
    {
        _repoMock.FindSecretByKeyAsync("NEW_KEY", Arg.Any<CancellationToken>()).Returns((Secret?)null);

        var request = new CreateSecretRequest { Key = "NEW_KEY", Value = "secret_value" };
        var result = await _sut.CreateSecretAsync(request, ct: TestContext.Current.CancellationToken);

        Assert.Equal("NEW_KEY", result.Key);
        await _repoMock.Received(1).AddSecretAsync(Arg.Is<Secret>(s => s.Key == "NEW_KEY" && !string.IsNullOrEmpty(s.EncryptedValue)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateSecretAsync_ExistingSecret_UpdatesAndReturnsDto()
    {
        var existing = new Secret { Id = 5, Key = "EXISTING", EncryptedValue = "old", CreatedAt = DateTime.UtcNow.AddDays(-1) };
        _repoMock.FindSecretByKeyAsync("EXISTING", Arg.Any<CancellationToken>()).Returns(existing);

        var request = new CreateSecretRequest { Key = "EXISTING", Value = "new_value" };
        var result = await _sut.CreateSecretAsync(request, ct: TestContext.Current.CancellationToken);

        Assert.Equal(5, result.Id);
        Assert.Equal("EXISTING", result.Key);
        Assert.NotEqual("old", existing.EncryptedValue);
        await _repoMock.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteSecretAsync_Found_ReturnsTrue()
    {
        var secret = new Secret { Id = 1, Key = "X" };
        _repoMock.FindSecretByIdAsync(1, Arg.Any<CancellationToken>()).Returns(secret);

        var result = await _sut.DeleteSecretAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _repoMock.Received(1).RemoveSecretAsync(secret, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteSecretAsync_NotFound_ReturnsFalse()
    {
        _repoMock.FindSecretByIdAsync(99, Arg.Any<CancellationToken>()).Returns((Secret?)null);

        var result = await _sut.DeleteSecretAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task GetSettingValueAsync_Found_ReturnsValue()
    {
        _repoMock.FindSettingByKeyAsync("key1", Arg.Any<CancellationToken>())
            .Returns(new AppSetting { Key = "key1", Value = "val1" });

        var result = await _sut.GetSettingValueAsync("key1", ct: TestContext.Current.CancellationToken);

        Assert.Equal("val1", result);
    }

    [Fact]
    public async Task GetSettingValueAsync_NotFound_ReturnsNull()
    {
        _repoMock.FindSettingByKeyAsync("missing", Arg.Any<CancellationToken>()).Returns((AppSetting?)null);

        var result = await _sut.GetSettingValueAsync("missing", ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateSecretAsync_EncryptsValue_ProducesBase64()
    {
        _repoMock.FindSecretByKeyAsync("K", Arg.Any<CancellationToken>()).Returns((Secret?)null);

        var request = new CreateSecretRequest { Key = "K", Value = "plaintext" };
        await _sut.CreateSecretAsync(request, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddSecretAsync(
            Arg.Is<Secret>(s => IsValidBase64(s.EncryptedValue)),
            Arg.Any<CancellationToken>());
    }

    private static bool IsValidBase64(string value)
    {
        try { Convert.FromBase64String(value); return true; }
        catch { return false; }
    }
}
