// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Logs;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Logs;

/// <summary>PLAN-003 lot 27 / D27: the Performance page and the browser-side timing it shows.</summary>
public sealed class PerformanceTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PerformanceTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<ClientApiTimings>();
    }

    [Theory]
    [InlineData("http://test/api/pipelines/51/runs?page=2", "api/pipelines/{id}/runs")]
    [InlineData("http://test/api/git/commits/0123456789abcdef0123456789abcdef01234567", "api/git/commits/{id}")]
    [InlineData("http://test/api/vaults/3fa85f64-5717-4562-b3fc-2c963f66afa6/secrets", "api/vaults/{id}/secrets")]
    [InlineData("http://test/api/projects", "api/projects")]
    public void BrowserPaths_DropIdentifiersAndQuery(string url, string expected) =>
        Assert.Equal(expected, ClientApiTimings.Normalize(new Uri(url)));

    [Fact]
    public void TheSessionStore_KeepsOnlyTheLastCalls()
    {
        var timings = new ClientApiTimings();
        for (var i = 0; i < ClientApiTimings.Capacity + 5; i++)
            timings.Record(new ClientApiCall(DateTime.UtcNow, "GET", $"api/{i}", 200, i, null));

        var kept = timings.Snapshot();
        Assert.Equal(ClientApiTimings.Capacity, kept.Count);
        Assert.Equal("api/5", kept[0].Path);
    }

    [Fact]
    public async Task TheHandler_KeepsACallThatGotNoAnswer_WithStatusZero()
    {
        var timings = new ClientApiTimings();
        using var invoker = new HttpMessageInvoker(new ClientApiTimingHandler(timings)
        {
            InnerHandler = new ThrowingHandler(new HttpRequestException("unreachable"))
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://test/api/pipelines/51/runs?page=2"),
            Xunit.TestContext.Current.CancellationToken));

        var call = Assert.Single(timings.Snapshot());
        Assert.Equal("api/pipelines/{id}/runs", call.Path);
        Assert.Equal(0, call.StatusCode);
        Assert.Null(call.Bytes);
    }

    [Fact]
    public async Task TheHandler_LeavesOutACallThePageCancelled()
    {
        var timings = new ClientApiTimings();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var invoker = new HttpMessageInvoker(new ClientApiTimingHandler(timings)
        {
            InnerHandler = new ThrowingHandler(new TaskCanceledException())
        });

        await Assert.ThrowsAsync<TaskCanceledException>(() => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://test/api/projects"), cancelled.Token));

        Assert.Empty(timings.Snapshot());
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    [Fact]
    public void ThePage_ShowsTheServerWindow_TheRoutes_AndThisBrowsersSlowestCalls()
    {
        _handler.SetJsonResponse("api/admin/performance", new ApiPerformanceReportDto
        {
            Since = new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc),
            SampleCount = 1234,
            Slowest = [new ApiCallTimingDto { Method = "GET", Route = "api/analysis/findings/{id}/occurrences", StatusCode = 200, DurationMs = 4200 }],
            Endpoints = [new ApiEndpointTimingDto { Method = "GET", Route = "api/analysis/findings/{id}/occurrences", Count = 9, P50Ms = 900, P95Ms = 4000, P99Ms = 4200, MaxMs = 4200 }]
        });
        Services.GetRequiredService<ClientApiTimings>().Record(
            new ClientApiCall(DateTime.UtcNow, "GET", "api/pipelines/{id}/runs", 200, 850, 2048));

        var cut = Render<Performance>();

        cut.WaitForAssertion(() => Assert.Contains("api/analysis/findings/{id}/occurrences", cut.Markup, StringComparison.Ordinal));
        // Decision of 2026-10-07: the window is the detail of the first key-figure tile, beside three more.
        var window = cut.Find("[data-testid='performance-window']");
        Assert.Contains("PerformanceTileRequests", window.TextContent, StringComparison.Ordinal);
        Assert.Contains("PerformanceWindow", window.TextContent, StringComparison.Ordinal);
        Assert.Contains("PerformanceTileSlowestCall", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("4.20 s", cut.Markup.Replace(',', '.'), StringComparison.Ordinal);
        Assert.Contains("api/pipelines/{id}/runs", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R181_NoRefreshButton_AnApiPerformancePushReloadsTheReport()
    {
        _handler.SetJsonResponse("api/admin/performance", new ApiPerformanceReportDto
        {
            Since = new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc),
            SampleCount = 1,
            Slowest = [new ApiCallTimingDto { Method = "GET", Route = "api/projects", StatusCode = 200, DurationMs = 10 }]
        });
        var cut = Render<Performance>();
        cut.WaitForAssertion(() => Assert.Contains("api/projects", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Refresh", StringComparison.Ordinal));
        _handler.SetJsonResponse("api/admin/performance", new ApiPerformanceReportDto
        {
            Since = new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc),
            SampleCount = 2,
            Slowest = [new ApiCallTimingDto { Method = "POST", Route = "api/pipelines/{id}/runs", StatusCode = 201, DurationMs = 900 }]
        });

        await cut.InvokeAsync(() => cut.Instance.ReloadFromPushAsync());

        Assert.Contains("api/pipelines/{id}/runs", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R181_AFailedPushReload_KeepsTheFiguresOnScreen()
    {
        _handler.SetJsonResponse("api/admin/performance", new ApiPerformanceReportDto
        {
            Since = new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc),
            SampleCount = 1,
            Slowest = [new ApiCallTimingDto { Method = "GET", Route = "api/projects", StatusCode = 200, DurationMs = 10 }]
        });
        var cut = Render<Performance>();
        cut.WaitForAssertion(() => Assert.Contains("api/projects", cut.Markup, StringComparison.Ordinal));
        _handler.SetResponse(HttpMethod.Get, "api/admin/performance", HttpStatusCode.InternalServerError);

        await cut.InvokeAsync(() => cut.Instance.ReloadFromPushAsync());

        Assert.Contains("api/projects", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("GridLoadFailed", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R2124_TheThreeTables_AreTabs_EachWithTheMarkdownExport_AndNoWholePageButton()
    {
        _handler.SetJsonResponse("api/admin/performance", new ApiPerformanceReportDto
        {
            Since = new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc),
            SampleCount = 1,
            Slowest = [new ApiCallTimingDto { Method = "GET", Route = "api/projects", StatusCode = 200, DurationMs = 10 }]
        });

        var cut = Render<Performance>();

        cut.WaitForAssertion(() => Assert.Contains("api/projects", cut.Markup, StringComparison.Ordinal));
        var tabs = cut.FindAll("[role='tab']");
        Assert.Equal(["slowest", "routes", "browser"], tabs.Select(tab => tab.GetAttribute("data-key")).ToArray());
        // Markdown alone under each of the three tables (second decision of 2026-10-05, no CSV); the former
        // whole-page Markdown button is gone.
        Assert.Equal(3, cut.FindAll(".omni-data-grid__export-button").Count);
        Assert.DoesNotContain("PerformanceExportMarkdown", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R2124_EachTableExport_SaysInItsHeaderSinceWhenTheFiguresCount()
    {
        var module = JSInterop.SetupModule("./_content/OmniEurope.Blazor/omni-document-editor.js");
        module.SetupVoid("download", _ => true).SetVoidResult();
        _handler.SetJsonResponse("api/admin/performance", new ApiPerformanceReportDto
        {
            Since = new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc),
            SampleCount = 3,
            Truncated = true,
            Slowest = [new ApiCallTimingDto { Method = "GET", Route = "api/projects", StatusCode = 200, DurationMs = 10 }],
            Endpoints = [new ApiEndpointTimingDto { Method = "GET", Route = "api/projects", Count = 3, P50Ms = 10, P95Ms = 10, P99Ms = 10, MaxMs = 10 }]
        });
        Services.GetRequiredService<ClientApiTimings>().Record(
            new ClientApiCall(new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc), "GET", "api/browser-call", 200, 12, 100));
        var cut = Render<Performance>();
        cut.WaitForAssertion(() => Assert.Contains("api/projects", cut.Markup, StringComparison.Ordinal));

        foreach (var index in new[] { 0, 1, 2 })
            await cut.FindAll(".omni-data-grid__export-button")[index].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Equal(3, module.Invocations["download"].Count));
        foreach (var download in module.Invocations["download"])
        {
            var markdown = System.Text.Encoding.UTF8.GetString(Assert.IsType<byte[]>(download.Arguments[2]));
            Assert.Contains("PerformanceExportSince", markdown, StringComparison.Ordinal);
            Assert.Contains("2026-09-10T08:00:00.000Z", markdown, StringComparison.Ordinal);
            Assert.Contains("PerformanceExportSamples", markdown, StringComparison.Ordinal);
            Assert.Contains("PerformanceExportTruncated", markdown, StringComparison.Ordinal);
            Assert.Contains("GridExportAddress", markdown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void R2124_TheWindowFields_SayWhenNoSampleWasMeasured()
    {
        var fields = Performance.WindowFields(new ApiPerformanceReportDto(), key => key);

        Assert.Equal(["PerformanceExportSince", "PerformanceExportSamples", "PerformanceExportTruncated"], fields.Select(field => field.Label).ToArray());
        Assert.Equal(["ExportNone", "0", "No"], fields.Select(field => field.Value).ToArray());
    }

    [Fact]
    public void AFailedLoad_SaysSo()
    {
        _handler.SetResponse(HttpMethod.Get, "api/admin/performance", HttpStatusCode.Forbidden);

        var cut = Render<Performance>();

        cut.WaitForAssertion(() => Assert.Contains("GridLoadFailed", cut.Markup, StringComparison.Ordinal));
    }
}
