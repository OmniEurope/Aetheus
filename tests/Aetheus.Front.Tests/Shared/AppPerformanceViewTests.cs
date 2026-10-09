// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// R-455 / R-444: the Performance tab of a supervised application. The overview and the per-route table
/// come from the timings its Aetheus.Telemetry package exported; the metric explorer opens on demand.
/// </summary>
public sealed class AppPerformanceViewTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AppPerformanceViewTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void TheOverviewAndTheRoutes_ShowTheExportedFigures()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/5/performance", new AppPerformanceReportDto
        {
            MeasuredAt = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc),
            Routes =
            [
                new AppRouteTimingDto { Method = "POST", Route = "api/login", Count = 12, P50Ms = 300, P95Ms = 2500, P99Ms = 2600, MaxMs = 2700 },
                new AppRouteTimingDto { Method = "GET", Route = "api/orders/{id}", Count = 30, P50Ms = 20, P95Ms = 80, P99Ms = 90, MaxMs = 95 }
            ]
        });

        var cut = Render<AppPerformanceView>(parameters => parameters.Add(component => component.AppId, 5));

        cut.WaitForAssertion(() => Assert.Contains("api/orders/{id}", cut.Markup, StringComparison.Ordinal));
        var tiles = cut.FindAll(".app-performance-tile");
        Assert.Equal(4, tiles.Count);
        // Recette R2-010: the last export is the first tile, the window its detail; no sentence above.
        Assert.Contains("AppPerformanceLastSent", tiles[0].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Never", tiles[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("AppPerformanceWindow", tiles[0].TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("p[data-testid='app-performance-window']"));
        Assert.Contains("2", tiles[1].TextContent, StringComparison.Ordinal);            // two routes
        Assert.Contains("42", tiles[2].TextContent, StringComparison.Ordinal);           // 12 + 30 requests
        Assert.Contains("2.50 s", tiles[3].TextContent.Replace(',', '.'), StringComparison.Ordinal);
        Assert.Contains("POST api/login", tiles[3].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R476_AWindowTheApplicationStated_IsShown_AndPercentilesOnFewCallsAreMarked()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/5/performance", new AppPerformanceReportDto
        {
            MeasuredAt = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc),
            WindowSince = new DateTime(2026, 9, 28, 6, 55, 0, DateTimeKind.Utc),
            Routes =
            [
                new AppRouteTimingDto { Method = "GET", Route = "api/few", Count = 12, P50Ms = 10, P95Ms = 40, P99Ms = 40, MaxMs = 40 },
                new AppRouteTimingDto { Method = "GET", Route = "api/some", Count = 30, P50Ms = 10, P95Ms = 30, P99Ms = 35, MaxMs = 35 },
                new AppRouteTimingDto { Method = "GET", Route = "api/many", Count = 400, P50Ms = 10, P95Ms = 20, P99Ms = 25, MaxMs = 90 }
            ]
        });

        var cut = Render<AppPerformanceView>(parameters => parameters.Add(component => component.AppId, 5));

        cut.WaitForAssertion(() => Assert.Contains("api/many", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("AppPerformanceWindowSince", cut.FindAll(".app-performance-tile")[0].TextContent, StringComparison.Ordinal);
        // 12 calls: p95 and p99 are the maximum; 30 calls: only the p99; 400 calls: neither.
        Assert.Equal(3, cut.FindAll("[data-testid='app-performance-few-calls']").Count);
        Assert.Equal("3 h 05 min", AppPerformanceView.SpanText(TimeSpan.FromMinutes(185)));
        Assert.Equal("12 min", AppPerformanceView.SpanText(TimeSpan.FromMinutes(12)));
    }

    [Fact]
    public async Task R2011_TheGridExportBar_WritesTheWindowTheFiguresAndEveryRoute_UnderAReadableName()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/5/performance", new AppPerformanceReportDto
        {
            MeasuredAt = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc),
            WindowSince = new DateTime(2026, 9, 28, 7, 0, 0, DateTimeKind.Utc),
            Routes =
            [
                new AppRouteTimingDto { Method = "POST", Route = "api/login", Count = 12, P50Ms = 300, P95Ms = 2500, P99Ms = 2600, MaxMs = 2700 },
                new AppRouteTimingDto { Method = "GET", Route = "api/orders/{id}", Count = 30, P50Ms = 20, P95Ms = 80.5, P99Ms = 90, MaxMs = 95 }
            ]
        });
        var module = JSInterop.SetupModule("./_content/OmniEurope.Blazor/omni-document-editor.js");
        module.SetupVoid("download", _ => true).SetVoidResult();
        var cut = Render<AppPerformanceView>(parameters => parameters
            .Add(component => component.AppId, 5)
            .Add(component => component.AppName, "Éditeur Toto"));
        cut.WaitForAssertion(() => Assert.Contains("api/orders/{id}", cut.Markup, StringComparison.Ordinal));

        // Recette R2-011: the bar of the grid, under the table; no standalone button. R2-124 (second decision
        // of 2026-10-05): Markdown alone, blue (R2-009), no CSV.
        Assert.Empty(cut.FindComponents<OmniMarkdownExportButton<AppRouteTimingDto>>());
        var button = Assert.Single(cut.FindAll(".omni-data-grid__export-button"));
        Assert.Contains("omni-button--primary", button.ClassList);
        await button.ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["download"]));
        var download = module.Invocations["download"].Single();
        // Recette R2-036: the application's name, lowercase without accents, then the content; OE appends its time.
        Assert.Matches(@"^editeur-toto-performance-\d{4}-\d{2}-\d{2}-\d{4}\.md$", Assert.IsType<string>(download.Arguments[0]));
        var markdown = System.Text.Encoding.UTF8.GetString(Assert.IsType<byte[]>(download.Arguments[2]));
        Assert.Contains("Éditeur Toto (#5)", markdown, StringComparison.Ordinal);
        Assert.Contains("2026-09-28T07:00:00.000Z", markdown, StringComparison.Ordinal);
        Assert.Contains("POST api/login (2500 ms)", markdown, StringComparison.Ordinal);
        Assert.Contains("AppPerformanceExportDurationsValue", markdown, StringComparison.Ordinal);
        Assert.Contains("api/orders/{id}", markdown, StringComparison.Ordinal);
        Assert.Contains("80", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void R474_TheExportHeader_CarriesTheWindowAndTheFigures()
    {
        var report = new AppPerformanceReportDto
        {
            MeasuredAt = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc),
            WindowSince = new DateTime(2026, 9, 28, 7, 0, 0, DateTimeKind.Utc),
            Routes =
            [
                new AppRouteTimingDto { Method = "POST", Route = "api/login", Count = 12, P50Ms = 300, P95Ms = 2500, P99Ms = 2600, MaxMs = 2700 },
                new AppRouteTimingDto { Method = "GET", Route = "api/orders/{id}", Count = 30, P50Ms = 20, P95Ms = 80.5, P99Ms = 90, MaxMs = 95 }
            ]
        };

        var fields = AppTelemetryMarkdownExport.PerformanceFields(5, "toto", report, key => key);

        Assert.Contains(fields, field => field.Value == "toto (#5)");
        Assert.Contains(fields, field => field.Value == "2026-09-28T10:00:00.000Z");
        Assert.Contains(fields, field => field.Value == "2026-09-28T07:00:00.000Z");
        Assert.Contains(fields, field => field.Value == "42");
        Assert.Contains(fields, field => field.Value == "POST api/login (2500 ms)");
        Assert.Equal(80.5, AppTelemetryMarkdownExport.MillisecondsExport(80.5));
        Assert.Equal(1.235, AppTelemetryMarkdownExport.MillisecondsExport(1.23456));
        Assert.Null(AppTelemetryMarkdownExport.MillisecondsExport(null));
    }

    [Fact]
    public void TheExplorer_IsNotLoadedUntilOpened()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/5/performance", new AppPerformanceReportDto());

        var cut = Render<AppPerformanceView>(parameters => parameters.Add(component => component.AppId, 5));

        cut.WaitForAssertion(() => Assert.Contains("AppPerformanceNoData", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("AppPerformanceExplore", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindComponents<AppMetricsView>());
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("metrics/names", StringComparison.Ordinal));
    }

    [Fact]
    public void AFailedLoad_SaysSo()
    {
        _handler.SetResponse(HttpMethod.Get, "api/appmonitoring/apps/5/performance", HttpStatusCode.InternalServerError);

        var cut = Render<AppPerformanceView>(parameters => parameters.Add(component => component.AppId, 5));

        cut.WaitForAssertion(() => Assert.Contains("LoadFailed", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("AppPerformanceNoData", cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData(12.4, "12 ms")]
    [InlineData(2500d, "2.50 s")]
    public void Durations_ReadInMillisecondsThenSeconds(double? value, string expected) =>
        Assert.Equal(expected, AppPerformanceView.Milliseconds(value).Replace(',', '.'));
}
