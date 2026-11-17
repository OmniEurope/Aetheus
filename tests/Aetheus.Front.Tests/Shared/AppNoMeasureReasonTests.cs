// SPDX-License-Identifier: EUPL-1.2
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>R-27: an empty Performance or Database view says why no server measure ever arrived (the cause the server read from
/// the application's key, enabled flag, refusal log and last reception), and keeps its general hint when it gave none. The test
/// localizer answers each key with its name, so the cause shows as its key.</summary>
public sealed class AppNoMeasureReasonTests : BunitContext
{
    private static readonly DateTime LastReception = new(2026, 11, 5, 10, 38, 0, DateTimeKind.Utc);
    private readonly BunitTestHelper.TestHandler _handler;

    public AppNoMeasureReasonTests() => _handler = BunitTestHelper.RegisterServices(this);

    [Theory]
    [InlineData(AppNoMeasureCause.NoIngestKey, "NoMeasureNoIngestKey")]
    [InlineData(AppNoMeasureCause.AppDisabled, "NoMeasureAppDisabled")]
    [InlineData(AppNoMeasureCause.KeyExpired, "NoMeasureKeyExpired")]
    [InlineData(AppNoMeasureCause.KeyRefused, "NoMeasureKeyRefused")]
    [InlineData(AppNoMeasureCause.NothingReceived, "NoMeasureNothingReceived")]
    [InlineData(AppNoMeasureCause.MeasureMissing, "NoMeasureMissingPerformance")]
    public void ThePerformanceView_SaysWhyNoTimingArrived(AppNoMeasureCause cause, string expectedKey)
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/5/performance", new AppPerformanceReportDto
        {
            NoMeasure = new AppNoMeasureDto { Cause = cause, CauseAt = LastReception }
        });

        var cut = Render<AppPerformanceView>(parameters => parameters.Add(component => component.AppId, 5));

        cut.WaitForAssertion(() => Assert.Contains(expectedKey, cut.Find("[data-testid='app-performance-no-data']").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("AppPerformanceNoDataHint", cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AppNoMeasureCause.NoIngestKey, "NoMeasureNoIngestKey")]
    [InlineData(AppNoMeasureCause.KeyRefused, "NoMeasureKeyRefused")]
    [InlineData(AppNoMeasureCause.MeasureMissing, "NoMeasureMissingDatabase")]
    public void TheDatabaseView_SaysWhyNoMeasurementArrived(AppNoMeasureCause cause, string expectedKey)
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/7/database", new AppDatabaseSizeReportDto
        {
            NoMeasure = new AppNoMeasureDto { Cause = cause, CauseAt = LastReception }
        });

        var cut = Render<AppDatabaseSizeView>(parameters => parameters.Add(component => component.AppId, 7));

        cut.WaitForAssertion(() => Assert.Contains(expectedKey, cut.Find("[data-testid='app-database-no-data']").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("AppDatabaseNoDataHint", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLastReception_IsToldWhenThereIsOne()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/5/performance", new AppPerformanceReportDto
        {
            NoMeasure = new AppNoMeasureDto { Cause = AppNoMeasureCause.MeasureMissing, LastReceptionAt = LastReception }
        });

        var cut = Render<AppPerformanceView>(parameters => parameters.Add(component => component.AppId, 5));

        cut.WaitForAssertion(() => Assert.Contains("NoMeasureLastReception", cut.Find("[data-testid='app-performance-no-data']").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutALastReception_NoReceptionSentenceIsAdded()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/5/performance", new AppPerformanceReportDto
        {
            NoMeasure = new AppNoMeasureDto { Cause = AppNoMeasureCause.NothingReceived }
        });

        var cut = Render<AppPerformanceView>(parameters => parameters.Add(component => component.AppId, 5));

        cut.WaitForAssertion(() => Assert.Contains("NoMeasureNothingReceived", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("NoMeasureLastReception", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutACauseFromTheServer_OnlyTheTitleShows_R26()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/5/performance", new AppPerformanceReportDto());
        _handler.SetJsonResponse("api/appmonitoring/apps/7/database", new AppDatabaseSizeReportDto());

        var performance = Render<AppPerformanceView>(parameters => parameters.Add(component => component.AppId, 5));
        var database = Render<AppDatabaseSizeView>(parameters => parameters.Add(component => component.AppId, 7));

        performance.WaitForAssertion(() => Assert.Contains("AppPerformanceNoData", performance.Markup, StringComparison.Ordinal));
        Assert.Empty(performance.FindAll(".empty-state-description"));
        database.WaitForAssertion(() => Assert.Contains("AppDatabaseNoData", database.Markup, StringComparison.Ordinal));
        Assert.Empty(database.FindAll(".empty-state-description"));
    }

    [Fact]
    public void AnExpiredKey_IsToldByTheChecklist_NotAsAMissingKey()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/5/web-analytics/configuration", new AppWebAnalyticsConfigurationDto());
        _handler.SetJsonResponse("api/appmonitoring/apps/5/web-analytics?days=30", new AppWebAnalyticsSummaryDto());
        var app = new MonitoredAppDto
        {
            Id = 5,
            ProjectId = 19,
            Name = "w1",
            Enabled = true,
            HasIngestKey = false,
            IngestKeyCreatedAt = LastReception.AddDays(-90)
        };

        var cut = Render<AppCollectionChecklist>(parameters => parameters.Add(c => c.App, app));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='collection-checklist']")));
        var lines = cut.FindAll("[data-testid='collection-line']").Select(line => line.TextContent).ToList();
        Assert.Contains(lines, line => line.Contains("CollectionIngestKeyExpired", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("CollectionIngestKeyMissing", StringComparison.Ordinal));
    }
}
