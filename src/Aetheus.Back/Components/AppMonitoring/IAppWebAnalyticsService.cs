// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.AppMonitoring;

public readonly record struct WebAnalyticsIngestOutcome(int Accepted, int Replayed, int Rejected);

public interface IAppWebAnalyticsService
{
    Task<WebAnalyticsIngestOutcome> IngestAsync(
        int appId,
        IReadOnlyList<AppWebAnalyticsIngestEvent> events,
        CancellationToken ct = default);
    Task<AppWebAnalyticsSummaryDto?> GetSummaryAsync(int appId, int days, CancellationToken ct = default);

    /// <summary>The daily visitors of the last <paramref name="days"/> days (1-90) with their first visit time
    /// and entry page; null when the app does not exist.</summary>
    Task<IReadOnlyList<AppWebAnalyticsVisitorDto>?> GetVisitorsAsync(int appId, int days, CancellationToken ct = default);

    /// <summary>Rolls every app whose audience data reached its budget; returns the rows
    /// removed.</summary>
    Task<int> RollStorageAsync(CancellationToken ct = default);
}
