// SPDX-License-Identifier: EUPL-1.2

using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

public partial class AppWebAnalyticsView : AppWebAnalyticsSummaryViewBase, IAsyncDisposable
{
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;

    /// <summary>The app's project: the scope of the <c>AppTelemetryChanged</c> push that reloads this view.</summary>
    [Parameter] public int ProjectId { get; set; }

    // Refresh audience figures and the last-event date on project telemetry events so the overview
    // follows ingested batches.
    private EntityOperationalFeed? _liveFeed;
    internal EntityOperationalFeed? LiveFeed => _liveFeed;

    protected sealed record SummaryTile(string Label, string Value);
    protected sealed record DailyPoint(string Label, int UniqueVisitors, long PageViews);

    protected IReadOnlyList<SummaryTile> SummaryTiles =>
        _summary is null
            ? []
            :
            [
                new(L["LastEventReceived"], LastEventText),
                new(L["UniqueVisitorsToday"], _summary.UniqueVisitorsToday.ToString("N0")),
                new(L["UniqueVisitorsThisWeek"], _summary.UniqueVisitorsThisWeek.ToString("N0")),
                new(L["UniqueVisitorsThisMonth"], _summary.UniqueVisitorsThisMonth.ToString("N0")),
                new(L["SessionsThisMonth"], _summary.SessionsThisMonth.ToString("N0")),
                new(L["ReturningVisitorsThisMonth"], _summary.ReturningVisitorsThisMonth.ToString("N0")),
                new(L["PageViewsThisMonth"], _summary.PageViewsThisMonth.ToString("N0")),
                new(L["AuthenticatedUniqueThisMonth"], _summary.AuthenticatedUniqueThisMonth.ToString("N0")),
                new(
                    L["BrowserPerformanceSamplesLast30Days"],
                    _summary.BrowserPerformanceSamplesLast30Days.ToString("N0")),
                new(
                    L["AverageBrowserNavigationDuration"],
                    FormatDuration(_summary.AverageBrowserNavigationDurationMs)),
                new(
                    L["P95BrowserNavigationDuration"],
                    FormatDuration(_summary.P95BrowserNavigationDurationMs)),
                new(L["BrowserErrorsLast30Days"], _summary.BrowserErrorsLast30Days.ToString("N0"))
            ];

    /// <summary>R-30: days shown at first; the user drags the chart range handles to choose another range.</summary>
    internal const int DailyWindowDays = 7;

    // Bound to the OE range navigator under the chart; null until the series is known.
    private int? _rangeStart;
    private int? _rangeEnd;

    /// <summary>Index of the first day shown at first: the last <see cref="DailyWindowDays"/> days.</summary>
    internal static int DefaultRangeStart(int count) => Math.Max(0, count - DailyWindowDays);

    private void EnsureDefaultRange()
    {
        if (_rangeStart is null && _summary is { Daily.Count: > 0 } summary)
            _rangeStart = DefaultRangeStart(summary.Daily.Count);
    }

    protected IReadOnlyList<DailyPoint> DailyPoints =>
        (_summary?.Daily ?? []).Select(point =>
            new DailyPoint(
                DateDisplay.DayMonth(point.DayUtc),
                point.UniqueVisitors,
                point.PageViews)).ToList();
    protected IReadOnlyList<OmniChartPoint> DailyVisitorPoints => OmniChartData.Indexed(DailyPoints, point => point.UniqueVisitors, point => point.Label);
    protected IReadOnlyList<OmniChartPoint> DailyPageViewPoints => OmniChartData.Indexed(DailyPoints, point => point.PageViews, point => point.Label);

    /// <summary>A rounded top for the shared value axis (1, 2 or 5 times a power of ten, at least
    /// 5), so its ticks read as round counts instead of the raw daily maximum.</summary>
    protected double AudienceAxisMaximum => RoundedAxisMaximum(
        (_summary?.Daily ?? []).Select(point => (double)Math.Max(point.UniqueVisitors, point.PageViews)).DefaultIfEmpty(0).Max());

    internal static double RoundedAxisMaximum(double peak)
    {
        if (peak <= 5) return 5;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(peak)));
        foreach (var step in new[] { 1d, 2d, 5d, 10d })
            if (step * magnitude >= peak) return step * magnitude;
        return 10 * magnitude;
    }

    /// <summary>The date of the last stored event, local and readable, in its own tile.</summary>
    protected string LastEventText => _summary?.LastIngestAtUtc is { } receivedAt
        ? receivedAt.ToLocalTime().ToString("g")
        : L["Never"];

    protected override async Task OnParametersSetAsync()
    {
        if (ProjectId > 0)
        {
            _liveFeed ??= new EntityOperationalFeed(HubFactory);
            _ = _liveFeed.StartAsync(ResourceType.Project, OperationalRealtimeEvents.AppTelemetryChanged, ProjectId,
                () => InvokeAsync(RefreshFromPushAsync));
        }
        await base.OnParametersSetAsync();
        EnsureDefaultRange();
    }

    /// <summary>A push re-reads the summary in place: no loader, and a failed read keeps the figures on
    /// screen for the next push.</summary>
    private async Task RefreshFromPushAsync()
    {
        var appId = AppId;
        try
        {
            var summary = await Api.Monitoring.GetAppWebAnalyticsAsync(appId);
            if (AppId != appId || summary is null) return;
            _summary = summary;
            _loadFailed = false;
            EnsureDefaultRange();
            StateHasChanged();
        }
        catch (HttpRequestException)
        {
            // A push whose read fails keeps the figures on screen; the next stored batch reads again.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_liveFeed is not null)
            await _liveFeed.DisposeAsync();
    }

    private string FormatDuration(double? durationMs) =>
        durationMs is null ? L["NotAvailable"] : $"{durationMs.Value:N0} ms";
}
