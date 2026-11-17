// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.PackageFeeds;

/// <summary>
/// Periodically refreshes every feed's tracked packages against their upstream registries. Opt-in:
/// disabled unless <c>PackageFeeds:SyncIntervalMinutes</c> is &gt; 0 (so dev/CI never hammers public
/// registries). Manual "sync now" is always available through the controller.
/// </summary>
public sealed class PackageFeedSyncHostedService(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<PackageFeedSyncHostedService> logger,
    IPostgresLeaderLease leaderLease) : LeaderBackgroundService(leaderLease, "aetheus:package-feed-sync")
{
    protected override async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = configuration.GetValue("PackageFeeds:SyncIntervalMinutes", 0);
        if (intervalMinutes <= 0)
        {
            logger.LogInformation("PackageFeed sync is disabled (PackageFeeds:SyncIntervalMinutes <= 0).");
            return;
        }

        var period = TimeSpan.FromMinutes(intervalMinutes);
        using var timer = new PeriodicTimer(period);
        do
        {
            try
            {
                await SyncAllAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "PackageFeed periodic sync run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task SyncAllAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPackageFeedRepository>();
        var service = scope.ServiceProvider.GetRequiredService<IPackageFeedService>();

        var feedIds = await repo.GetFeedIdsAsync(ct).ConfigureAwait(false);
        foreach (var feedId in feedIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var result = await service.SyncFeedAsync(feedId, ct).ConfigureAwait(false);
                if (result is not null)
                    logger.LogInformation("PackageFeed {FeedId} synced: {Synced} ok, {Failed} failed, {Unsupported} unsupported",
                        feedId, result.Synced, result.Failed, result.Unsupported);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Isolate per feed: an unexpected failure on one feed (e.g. a DB conflict) must not
                // abort the sync of the remaining feeds until the next tick.
                logger.LogWarning(ex, "PackageFeed {FeedId} sync failed; continuing with the next feed", feedId);
            }
        }
    }
}
