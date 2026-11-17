// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.PackageRegistry;

internal sealed class PackageRegistryStorageReconciler(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<PackageRegistryStorageReconciler> logger,
    IPostgresLeaderLease leaderLease) : LeaderBackgroundService(leaderLease, "aetheus:package-registry-storage")
{
    protected override async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
    }

    internal async Task ReconcileOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IPackageRegistryRepository>();
            var storage = scope.ServiceProvider.GetRequiredService<IPackageRegistryStorage>();
            var referenced = await repository.GetStoredFilePathsAsync(ct).ConfigureAwait(false);
            var deleted = await storage.ReconcileAsync(
                referenced,
                timeProvider.GetUtcNow().UtcDateTime.AddHours(-1),
                ct).ConfigureAwait(false);
            if (deleted > 0)
                logger.LogWarning("Package registry reconciliation removed {Count} orphaned payloads.", deleted);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Package registry storage reconciliation failed.");
        }
    }
}
