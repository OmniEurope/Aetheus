// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text.Json;
using Aetheus.Back.Components.Vaults;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.AppMonitoring;

public sealed class AppWebAnalyticsConfigurationService(
    IAppMonitoringRepository apps,
    IVaultService vaults,
    IAuditService audit,
    IMemoryCache cache,
    TimeProvider timeProvider) : IAppWebAnalyticsConfigurationService
{
    internal const string SecretKey = "AETHEUS_WEB_ANALYTICS_PSEUDONYMIZATION_KEY";
    internal const int MaintenanceBatchSize = 50;
    internal const int MaximumMaintenanceAppsPerSweep = 100;
    internal const int MaximumPurgedVersionsPerSecret = 100;
    private static readonly TimeSpan PublicKeyCacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaintenanceTimeBudget = TimeSpan.FromSeconds(15);
    private const string MaintenanceCursorCacheKey = "web-analytics-key-maintenance-cursor";

    public async Task<AppWebAnalyticsConfigurationDto?> GetAsync(
        int appId,
        CancellationToken ct = default)
    {
        var app = await apps.GetAppAsync(appId, ct).ConfigureAwait(false);
        return app is null
            ? null
            : Map(app, ReadOrigins(app.AnalyticsAllowedOriginsJson));
    }

    public async Task<AppWebAnalyticsConfigurationDto?> ConfigureAsync(
        int appId,
        ConfigureAppWebAnalyticsRequest request,
        CancellationToken ct = default)
    {
        var app = await apps.GetAppForUpdateAsync(appId, ct).ConfigureAwait(false);
        if (app is null)
            return null;
        InvalidatePublicKey(app.Id);

        var existingSite = await apps.GetAppByAnalyticsSiteIdAsync(request.SiteId, ct).ConfigureAwait(false);
        if (existingSite is not null && existingSite.Id != appId)
            throw new InvalidOperationException("This analytics site id is already assigned.");

        var origins = NormalizeOrigins(request.AllowedOrigins);
        ProvisionedKey? provisioned = null;
        try
        {
            if (app.AnalyticsVaultName is null)
            {
                var projectName = (await apps.GetAppAsync(appId, ct).ConfigureAwait(false))?.Project?.Name ?? string.Empty;
                var vaultName = VaultNameFor(projectName, app.ProjectId);
                provisioned = await ProvisionKeyAsync(app.Id, app.ProjectId, vaultName, ct).ConfigureAwait(false);
                app.AnalyticsVaultName = vaultName;
                app.AnalyticsPseudonymKeyVersion = 1;
                app.AnalyticsPseudonymKeyCreatedAt = timeProvider.GetUtcNow().UtcDateTime;
            }

            app.AnalyticsSiteId = request.SiteId;
            app.AnalyticsAllowedOriginsJson = JsonSerializer.Serialize(origins);
            app.AnalyticsEnabled = request.Enabled;
            app.AnalyticsPublicIngestEnabled = request.PublicIngestEnabled;
            app.AnalyticsStorageBudgetBytes = request.StorageBudgetBytes;
            await apps.SaveChangesAsync(ct).ConfigureAwait(false);
            provisioned = null;
        }
        catch
        {
            // Undo only what this call created: the project vault may already hold other apps' keys.
            if (provisioned is { CreatedVault: true } created)
                await vaults.DeleteVaultAsync(created.VaultId, CancellationToken.None).ConfigureAwait(false);
            else if (provisioned is { SecretId: { } secretId } added)
                await vaults.DeleteSecretAsync(added.VaultId, secretId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        InvalidatePublicKey(app.Id);
        await audit.LogAsync(
            "ConfiguredWebAnalytics",
            "MonitoredApp",
            app.Id,
            $"Site {app.AnalyticsSiteId}; public ingest {app.AnalyticsPublicIngestEnabled}; key version {app.AnalyticsPseudonymKeyVersion}",
            ct).ConfigureAwait(false);
        return Map(app, origins);
    }

    public async Task<AppWebAnalyticsConfigurationDto?> RotateKeyAsync(
        int appId,
        CancellationToken ct = default)
    {
        var app = await apps.GetAppForUpdateAsync(appId, ct).ConfigureAwait(false);
        if (app?.AnalyticsVaultName is null)
            return null;
        InvalidatePublicKey(app.Id);

        var vaultPage = await vaults.GetVaultsAsync(
            app.ProjectId,
            request: new PaginationRequest { Search = app.AnalyticsVaultName, PageSize = 20 },
            ct: ct).ConfigureAwait(false);
        var vault = vaultPage.Items.SingleOrDefault(item =>
            string.Equals(item.Name, app.AnalyticsVaultName, StringComparison.Ordinal));
        if (vault is null)
            throw new InvalidOperationException("The analytics key vault no longer exists.");
        var detail = await vaults.GetVaultDetailAsync(vault.Id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The analytics key vault no longer exists.");
        var secret = FindKey(detail.Secrets, app.Id)
            ?? throw new InvalidOperationException("The analytics pseudonymization key no longer exists.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (app.AnalyticsPendingPseudonymKeyVersion is null)
        {
            app.AnalyticsPendingPseudonymKeyVersion = app.AnalyticsPseudonymKeyVersion + 1;
            app.AnalyticsKeyRotationPendingAt = now;
            await apps.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await vaults.RotateSecretAsync(vault.Id, secret.Id, new RotateVaultSecretRequest
        {
            Value = GenerateKey()
        }, ct).ConfigureAwait(false);
        app.AnalyticsPseudonymKeyVersion = app.AnalyticsPendingPseudonymKeyVersion.Value;
        app.AnalyticsPseudonymKeyCreatedAt = now;
        app.AnalyticsPendingPseudonymKeyVersion = null;
        app.AnalyticsKeyRotationPendingAt = null;
        await apps.SaveChangesAsync(ct).ConfigureAwait(false);
        InvalidatePublicKey(app.Id);
        await audit.LogAsync(
            "RotatedWebAnalyticsKey",
            "MonitoredApp",
            app.Id,
            $"Version {app.AnalyticsPseudonymKeyVersion}",
            ct).ConfigureAwait(false);
        return Map(app, ReadOrigins(app.AnalyticsAllowedOriginsJson));
    }

    public async Task<PublicAnalyticsContext?> ResolvePublicContextAsync(
        string siteId,
        string origin,
        CancellationToken ct = default)
    {
        var app = await apps.GetAppByAnalyticsSiteIdAsync(siteId, ct).ConfigureAwait(false);
        if (app?.AnalyticsVaultName is null
            || app.AnalyticsPendingPseudonymKeyVersion is not null
            || !app.Enabled
            || !app.AnalyticsEnabled
            || !app.AnalyticsPublicIngestEnabled)
            return null;
        var allowedOrigins = ReadOrigins(app.AnalyticsAllowedOriginsJson);
        if (!allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            return null;
        var cacheKey = PublicKeyCacheKey(app.Id);
        if (!cache.TryGetValue<CachedPublicKey>(cacheKey, out var cached)
            || cached is null
            || cached.KeyVersion != app.AnalyticsPseudonymKeyVersion)
        {
            var secrets = await vaults.ResolveVaultSecretsAsync(
                [app.AnalyticsVaultName],
                app.ProjectId,
                ct).ConfigureAwait(false);
            if (ReadKey(secrets, app.Id) is not { } resolvedKey)
                return null;
            cached = new CachedPublicKey(app.AnalyticsPseudonymKeyVersion, resolvedKey);
            cache.Set(cacheKey, cached, PublicKeyCacheLifetime);
        }
        return cached is { } publicKey
            ? new PublicAnalyticsContext(app, publicKey.Value)
            : null;
    }

    public async Task<(int Rotated, int PurgedVersions)> MaintainKeysAsync(
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        var rotated = 0;
        var purged = 0;
        var startedAt = timeProvider.GetTimestamp();
        var rotationCandidates = await apps.GetAnalyticsKeyRotationCandidatesAsync(
            nowUtc.AddMonths(-12),
            MaintenanceBatchSize,
            ct).ConfigureAwait(false);
        foreach (var configuredApp in rotationCandidates)
        {
            if (timeProvider.GetElapsedTime(startedAt) >= MaintenanceTimeBudget)
                break;
            if (await RotateKeyAsync(configuredApp.Id, ct).ConfigureAwait(false) is not null)
                rotated++;
        }

        var afterId = cache.Get<int>(MaintenanceCursorCacheKey);
        var visited = 0;
        var timeBudgetReached = false;
        while (visited < MaximumMaintenanceAppsPerSweep
               && timeProvider.GetElapsedTime(startedAt) < MaintenanceTimeBudget)
        {
            var pageSize = Math.Min(
                MaintenanceBatchSize,
                MaximumMaintenanceAppsPerSweep - visited);
            var configuredApps = await apps.GetAnalyticsConfiguredAppsPageAsync(
                afterId,
                pageSize,
                ct).ConfigureAwait(false);
            if (configuredApps.Count == 0)
            {
                cache.Remove(MaintenanceCursorCacheKey);
                break;
            }

            foreach (var configuredApp in configuredApps)
            {
                if (timeProvider.GetElapsedTime(startedAt) >= MaintenanceTimeBudget)
                {
                    timeBudgetReached = true;
                    break;
                }
                afterId = configuredApp.Id;
                visited++;
                cache.Set(MaintenanceCursorCacheKey, afterId);
                purged += await PurgeKeyHistoryAsync(configuredApp, nowUtc, ct).ConfigureAwait(false);
            }
            if (!timeBudgetReached && configuredApps.Count < pageSize)
            {
                cache.Remove(MaintenanceCursorCacheKey);
                break;
            }
            if (timeBudgetReached)
                break;
        }
        return (rotated, purged);
    }

    /// <summary>Per-project analytics vault slugs must match the migration's canonical lowercase,
    /// hyphenated and length-bounded calculation.</summary>
    internal static string VaultNameFor(string projectName, int projectId) =>
        ProjectResourceNames.Slug(projectName, projectId) + ".analytics";

    /// <summary>The key of one app inside its project's vault.</summary>
    internal static string SecretKeyFor(int appId) => $"{SecretKey}_{appId}";

    /// <summary>Reads the application-specific key with an unsuffixed single-application-vault
    /// fallback for compatibility.</summary>
    internal static string? ReadKey(IReadOnlyDictionary<string, string> secrets, int appId) =>
        secrets.TryGetValue(SecretKeyFor(appId), out var own) ? own
        : secrets.TryGetValue(SecretKey, out var legacy) ? legacy
        : null;

    private static VaultSecretDto? FindKey(IEnumerable<VaultSecretDto> secrets, int appId)
    {
        var list = secrets as IReadOnlyCollection<VaultSecretDto> ?? secrets.ToList();
        return list.SingleOrDefault(item => string.Equals(item.Key, SecretKeyFor(appId), StringComparison.Ordinal))
            ?? list.SingleOrDefault(item => string.Equals(item.Key, SecretKey, StringComparison.Ordinal));
    }

    /// <summary>Creates the project vault when it does not exist yet, then this app's key in it,
    /// and says what it created so a failed configuration can undo exactly that.</summary>
    private async Task<ProvisionedKey> ProvisionKeyAsync(int appId, int projectId, string vaultName, CancellationToken ct)
    {
        var page = await vaults.GetVaultsAsync(
            projectId,
            request: new PaginationRequest { Search = vaultName, PageSize = 20 },
            ct: ct).ConfigureAwait(false);
        var existing = page.Items.SingleOrDefault(item => string.Equals(item.Name, vaultName, StringComparison.Ordinal));
        var createdVault = existing is null;
        var vaultId = existing?.Id ?? (await vaults.CreateVaultAsync(new CreateVaultRequest
        {
            Name = vaultName,
            Description = "Aetheus-managed pseudonymization keys for no-banner web analytics, one per app.",
            ProjectId = projectId
        }, ct).ConfigureAwait(false)).Id;

        if (!createdVault)
        {
            var detail = await vaults.GetVaultDetailAsync(vaultId, ct).ConfigureAwait(false);
            if (detail?.Secrets.Any(item => string.Equals(item.Key, SecretKeyFor(appId), StringComparison.Ordinal)) == true)
                return new ProvisionedKey(vaultId, CreatedVault: false, SecretId: null);
        }

        try
        {
            var secret = await vaults.CreateSecretAsync(vaultId, new CreateVaultSecretRequest
            {
                Key = SecretKeyFor(appId),
                Value = GenerateKey()
            }, ct).ConfigureAwait(false);
            return new ProvisionedKey(vaultId, createdVault, secret.Id);
        }
        catch when (createdVault)
        {
            await vaults.DeleteVaultAsync(vaultId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private sealed record ProvisionedKey(int VaultId, bool CreatedVault, int? SecretId);

    /// <summary>Drops the key versions older than 31 days for one app; 0 when its vault or key is gone.</summary>
    private async Task<int> PurgeKeyHistoryAsync(
        Aetheus.Back.Data.Entities.MonitoredApp configuredApp, DateTime nowUtc, CancellationToken ct)
    {
        if (configuredApp.AnalyticsVaultName is null) return 0;
        var vaultPage = await vaults.GetVaultsAsync(
            configuredApp.ProjectId,
            request: new PaginationRequest { Search = configuredApp.AnalyticsVaultName, PageSize = 20 },
            ct: ct).ConfigureAwait(false);
        var vault = vaultPage.Items.SingleOrDefault(item =>
            string.Equals(item.Name, configuredApp.AnalyticsVaultName, StringComparison.Ordinal));
        if (vault is null) return 0;
        var detail = await vaults.GetVaultDetailAsync(vault.Id, ct).ConfigureAwait(false);
        var secret = detail is null ? null : FindKey(detail.Secrets, configuredApp.Id);
        return secret is null
            ? 0
            : await vaults.PurgeHistoricalSecretVersionsAsync(
                vault.Id, secret.Id, nowUtc.AddDays(-31), MaximumPurgedVersionsPerSecret, ct).ConfigureAwait(false);
    }

    private static string PublicKeyCacheKey(int appId) => $"web-analytics-public-key:{appId}";

    private void InvalidatePublicKey(int appId) => cache.Remove(PublicKeyCacheKey(appId));

    private sealed record CachedPublicKey(int KeyVersion, string Value);

    private static List<string> NormalizeOrigins(IEnumerable<string> values) =>
        values.Select(value =>
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps
                    || uri.AbsolutePath != "/"
                    || !string.IsNullOrEmpty(uri.Query)
                    || !string.IsNullOrEmpty(uri.Fragment))
                    throw new InvalidOperationException("Analytics origins must be HTTPS origins without a path.");
                return uri.GetLeftPart(UriPartial.Authority);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> ReadOrigins(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static string GenerateKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static AppWebAnalyticsConfigurationDto Map(
        Aetheus.Back.Data.Entities.MonitoredApp app,
        IReadOnlyList<string> origins) => new()
        {
            PublicIngestEnabled = app.AnalyticsPublicIngestEnabled,
            Enabled = app.AnalyticsEnabled,
            SiteId = app.AnalyticsSiteId ?? string.Empty,
            AllowedOrigins = origins,
            StorageBudgetBytes = app.AnalyticsStorageBudgetBytes,
            PseudonymKeyVersion = app.AnalyticsPseudonymKeyVersion,
            PseudonymKeyCreatedAt = app.AnalyticsPseudonymKeyCreatedAt
        };
}
