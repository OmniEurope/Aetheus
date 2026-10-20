// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.Email;

namespace Aetheus.Back.Components.Settings;

public class SettingsService(ISettingsRepository repo, IEncryptionService encryption, IAuditService audit, TimeProvider timeProvider) : ISettingsService
{
    // Hardening (#18): only an explicit allow-list of keys may be persisted via the
    // public Settings API. Prevents abuse where an admin could write arbitrary keys
    // (e.g. "Auth:AdminPassword") into the same store and have them shadow config.
    private static readonly HashSet<string> AllowedSettingKeys = new(StringComparer.Ordinal)
    {
        "Theme",
        "Locale",
        "DefaultPageSize",
        "Branding:LogoUrl",
        "Branding:Name",
        "Notifications:EmailEnabled",
        "Notifications:WebhookEnabled",
        "Pipelines:DefaultRetentionDays",
        "Metrics:RetentionDays",
        "Retention:LogDays",
        PipelineRunToEndSetting.Key
    };

    // L9QM: operational settings that should always be visible/editable in the admin Settings page
    // with a sensible default, even before being explicitly saved - so the key that governs the
    // system-log purge depth is exposed in the UI everywhere, not only once it happens to be
    // persisted. Saving a defaulted row upserts it through UpdateSettingAsync (allow-list-checked);
    // the SystemLogs purge reads it via GetSettingValueAsync (falling back to the same default).
    private static readonly Dictionary<string, string> DefaultSettingValues = new(StringComparer.Ordinal)
    {
        ["Retention:LogDays"] = "30",
        // Off until an administrator turns it on: "run to end" lets a deployment run after failed tests.
        [PipelineRunToEndSetting.Key] = "false"
    };

    internal const string DailyRecapRowPrefix = "DailyRecap:";

    public async Task<List<AppSettingDto>> GetSettingsAsync(CancellationToken ct = default)
    {
        var settings = await repo.GetAllSettingsAsync(ct).ConfigureAwait(false);
        // The e-mail sender has its own tab and endpoint; its rows are not free-form settings. The
        // daily recap neither (DailyRecapSettingKeys.Prefix, spelled here so Settings does not
        // depend on that module).
        var result = settings
            .Where(s => !s.Key.StartsWith(EmailSenderSettingKeys.Prefix, StringComparison.Ordinal)
                && !s.Key.StartsWith(DailyRecapRowPrefix, StringComparison.Ordinal))
            .Select(s => new AppSettingDto { Key = s.Key, Value = s.Value }).ToList();
        var present = result.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, def) in DefaultSettingValues)
        {
            if (!present.Contains(key))
                result.Add(new AppSettingDto { Key = key, Value = def });
        }
        return result;
    }

    public async Task UpdateSettingAsync(string key, string value, CancellationToken ct = default)
    {
        if (!AllowedSettingKeys.Contains(key))
            throw new BadRequestException($"Setting key '{key}' is not allowed.");
        // A switch, not free text: a typo must not leave an administrator believing it is off or on.
        if (key == PipelineRunToEndSetting.Key && !bool.TryParse(value, out _))
            throw new BadRequestException($"Setting '{key}' must be 'true' or 'false'.");

        var setting = await repo.FindSettingByKeyAsync(key, ct).ConfigureAwait(false);
        if (setting is null)
        {
            await repo.AddSettingAsync(new AppSetting { Key = key, Value = value }, ct).ConfigureAwait(false);
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        await audit.LogAsync("Updated", "Setting", null, key, ct).ConfigureAwait(false);
    }

    public async Task<List<SecretDto>> GetSecretsAsync(CancellationToken ct = default)
    {
        var secrets = await repo.GetAllSecretsAsync(ct).ConfigureAwait(false);
        return secrets.Select(s => new SecretDto
        {
            Id = s.Id,
            Key = s.Key,
            CreatedAt = s.CreatedAt,
            UpdatedAt = s.UpdatedAt
        }).ToList();
    }

    public async Task<SecretDto> CreateSecretAsync(CreateSecretRequest request, CancellationToken ct = default)
    {
        var existing = await repo.FindSecretByKeyAsync(request.Key, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            existing.EncryptedValue = encryption.EncryptValue(request.Value);
            existing.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
            // Audit the upsert too - overwriting a secret value must leave an audit trail, not just the
            // initial creation path below.
            await audit.LogAsync("Updated", "Secret", existing.Id, existing.Key, ct).ConfigureAwait(false);
            return new SecretDto
            {
                Id = existing.Id,
                Key = existing.Key,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = existing.UpdatedAt
            };
        }

        var secret = new Secret
        {
            Key = request.Key,
            EncryptedValue = encryption.EncryptValue(request.Value)
        };
        await repo.AddSecretAsync(secret, ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "Secret", secret.Id, secret.Key, ct).ConfigureAwait(false);

        return new SecretDto
        {
            Id = secret.Id,
            Key = secret.Key,
            CreatedAt = secret.CreatedAt,
            UpdatedAt = secret.UpdatedAt
        };
    }

    public async Task<bool> DeleteSecretAsync(int id, CancellationToken ct = default)
    {
        var secret = await repo.FindSecretByIdAsync(id, ct).ConfigureAwait(false);
        if (secret is null) return false;
        var name = secret.Key;
        await repo.RemoveSecretAsync(secret, ct).ConfigureAwait(false);
        await audit.LogAsync("Deleted", "Secret", id, name, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default)
    {
        var setting = await repo.FindSettingByKeyAsync(key, ct).ConfigureAwait(false);
        return setting?.Value;
    }


}
