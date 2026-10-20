// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.AppMonitoring;

public readonly record struct WebAnalyticsPurgeResult(
    int Events,
    int Sessions,
    int PeriodIdentities,
    int Aggregates,
    int Pages,
    int Rejections,
    int IngestVolumes)
{
    public int Total => Events + Sessions + PeriodIdentities + Aggregates + Pages + Rejections + IngestVolumes;
}

public interface IAppWebAnalyticsRepository
{
    Task<(int Accepted, int Replayed)> RecordAsync(
        int appId,
        IReadOnlyList<AppWebAnalyticsIngestEvent> events,
        int sessionTimeoutMinutes,
        DateTime receivedAtUtc,
        CancellationToken ct = default);

    Task<long> EstimateStorageBytesAsync(int appId, CancellationToken ct = default);

    /// <summary>Deletes the app's oldest raw audience rows until it weighs at most <paramref
    /// name="targetBytes"/>.</summary>
    Task<WebAnalyticsTrimResult> TrimToAsync(int appId, long targetBytes, CancellationToken ct = default);

    /// <summary>The apps holding raw audience rows, with their storage budget (0 when none was set).</summary>
    Task<IReadOnlyList<WebAnalyticsStorageBudget>> GetStorageBudgetsAsync(CancellationToken ct = default);

    /// <summary>Counts accepted events since <paramref name="sinceUtc"/> across backend
    /// colours.</summary>
    Task<int> CountAcceptedSinceAsync(int appId, DateTime sinceUtc, CancellationToken ct = default);
    Task<HashSet<string>> GetRouteNamesAsync(int appId, CancellationToken ct = default);
    Task RecordRejectionAsync(int appId, string reasonCode, int count, DateTime occurredAtUtc, CancellationToken ct = default);
    Task<AppWebAnalyticsSummaryDto> GetSummaryAsync(
        int appId,
        DateOnly todayUtc,
        DateOnly weekStartUtc,
        DateOnly monthStartUtc,
        DateOnly historyStartUtc,
        long storageBudgetBytes,
        long rejectedEvents,
        DateTime? lastIngestAtUtc,
        CancellationToken ct = default);

    Task<WebAnalyticsPurgeResult> PurgeAsync(
        DateTime eventCutoffUtc,
        DateTime sessionCutoffUtc,
        DateOnly aggregateCutoffUtc,
        DateTime rejectionCutoffUtc,
        CancellationToken ct = default);
}
