// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.Email;

namespace Aetheus.Back.Components.Settings;

/// <summary> Saves the custom SMTP server in the platform settings (its password in the secret
/// store, encrypted) and reports the sender really in use, default no-reply account included.
/// </summary>
public sealed class EmailSenderSettingsService(
    ISettingsRepository repo,
    IEmailSenderConfigurationResolver resolver,
    IEncryptionService encryption,
    IAuditService audit,
    TimeProvider timeProvider) : IEmailSenderSettingsService
{
    public async Task<EmailSenderSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var settings = (await repo.GetAllSettingsAsync(ct).ConfigureAwait(false))
            .Where(setting => setting.Key.StartsWith(EmailSenderSettingKeys.Prefix, StringComparison.Ordinal))
            .ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.Ordinal);
        var hasPassword = await repo.FindSecretByKeyAsync(EmailSenderSettingKeys.SmtpPasswordSecret, ct).ConfigureAwait(false) is not null;
        var resolution = await resolver.ResolveAsync(ct).ConfigureAwait(false);

        return new EmailSenderSettingsDto
        {
            UseCustom = string.Equals(Read(settings, EmailSenderSettingKeys.Mode), EmailSenderSettingKeys.CustomMode, StringComparison.OrdinalIgnoreCase),
            Host = Read(settings, EmailSenderSettingKeys.Host),
            Port = int.TryParse(Read(settings, EmailSenderSettingKeys.Port), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                ? port : EmailSenderSettingKeys.ManagedSubmissionPort,
            TlsMode = Enum.TryParse<SmtpTlsMode>(Read(settings, EmailSenderSettingKeys.TlsMode), ignoreCase: true, out var mode)
                && Enum.IsDefined(mode) ? mode : SmtpTlsMode.StartTls,
            Username = Read(settings, EmailSenderSettingKeys.Username),
            HasPassword = hasPassword,
            FromAddress = Read(settings, EmailSenderSettingKeys.FromAddress),
            EffectiveSource = resolution.Source,
            EffectiveFromAddress = resolution.Configuration?.FromAddress,
            EffectiveHost = resolution.Configuration?.Host,
            EffectivePort = resolution.Configuration?.Port,
            Problem = resolution.Problem,
            ProblemDetail = resolution.ProblemDetail,
            NoReplyAddress = resolution.NoReplyAddress,
            CanPrepareNoReply = resolution.NoReplyServerId is not null
        };
    }

    public async Task<EmailSenderSettingsDto> UpdateAsync(UpdateEmailSenderSettingsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var host = request.Host.Trim();
        var from = request.FromAddress.Trim();
        var username = request.Username.Trim();
        if (request.UseCustom)
        {
            if (host.Length == 0) throw new BadRequestException("The SMTP host is required for a custom server.");
            if (!System.Net.Mail.MailAddress.TryCreate(from, out var parsed) || parsed.Address != from)
                throw new BadRequestException("The sender address is not a valid e-mail address.");
        }

        await UpsertAsync(EmailSenderSettingKeys.Mode, request.UseCustom ? EmailSenderSettingKeys.CustomMode : EmailSenderSettingKeys.DefaultMode, ct).ConfigureAwait(false);
        await UpsertAsync(EmailSenderSettingKeys.Host, host, ct).ConfigureAwait(false);
        await UpsertAsync(EmailSenderSettingKeys.Port, request.Port.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await UpsertAsync(EmailSenderSettingKeys.TlsMode, request.TlsMode.ToString(), ct).ConfigureAwait(false);
        await UpsertAsync(EmailSenderSettingKeys.Username, username, ct).ConfigureAwait(false);
        await UpsertAsync(EmailSenderSettingKeys.FromAddress, from, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(request.Password))
            await UpsertSecretAsync(EmailSenderSettingKeys.SmtpPasswordSecret, request.Password, ct).ConfigureAwait(false);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.LogAsync("Updated", "Setting", null, "EmailSender", ct).ConfigureAwait(false);
        return await GetAsync(ct).ConfigureAwait(false);
    }

    private async Task UpsertAsync(string key, string value, CancellationToken ct)
    {
        var setting = await repo.FindSettingByKeyAsync(key, ct).ConfigureAwait(false);
        if (setting is null)
        {
            await repo.AddSettingAsync(new AppSetting { Key = key, Value = value }, ct).ConfigureAwait(false);
            return;
        }
        setting.Value = value;
        setting.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
    }

    private async Task UpsertSecretAsync(string key, string value, CancellationToken ct)
    {
        var secret = await repo.FindSecretByKeyAsync(key, ct).ConfigureAwait(false);
        if (secret is null)
        {
            await repo.AddSecretAsync(new Secret { Key = key, EncryptedValue = encryption.EncryptValue(value) }, ct).ConfigureAwait(false);
            return;
        }
        secret.EncryptedValue = encryption.EncryptValue(value);
        secret.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
    }

    private static string Read(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) ? value.Trim() : string.Empty;
}
