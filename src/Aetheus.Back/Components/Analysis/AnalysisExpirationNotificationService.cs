// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Configuration;

namespace Aetheus.Back.Components.Analysis;

public sealed class AnalysisExpirationNotificationService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<AnalysisExpirationNotificationService> logger,
    IPostgresLeaderLease leaderLease) : LeaderBackgroundService(leaderLease, "aetheus:analysis-expiration-notifications")
{
    private static readonly TimeSpan NoticeWindow = TimeSpan.FromDays(7);

    protected override async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        await RunCycleAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(BackendRuntimeDefaults.MaintenanceInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await RunCycleAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try { await NotifyAsync(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogError(exception, "Analysis expiration notification cycle failed"); }
    }

    internal async Task NotifyAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAnalysisRepository>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var items = await repository.GetExpiringGovernanceItemsAsync(now, now + NoticeWindow, 200, ct).ConfigureAwait(false);
        foreach (var item in items)
        {
            await notifications.SendEventAsync("analysis.governance.expiring", new
            {
                item.Kind,
                item.Id,
                item.OrganizationId,
                item.ProjectId,
                item.FindingId,
                item.ExpiresAt,
                item.CreatedByUsername
            }, ct).ConfigureAwait(false);
            await repository.MarkExpirationNotificationSentAsync(item.Kind, item.Id, now, ct).ConfigureAwait(false);
        }
        if (items.Count > 0)
            logger.LogInformation("Sent {Count} analysis governance expiration notifications", items.Count);
    }
}
