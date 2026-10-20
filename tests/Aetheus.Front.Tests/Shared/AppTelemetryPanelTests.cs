// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

public class AppTelemetryPanelTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AppTelemetryPanelTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private void SeedTelemetry()
    {
        _handler.SetJsonResponse("api/appmonitoring/projects/1/apps", new List<MonitoredAppDto>
        {
            new() { Id = 5, ProjectId = 1, Name = "toto-qa", CurrentStatus = AppHealthStatus.Up, HasIngestKey = true }
        });
        _handler.SetJsonResponse("metrics/names", new List<string> { "http.server.request.duration" });
        _handler.SetJsonResponse("/send-interval", new AppTelemetrySendIntervalDto
        {
            ServerIntervalSeconds = 300,
            Profile = DataRetentionProfile.Standard,
            ObservedIntervalSeconds = 60
        });
        _handler.SetJsonResponse("thresholds", new List<AppMetricThresholdDto>());
        _handler.SetJsonResponse("/logs", new PaginatedResult<AppLogEntryDto>());
        _handler.SetJsonResponse("/errors", new PaginatedResult<AppErrorEventDto>());
        _handler.SetJsonResponse("metrics/series", new MetricSeriesDto { MetricName = "http.server.request.duration" });
        _handler.SetJsonResponse("/web-analytics?days=30", new AppWebAnalyticsSummaryDto
        {
            UniqueVisitorsToday = 3,
            UniqueVisitorsThisWeek = 8,
            UniqueVisitorsThisMonth = 12,
            StorageBudgetBytes = 104_857_600,
            TopPages =
            [
                new AppWebAnalyticsPageDto { Route = "/docs", PageViews = 11 },
                new AppWebAnalyticsPageDto { Route = "/pricing", PageViews = 7 }
            ]
        });
        _handler.SetJsonResponse(
            "web-analytics/configuration",
            new AppWebAnalyticsConfigurationDto
            {
                SiteId = "toto-qa",
                AllowedOrigins = ["https://toto.example"],
                StorageBudgetBytes = 104_857_600
            });
    }

    [Fact]
    public void RendersAppPicker_AndTelemetrySubTabs()
    {
        SeedTelemetry();
        var cut = Render<AppTelemetryPanel>(p => p.Add(c => c.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("toto-qa"));

        Assert.Contains("toto-qa", cut.Markup);        // app picker populated
        Assert.Contains("Visitors", cut.Markup);
        Assert.Contains("Performance", cut.Markup);    // The test localizer echoes sub-tab keys.
        Assert.Contains("Logs", cut.Markup);
        Assert.Contains("Errors", cut.Markup);
        Assert.Contains("Ingestion", cut.Markup);
        Assert.Contains("WebAnalyticsPagesTab", cut.Markup);
    }

    [Fact]
    public void TheSendInterval_IsStatedAboveTheTabs_ForTheSelectedApplication()
    {
        SeedTelemetry();
        var cut = Render<AppTelemetryPanel>(p => p.Add(c => c.ProjectId, 1));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=telemetry-send-interval]")));
        Assert.Contains(_handler.Requests, r => r.Url.Contains("apps/5/send-interval", StringComparison.Ordinal));
        // In the header, before the sub-tabs: the line covers every figure and chart under it.
        var note = cut.Find("[data-testid=telemetry-send-interval]");
        var tabs = cut.Find("[role='tab']");
        Assert.Equal(AngleSharp.Dom.DocumentPositions.Following, note.CompareDocumentPosition(tabs) & AngleSharp.Dom.DocumentPositions.Following);
        Assert.Contains("TelemetrySendIntervalSetting", note.TextContent);
        Assert.Contains("TelemetrySendIntervalObservedDiffers", note.TextContent);
    }

    [Fact]
    public void R467_TheApplicationNamedByThePage_IsTheOneThePanelOpensOn()
    {
        SeedTelemetry();
        _handler.SetJsonResponse("api/appmonitoring/projects/1/apps", new List<MonitoredAppDto>
        {
            new() { Id = 5, ProjectId = 1, Name = "toto-qa", CurrentStatus = AppHealthStatus.Up, HasIngestKey = true },
            new() { Id = 9, ProjectId = 1, Name = "toto-prod", CurrentStatus = AppHealthStatus.Up, HasIngestKey = true }
        });

        var cut = Render<AppTelemetryPanel>(p => p.Add(c => c.ProjectId, 1).Add(c => c.SelectedAppId, 9));

        // The telemetry read is the one of the named application, not of the first in the list.
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Url.Contains("apps/9/web-analytics", StringComparison.Ordinal)));
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("apps/5/", StringComparison.Ordinal));
    }

    [Fact]
    public void R467_AnApplicationTheProjectDoesNotHave_FallsBackToTheFirstOne()
    {
        SeedTelemetry();

        var cut = Render<AppTelemetryPanel>(p => p.Add(c => c.ProjectId, 1).Add(c => c.SelectedAppId, 404));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Url.Contains("apps/5/web-analytics", StringComparison.Ordinal)));
    }

    [Fact]
    public void R355_TopPagesLiveInTheirOwnSubTab_NotInTheVisitorsView()
    {
        SeedTelemetry();
        var cut = Render<AppTelemetryPanel>(p => p.Add(c => c.ProjectId, 1));
        cut.WaitForAssertion(() => Assert.Contains("UniqueVisitorsToday", cut.Markup));

        // Top pages belong to their own tab.
        Assert.DoesNotContain("TopPagesThisMonth", cut.Markup);

        var pagesTab = cut.FindAll("[role='tab']").Single(tab => tab.GetAttribute("data-key") == "pages");
        Assert.Contains("WebAnalyticsPagesTab", pagesTab.TextContent);
        pagesTab.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("TopPagesThisMonth", cut.Markup);
            var rows = cut.FindAll(".omni-data-grid__table tbody tr[data-omni-row-index]");
            Assert.Equal(2, rows.Count);
            Assert.Contains("/docs", rows[0].TextContent);
            Assert.Contains("11", rows[0].TextContent);
            Assert.Contains("/pricing", rows[1].TextContent);
        });
    }

    [Fact]
    public void R355_PagesView_HttpFailure_ShowsErrorAndRetry()
    {
        _handler.SetResponse("api/appmonitoring/apps/5/web-analytics?days=30", HttpStatusCode.ServiceUnavailable);

        var cut = Render<AppWebAnalyticsPagesView>(parameters => parameters.Add(component => component.AppId, 5));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("LoadFailed", cut.Markup);
            Assert.Contains("Retry", cut.Markup);
            Assert.DoesNotContain("TopPagesThisMonth", cut.Markup);
        });
    }

    [Fact]
    public void EmptyProject_ShowsNoAppsState()
    {
        _handler.SetJsonResponse("api/appmonitoring/projects/1/apps", new List<MonitoredAppDto>());
        var cut = Render<AppTelemetryPanel>(p => p.Add(c => c.ProjectId, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        Assert.Contains("NoMonitoredApps", cut.Markup);
    }

    [Fact]
    public void VisitorsView_Success_RendersDailyCount()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/5/visitors?days=30", new AppVisitorSeriesDto
        {
            Today = 3
        });

        var cut = Render<AppVisitorsView>(parameters => parameters.Add(component => component.AppId, 5));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("UniqueVisitorsToday", cut.Markup);
            Assert.Contains(">3<", cut.Markup);
            Assert.DoesNotContain("LoadFailed", cut.Markup);
        });
    }

    [Fact]
    public void VisitorsView_HttpFailure_ShowsErrorAndRetry()
    {
        _handler.SetResponse("api/appmonitoring/apps/5/visitors?days=30", HttpStatusCode.ServiceUnavailable);

        var cut = Render<AppVisitorsView>(parameters => parameters.Add(component => component.AppId, 5));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("LoadFailed", cut.Markup);
            Assert.Contains("Retry", cut.Markup);
            Assert.DoesNotContain("UniqueVisitorsToday", cut.Markup);
        });
    }

    [Fact]
    public async Task R469_AStoredBatch_RefreshesTheFiguresAndTheLastEventDate()
    {
        const string url = "api/appmonitoring/apps/5/web-analytics?days=30";
        _handler.SetJsonResponse(url, new AppWebAnalyticsSummaryDto { UniqueVisitorsToday = 3 });
        var cut = Render<AppWebAnalyticsView>(parameters => parameters
            .Add(component => component.AppId, 5)
            .Add(component => component.ProjectId, 8));
        cut.WaitForAssertion(() => Assert.Contains(">3<", cut.Markup));
        _handler.SetJsonResponse(url, new AppWebAnalyticsSummaryDto
        {
            UniqueVisitorsToday = 4,
            LastIngestAtUtc = new DateTime(2026, 9, 30, 8, 15, 0, DateTimeKind.Local)
        });

        await cut.Instance.LiveFeed!.OnEventAsync(8);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(">4<", cut.Markup);
            Assert.Contains(new DateTime(2026, 9, 30, 8, 15, 0).ToString("g"), cut.FindAll(".web-analytics-summary-tile")[0].TextContent);
        });
        // No loader replaces the figures while a push is read.
        Assert.Empty(cut.FindAll(".aetheus-loader"));
    }

    [Fact]
    public void WebAnalyticsView_RendersPeriodCountsAndIngestionHealth()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/5/web-analytics?days=30", new AppWebAnalyticsSummaryDto
        {
            UniqueVisitorsToday = 3,
            UniqueVisitorsThisWeek = 8,
            UniqueVisitorsThisMonth = 12,
            SessionsThisMonth = 15,
            PageViewsThisMonth = 24,
            BrowserPerformanceSamplesLast30Days = 9,
            AverageBrowserNavigationDurationMs = 125,
            P95BrowserNavigationDurationMs = 240,
            BrowserErrorsLast30Days = 1,
            RejectedEvents = 2,
            EstimatedStorageBytes = 50,
            StorageBudgetBytes = 100,
            StorageUsagePercent = 50,
            Daily =
            [
                new AppWebAnalyticsPointDto { DayUtc = new DateOnly(2026, 9, 25), UniqueVisitors = 2, PageViews = 5 },
                new AppWebAnalyticsPointDto { DayUtc = new DateOnly(2026, 9, 26), UniqueVisitors = 3, PageViews = 6 }
            ],
            TopPages =
            [
                new AppWebAnalyticsPageDto { Route = "/docs", PageViews = 11 },
                new AppWebAnalyticsPageDto { Route = "/pricing", PageViews = 7 }
            ]
        });

        var cut = Render<AppWebAnalyticsView>(parameters => parameters.Add(component => component.AppId, 5));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("UniqueVisitorsThisWeek", cut.Markup);
            Assert.Contains(">8<", cut.Markup);
            // Storage information belongs in Ingestion.
            Assert.DoesNotContain("AnalyticsIngestionHealth", cut.Markup);
            Assert.DoesNotContain("AnalyticsStorage", cut.Markup);
            // Compact tiles, a third of the row from the small breakpoint.
            Assert.NotEmpty(cut.FindAll(".web-analytics-summary-tile"));
            // The date of the last event has its own tile, first of the row ("Never" without one).
            Assert.Contains("LastEventReceived", cut.FindAll(".web-analytics-summary-tile")[0].TextContent);
            Assert.Contains("Never", cut.FindAll(".web-analytics-summary-tile")[0].TextContent);
            // The daily chart is a wide, low drawing.
            Assert.DoesNotContain("LastAnalyticsIngest", cut.Markup);
            // The chart names both series per day, the UTC day axis and the count axis.
            Assert.Contains("AudienceDailyTitle", cut.Markup);
            Assert.Contains("AudienceDailyCaption", cut.Markup);
            Assert.Contains("UniqueVisitorsPerDay", cut.Markup);
            Assert.Contains("PageViewsPerDay", cut.Markup);
            Assert.Contains("AudienceDailyValueAxis", cut.Markup);
            Assert.Contains("AudienceDailyCategoryAxis", cut.Markup);
            Assert.Contains("BrowserPerformanceSamplesLast30Days", cut.Markup);
            Assert.Contains(">9<", cut.Markup);
            Assert.Contains("AverageBrowserNavigationDuration", cut.Markup);
            Assert.Contains(">125 ms<", cut.Markup);
            Assert.Contains("P95BrowserNavigationDuration", cut.Markup);
            Assert.Contains(">240 ms<", cut.Markup);
            Assert.Contains("BrowserErrorsLast30Days", cut.Markup);
            // Top pages have a dedicated sub-tab.
            Assert.DoesNotContain("TopPagesThisMonth", cut.Markup);
            Assert.Empty(cut.FindAll(".omni-data-grid__table"));
        });
    }
}
