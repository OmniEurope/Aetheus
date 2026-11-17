// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects;
using Aetheus.Front.Components.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MonitoringSection = Aetheus.Front.Components.Projects.Sections.Monitoring;

namespace Aetheus.Front.Tests.Projects;

/// <summary> Recette R-23, R-24 and R-26: the Supervision page has no « Télémétrie » heading, the application picker and the
/// probe button share one row with the alerts below it, and the performance and database tabs without a measure show only
/// their title (the test localizer echoes keys). </summary>
public sealed class SupervisionRecetteR23R24R26Tests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public SupervisionRecetteR23R24R26Tests() => _handler = BunitTestHelper.RegisterServices(this);

    private void SeedApp()
    {
        _handler.SetJsonResponse("/samples", new List<AppHealthSampleDto>());
        _handler.SetJsonResponse("metrics/names", new List<string>());
        _handler.SetJsonResponse("thresholds", new List<AppMetricThresholdDto>());
        _handler.SetJsonResponse("/logs", new PaginatedResult<AppLogEntryDto>());
        _handler.SetJsonResponse("/errors", new PaginatedResult<AppErrorEventDto>());
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("/web-analytics?days=30", new AppWebAnalyticsSummaryDto());
        _handler.SetJsonResponse("web-analytics/configuration", new AppWebAnalyticsConfigurationDto { SiteId = "toto-qa" });
        _handler.SetJsonResponse(HttpMethod.Get, "/probe-now", new AppProbeNowDto());
        _handler.SetJsonResponse(HttpMethod.Get, "/telemetry-export-request", new AppTelemetryExportRequestDto());
        _handler.SetJsonResponse("api/appmonitoring/projects/1/apps", new List<MonitoredAppDto>
        {
            new() { Id = 5, ProjectId = 1, Name = "toto-qa", Enabled = true, ProbeUrl = "https://toto.example/health", CurrentStatus = AppHealthStatus.Up, HasIngestKey = true }
        });
        _handler.SetJsonResponse("/send-interval", new AppTelemetrySendIntervalDto
        {
            ServerIntervalSeconds = 300,
            Profile = DataRetentionProfile.Standard,
            ObservedIntervalSeconds = 60
        });
    }

    [Fact]
    public async Task R23_TheSupervisionSection_HasNoTelemetryHeading()
    {
        _handler.SetJsonResponse("api/projects/1", new ProjectDetailDto { Id = 1, Name = "P1" });
        SeedApp();
        var loader = new ProjectDetailLoader(
            Services.GetRequiredService<ApiClient>(),
            Services.GetRequiredService<NavigationManager>(),
            NullLogger<ProjectDetailLoader>.Instance,
            Services.GetRequiredService<HubConnectionFactory>());
        await loader.EnsureLoadedAsync(1, Xunit.TestContext.Current.CancellationToken);

        var cut = Render<MonitoringSection>(p => p
            .Add(c => c.Id, 1)
            .AddCascadingValue("ProjectLoader", loader));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=telemetry-app-field]")));
        Assert.Empty(cut.FindAll(".monitoring-telemetry-heading"));
        Assert.DoesNotContain(cut.FindAll("h1,h2,h3,h4,h5,h6"), h => h.TextContent.Contains("Telemetry", StringComparison.Ordinal));
    }

    [Fact]
    public void R24_ThePickerAndTheProbeButton_ShareOneRow_AndTheAlertsStayBelowIt()
    {
        SeedApp();
        var cut = Render<AppTelemetryPanel>(p => p.Add(c => c.ProjectId, 1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=app-probe-now]")), TimeSpan.FromSeconds(30));

        var row = cut.Find("[data-testid=app-immediate-actions-row]");
        Assert.NotNull(row.QuerySelector("[data-testid=telemetry-app-field]"));
        Assert.NotNull(row.QuerySelector("[data-testid=app-probe-now]"));

        var at = new DateTime(2026, 11, 6, 9, 0, 1, DateTimeKind.Utc);
        _handler.SetJsonResponse(HttpMethod.Post, "apps/5/probe-now", new AppProbeNowDto
        {
            RequestedAt = at,
            Result = new AppHealthSampleDto { Timestamp = at, IsUp = true, StatusCode = 200, ResponseTimeMs = 31 }
        });
        // The probe result alert is displayed under the row, not inside it.
        cut.Find("[data-testid=app-probe-now] button").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=app-probe-now-result]")), TimeSpan.FromSeconds(30));
        Assert.Empty(row.QuerySelectorAll("[data-testid=app-probe-now-result]"));
        Assert.NotNull(cut.Find("[data-testid=app-immediate-actions]").QuerySelector("[data-testid=app-probe-now-result]"));
    }

    [Fact]
    public void R26_PerformanceWithoutMeasure_ShowsOnlyTheTitle_AndNoExplorer()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/5/performance", new AppPerformanceReportDto());

        var cut = Render<AppPerformanceView>(p => p.Add(c => c.AppId, 5));

        cut.WaitForAssertion(() => Assert.Contains("AppPerformanceNoData", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".empty-state-icon"));
        Assert.Empty(cut.FindAll(".empty-state-description"));
        Assert.DoesNotContain("AppPerformanceNoDataHint", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("AppPerformanceExplore", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R26_PerformanceWithAMeasure_ShowsTheExplorer()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/5/performance", new AppPerformanceReportDto
        {
            MeasuredAt = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc),
            Routes = [new AppRouteTimingDto { Method = "GET", Route = "api/x", Count = 3, P50Ms = 1, P95Ms = 2, P99Ms = 3, MaxMs = 4 }]
        });

        var cut = Render<AppPerformanceView>(p => p.Add(c => c.AppId, 5));

        cut.WaitForAssertion(() => Assert.Contains("AppPerformanceExplore", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void R26_DatabaseWithoutMeasure_ShowsOnlyTheTitle()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/7/database", new AppDatabaseSizeReportDto());

        var cut = Render<AppDatabaseSizeView>(p => p.Add(c => c.AppId, 7));

        cut.WaitForAssertion(() => Assert.Contains("AppDatabaseNoData", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".empty-state-icon"));
        Assert.Empty(cut.FindAll(".empty-state-description"));
        Assert.DoesNotContain("AppDatabaseNoDataHint", cut.Markup, StringComparison.Ordinal);
    }
}
