// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aetheus.Back.Components.SystemLogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OmniEurope.Performance;

namespace Aetheus.Back.Tests.SystemLogs;

/// <summary>Verifies retained API timings and report mapping, including route-template measurements
/// and change signals from a real ASP.NET Core host. Report reads are excluded from
/// measurement.</summary>
public sealed class ApiPerformanceTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Collector_KeepsOnlyApiRoutes_AndForgetsSamplesOlderThan24Hours()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(Now));
        using var feed = new RequestDurationFeed(ApiPerformanceReport.Configure, clock);

        feed.Request("api/projects", "GET", 200, 12);
        feed.Request("{*path:nonfile}", "GET", 200, 3); // the SPA fallback
        feed.Request(null, "GET", 404, 1);              // no endpoint matched
        clock.Advance(TimeSpan.FromHours(25));
        feed.Request("api/servers", "GET", 200, 40);

        var report = ApiPerformanceReport.Build(feed.Recorder);
        var kept = Assert.Single(report.Endpoints);
        Assert.Equal(("api/servers", 1), (kept.Route, kept.Count));
    }

    [Fact]
    public void Report_MapsThePackageSummary_ToThePageContract_WithoutTheLeadingSlash()
    {
        var summary = new RequestPerformanceSummary(
            WindowStart: Now.AddHours(-24),
            Slowest: [new RequestTimingSample(Now, "POST", "/api/pipelines/{id}", 201, 900)],
            Since: Now.AddMinutes(-30),
            SampleCount: 31,
            Truncated: true,
            Routes: [new RouteTimingSummary("GET", "/api/projects", 30, 15, 29, 30, 30)]);

        var report = ApiPerformanceReport.ToDto(summary);

        Assert.Equal((31, true, Now.AddMinutes(-30)), (report.SampleCount, report.Truncated, report.Since));
        var slowest = Assert.Single(report.Slowest);
        Assert.Equal(("POST", "api/pipelines/{id}", 201, 900d, Now),
            (slowest.Method, slowest.Route, slowest.StatusCode, slowest.DurationMs, slowest.At));
        var projects = Assert.Single(report.Endpoints);
        Assert.Equal(("api/projects", 30, 15d, 29d, 30d, 30d),
            (projects.Route, projects.Count, projects.P50Ms, projects.P95Ms, projects.P99Ms, projects.MaxMs));
    }

    [Fact]
    public async Task RealApiRequests_AreHeardUnderTheirTemplate_AndSignalAChange()
    {
        var client = AdminClient();
        var ct = TestContext.Current.CancellationToken;
        var recorder = factory.Services.GetRequiredService<RequestPerformanceRecorder>();
        await DrainAsync(recorder, ct);
        var before = await ReadAsync(client, ct);

        for (var i = 0; i < 4; i++)
            (await client.GetAsync("/api/projects", ct)).EnsureSuccessStatusCode();
        await recorder.WaitForChangeAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        // A sample is recorded once the response has completed, after the client already read it:
        // the change signal can fire before the fourth request is in (3 counted, ylaunch -ta of
        // 2026-10-11). Read again until all four are heard, within the same 5 s budget.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var after = await ReadAsync(client, ct);
        while (ProjectsCount(after) < 4 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, ct);
            after = await ReadAsync(client, ct);
        }

        var projects = Assert.Single(after.Endpoints, endpoint => endpoint.Route == "api/projects" && endpoint.Method == "GET");
        Assert.True(projects.Count >= 4, $"GET api/projects counted {projects.Count} times, expected at least 4");
        Assert.True(after.SampleCount >= before.SampleCount + 4);
        // One collector, the one the host started.
        Assert.Same(recorder,
            factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<RequestPerformanceRecorder>().Single());
    }

    [Fact]
    public async Task ReadingTheReport_IsNeverMeasured_SoAnOpenPageDoesNotReloadItself()
    {
        var client = AdminClient();
        var ct = TestContext.Current.CancellationToken;
        var recorder = factory.Services.GetRequiredService<RequestPerformanceRecorder>();
        await DrainAsync(recorder, ct);

        await ReadAsync(client, ct);
        var report = await ReadAsync(client, ct);

        Assert.DoesNotContain(report.Endpoints, endpoint => endpoint.Route == ApiPerformanceReport.ReportRoute);
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quiet.CancelAfter(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recorder.WaitForChangeAsync(quiet.Token));
    }

    private HttpClient AdminClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CustomWebApplicationFactory.GenerateTestToken());
        return client;
    }

    /// <summary>Takes any change signal left by earlier traffic of the shared host.</summary>
    private static async Task DrainAsync(RequestPerformanceRecorder recorder, CancellationToken ct)
    {
        // Keeps taking signals until a full second passes without one (bounded at 10 s): a late
        // signal from earlier traffic of the shared host on a loaded machine can no longer slip
        // past a single short drain window and surface later as a false change.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(TimeSpan.FromSeconds(1));
            try { await recorder.WaitForChangeAsync(window.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return; }
        }
    }

    private static int ProjectsCount(ApiPerformanceReportDto report) =>
        report.Endpoints.FirstOrDefault(endpoint => endpoint.Route == "api/projects" && endpoint.Method == "GET")?.Count ?? 0;

    private static async Task<ApiPerformanceReportDto> ReadAsync(HttpClient client, CancellationToken ct)
    {
        var response = await client.GetAsync("/" + ApiPerformanceReport.ReportRoute, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiPerformanceReportDto>(TestJsonOptions.Default, ct))!;
    }
}
