// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Metric views handle attribute-free series and distinguish name-load failure from an
/// empty metric list.</summary>
public sealed class AppMetricsViewTests : BunitContext
{
    [Fact]
    public void SeriesWithoutAttributes_RendersTheChartInsteadOfCrashing()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse("api/appmonitoring/apps/3/metrics/names", new List<string> { "http.server.request.duration" });
        handler.SetJsonResponse("metrics/series-groups", new List<string?> { "{\"route\":\"/a\"}", null });
        handler.SetJsonResponse("/send-interval", new AppTelemetrySendIntervalDto { ServerIntervalSeconds = 300 });
        handler.SetJsonResponse("metrics/series?metric", new MetricSeriesDto
        {
            MetricName = "http.server.request.duration",
            Unit = "s",
            Points =
            [
                new MetricPointDto { Timestamp = new DateTime(2026, 9, 26, 8, 0, 0, DateTimeKind.Utc), Value = 0.2 },
                new MetricPointDto { Timestamp = new DateTime(2026, 9, 26, 8, 1, 0, DateTimeKind.Utc), Value = 0.3 }
            ]
        });

        var cut = Render<AppMetricsView>(parameters => parameters.Add(component => component.AppId, 3));

        // Every option is built at render: the null group is what threw InvalidCastException in production.
        cut.WaitForAssertion(() => Assert.Contains("route=/a", cut.Markup));
        Assert.DoesNotContain("NoMetricsYet", cut.Markup);
        Assert.DoesNotContain("MetricsLoadFailed", cut.Markup);
    }

    [Fact]
    public void FailedNamesCall_SaysSoInsteadOfNoMetricsYet()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetResponse(HttpMethod.Get, "api/appmonitoring/apps/3/metrics/names", HttpStatusCode.InternalServerError);

        var cut = Render<AppMetricsView>(parameters => parameters.Add(component => component.AppId, 3));

        cut.WaitForAssertion(() => Assert.Contains("MetricsLoadFailed", cut.Markup));
        Assert.DoesNotContain("NoMetricsYet", cut.Markup);
    }

    [Fact]
    public void R444_WhileTheMetricListLoads_ALoaderShows_NotNoMetricsYet()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        var names = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.SetAsyncJsonResponse(HttpMethod.Get, "api/appmonitoring/apps/3/metrics/names", _ => names.Task);

        var cut = Render<AppMetricsView>(parameters => parameters.Add(component => component.AppId, 3));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindComponents<AetheusLoader>()));
        Assert.DoesNotContain("NoMetricsYet", cut.Markup);

        names.SetResult([]);
        // Under load the answered list was measured drawn 25 to 1143 ms after bUnit's 1 s default (PLAN-002 B7bis).
        cut.WaitForAssertion(() => Assert.Contains("NoMetricsYet", cut.Markup));
        Assert.Empty(cut.FindComponents<AetheusLoader>());
    }

    [Fact]
    public void R444_MetricSeriesAndPeriod_ShareOneLine_EachLabelTiedToItsField()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse("api/appmonitoring/apps/3/metrics/names", new List<string> { "http.server.request.duration" });
        handler.SetJsonResponse("metrics/series-groups", new List<string?> { "{\"route\":\"/a\"}", "{\"route\":\"/b\"}" });
        handler.SetJsonResponse("/send-interval", new AppTelemetrySendIntervalDto { ServerIntervalSeconds = 300 });
        handler.SetJsonResponse("metrics/series?metric", new MetricSeriesDto { MetricName = "http.server.request.duration" });

        var cut = Render<AppMetricsView>(parameters => parameters.Add(component => component.AppId, 3));

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".app-metrics-filters .app-metrics-filter").Count));
        foreach (var id in new[] { "app-metrics-series", "app-metrics-period" })
        {
            Assert.NotNull(cut.Find($"label[for='{id}']"));
            Assert.NotNull(cut.Find($"select#{id}"));
        }
        // The metric picker filters as one types (OE autocomplete): its field carries its own name.
        Assert.NotEmpty(cut.FindAll("[aria-label='Metric']"));
    }

    [Fact]
    public void TheSendInterval_SitsUnderTheChart_AndBeyondADayAPointIsAnHour()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse("api/appmonitoring/apps/3/metrics/names", new List<string> { "process.cpu" });
        handler.SetJsonResponse("metrics/series-groups", new List<string?> { null });
        handler.SetJsonResponse("metrics/series?metric", new MetricSeriesDto { MetricName = "process.cpu" });
        handler.SetJsonResponse("api/appmonitoring/apps/3/send-interval", new AppTelemetrySendIntervalDto
        {
            ServerIntervalSeconds = 300,
            Profile = DataRetentionProfile.Standard,
            ObservedIntervalSeconds = 300
        });

        var cut = Render<AppMetricsView>(parameters => parameters.Add(component => component.AppId, 3));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=telemetry-send-interval]")));
        Assert.DoesNotContain("TelemetrySendIntervalHourly", cut.Find("[data-testid=telemetry-send-interval]").TextContent);

        // OmniDropDown options carry their index: 0 = 24h, 1 = 7d.
        cut.Find("select#app-metrics-period").Change("1");

        cut.WaitForAssertion(() => Assert.Contains(
            "TelemetrySendIntervalHourly", cut.Find("[data-testid=telemetry-send-interval]").TextContent));
    }
}
