// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Alerts;

public sealed class SecretExpirationService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<SecretExpirationService> logger,
    TimeProvider timeProvider,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    // Only the live colour runs it (decision of 2026-10-02, PostgresLeaderLease).
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease is null
            ? RunLeaderLoopAsync(stoppingToken)
            : leaderLease.RunAsLeaderAsync("aetheus:secret-expiration", RunLeaderLoopAsync, stoppingToken);

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        // Hardening (#51): scan once at startup so a service restart does not extend the
        // detection window by up to 6 h. After that, the periodic timer takes over.
        await ScanOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ScanOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ProcessExpiringSecretsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during secret expiration check");
        }
    }

    internal async Task ProcessExpiringSecretsAsync(CancellationToken ct)
    {
        var alertDays = configuration.GetValue("SecretExpiration:AlertDays", 14);

        await using var scope = scopeFactory.CreateAsyncScope();
        var vaultRepo = scope.ServiceProvider.GetRequiredService<IVaultRepository>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var threshold = timeProvider.GetUtcNow().UtcDateTime.AddDays(alertDays);
        var expiringSecrets = await vaultRepo.GetExpiringSecretsAsync(threshold, ct).ConfigureAwait(false);

        if (expiringSecrets.Count == 0) return;

        logger.LogInformation("Found {Count} secrets expiring within {Days} days", expiringSecrets.Count, alertDays);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var secret in expiringSecrets)
        {
            await notificationService.SendEventAsync("SecretExpiring", new
            {
                SecretId = secret.Id,
                SecretKey = secret.Key,
                VaultName = secret.Vault.Name,
                // The project owning the vault, directly or through its environment or project server:
                // the AI tasks and the per-user notifications of an event are those of that project. A
                // vault of a shared environment has none, and its event reaches no project.
                ProjectId = secret.Vault.ProjectId
                    ?? secret.Vault.Environment?.ProjectId
                    ?? secret.Vault.ProjectServer?.ProjectId,
                ExpiresAt = secret.ExpiresAt,
                DaysRemaining = (secret.ExpiresAt!.Value - now).Days
            }, ct).ConfigureAwait(false);
        }
    }
}
