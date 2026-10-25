// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Aetheus.Front.Tests.TestDoubles;
using Microsoft.AspNetCore.Components;

namespace Aetheus.Front.Tests;

/// <summary>
/// Recette R1-89: four hubs that come back from one 404 s outage are one incident in the system logs, not
/// four errors, and the report still names each hub with its own loss time and duration.
/// </summary>
public sealed class RealtimeOutageReporterTests
{
    private static readonly DateTimeOffset LostAt = new(2026, 10, 12, 8, 6, 45, TimeSpan.Zero);

    [Fact]
    public async Task FourHubsBackFromOneOutage_AreReportedOnce_NamingEveryHub()
    {
        var (reporter, clock, sink) = Create();
        clock.Advance(TimeSpan.FromSeconds(404));

        foreach (var hub in new[] { "user", "alerts", "servers", "pipelines" })
            reporter.Restored(hub, LostAt);
        clock.Advance(RealtimeOutageReporter.GroupingWindow);

        var report = await sink.NextAsync();
        Assert.Equal("Realtime connections restored after an outage", report.Summary);
        // Restored on its own: a warning in the system log, not an error (decision of 2026-10-14).
        Assert.Equal(ClientErrorLogLevels.Warning, report.Level);
        Assert.Equal(
            "4 realtime connections came back after one outage: 'user', 'alerts', 'servers', 'pipelines' lost at 2026-10-12 08:06:45Z, back after 404 s.",
            report.Message);
        AssertNoMore(clock, sink);
    }

    [Fact]
    public async Task HubsWithDifferentLossTimes_KeepTheirOwnTimeAndDuration_InTheOneReport()
    {
        var (reporter, clock, sink) = Create();
        clock.Advance(TimeSpan.FromSeconds(404));

        reporter.Restored("pipelines", LostAt);
        reporter.Restored("user", LostAt.AddSeconds(100));
        clock.Advance(RealtimeOutageReporter.GroupingWindow);

        var report = await sink.NextAsync();
        Assert.Equal(
            "2 realtime connections came back after one outage: 'pipelines' lost at 2026-10-12 08:06:45Z, back after 404 s; 'user' lost at 2026-10-12 08:08:25Z, back after 304 s.",
            report.Message);
    }

    [Fact]
    public async Task OneHubBack_KeepsTheSingleConnectionSentence()
    {
        var (reporter, clock, sink) = Create();
        clock.Advance(TimeSpan.FromSeconds(90));

        reporter.Restored("servers", LostAt);
        clock.Advance(RealtimeOutageReporter.GroupingWindow);

        var report = await sink.NextAsync();
        Assert.Equal("Realtime connection restored after an outage", report.Summary);
        Assert.Equal(ClientErrorLogLevels.Warning, report.Level);
        Assert.Equal("The 'servers' realtime connection was lost at 2026-10-12 08:06:45Z and came back after 90 s.", report.Message);
    }

    [Fact]
    public async Task AShortOutage_IsNotReported()
    {
        var (reporter, clock, sink) = Create();
        clock.Advance(TimeSpan.FromSeconds(59));

        reporter.Restored("servers", LostAt);
        clock.Advance(RealtimeOutageReporter.GroupingWindow);

        AssertNoMore(clock, sink);
    }

    [Fact]
    public async Task TwoSeparateIncidents_AreTwoReports()
    {
        var (reporter, clock, sink) = Create();
        clock.Advance(TimeSpan.FromSeconds(120));
        reporter.Restored("servers", LostAt);
        clock.Advance(RealtimeOutageReporter.GroupingWindow);
        var first = await sink.NextAsync();

        clock.Advance(TimeSpan.FromMinutes(10));
        reporter.Restored("alerts", clock.GetUtcNow().AddSeconds(-70));
        clock.Advance(RealtimeOutageReporter.GroupingWindow);
        var second = await sink.NextAsync();

        Assert.Contains("'servers'", first.Message, StringComparison.Ordinal);
        Assert.Contains("'alerts'", second.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'servers'", second.Message, StringComparison.Ordinal);
    }

    // R1-42: the report tells how much of the outage the page was hidden, so a sleeping machine or a
    // parked tab reads differently from an outage seen on screen.
    [Fact]
    public async Task OneHub_NamesTheHiddenAndVisibleParts_OfTheOutage()
    {
        var (reporter, clock, sink, visibility) = CreateWithVisibility();
        visibility.Hidden();
        clock.Advance(TimeSpan.FromSeconds(300));
        visibility.Visible();
        clock.Advance(TimeSpan.FromSeconds(104));

        reporter.Restored("pipelines", LostAt);
        clock.Advance(RealtimeOutageReporter.GroupingWindow);

        var report = await sink.NextAsync();
        Assert.Equal(
            "The 'pipelines' realtime connection was lost at 2026-10-12 08:06:45Z and came back after 404 s. The page was hidden for 300 s and visible for 104 s during the outage.",
            report.Message);
    }

    [Fact]
    public async Task AnOutageSeenOnScreen_SaysThePageWasNeverHidden()
    {
        var (reporter, clock, sink, _) = CreateWithVisibility();
        clock.Advance(TimeSpan.FromSeconds(90));

        reporter.Restored("servers", LostAt);
        clock.Advance(RealtimeOutageReporter.GroupingWindow);

        var report = await sink.NextAsync();
        Assert.EndsWith("The page was hidden for 0 s and visible for 90 s during the outage.", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageStillHiddenWhenTheConnectionIsBack_CountsTheOpenStretch()
    {
        var (reporter, clock, sink, visibility) = CreateWithVisibility();
        clock.Advance(TimeSpan.FromSeconds(20));
        visibility.Hidden();
        clock.Advance(TimeSpan.FromSeconds(100));

        reporter.Restored("servers", LostAt);
        clock.Advance(RealtimeOutageReporter.GroupingWindow);

        var report = await sink.NextAsync();
        Assert.EndsWith("The page was hidden for 100 s and visible for 20 s during the outage.", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeveralHubs_EachKeepTheirOwnHiddenPart_InTheOneReport()
    {
        var (reporter, clock, sink, visibility) = CreateWithVisibility();
        clock.Advance(TimeSpan.FromSeconds(100));
        visibility.Hidden();
        clock.Advance(TimeSpan.FromSeconds(304));

        reporter.Restored("pipelines", LostAt);
        reporter.Restored("user", LostAt.AddSeconds(100));
        clock.Advance(RealtimeOutageReporter.GroupingWindow);

        var report = await sink.NextAsync();
        Assert.Equal(
            "2 realtime connections came back after one outage: 'pipelines' lost at 2026-10-12 08:06:45Z, back after 404 s (page hidden 304 s, visible 100 s); 'user' lost at 2026-10-12 08:08:25Z, back after 304 s (page hidden 304 s, visible 0 s).",
            report.Message);
    }

    private static (RealtimeOutageReporter Reporter, ObservedFakeClock Clock, ReportSink Sink, PageVisibilityTimeline Visibility) CreateWithVisibility()
    {
        var clock = new ObservedFakeClock(LostAt);
        var sink = new ReportSink();
        var visibility = new PageVisibilityTimeline(clock);
        var errors = new ClientErrorReporter(new HttpClient(sink) { BaseAddress = new Uri("http://localhost/") }, new TestNavigation());
        return (new RealtimeOutageReporter(errors, clock, visibility), clock, sink, visibility);
    }

    private static (RealtimeOutageReporter Reporter, ObservedFakeClock Clock, ReportSink Sink) Create()
    {
        var clock = new ObservedFakeClock(LostAt);
        var sink = new ReportSink();
        var errors = new ClientErrorReporter(new HttpClient(sink) { BaseAddress = new Uri("http://localhost/") }, new TestNavigation());
        return (new RealtimeOutageReporter(errors, clock), clock, sink);
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/pipelines/runs/2729");
    }

    /// <summary>
    /// No further report can come: the grouping window was armed once per incident and none is still
    /// open, so no flush is left to post one, and every report the closed windows posted was taken.
    /// </summary>
    private static void AssertNoMore(ObservedFakeClock clock, ReportSink sink)
    {
        Assert.Equal(0, clock.Pending(RealtimeOutageReporter.GroupingWindow));
        Assert.Equal(sink.Taken, clock.Armed(RealtimeOutageReporter.GroupingWindow));
        Assert.True(sink.IsEmpty);
    }

    /// <summary>The api/client-errors endpoint: keeps what each report posted, and says when one arrives.</summary>
    private sealed class ReportSink : HttpMessageHandler
    {
        private readonly ConcurrentQueue<ClientErrorLogRequest> _reports = new();
        private readonly StateSignal _posted = new();
        private int _taken;

        /// <summary>Reports taken by <see cref="NextAsync"/>.</summary>
        public int Taken => Volatile.Read(ref _taken);

        public bool IsEmpty => _reports.IsEmpty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _reports.Enqueue((await request.Content!.ReadFromJsonAsync<ClientErrorLogRequest>(cancellationToken))!);
            _posted.Pulse();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        public async Task<ClientErrorLogRequest> NextAsync()
        {
            ClientErrorLogRequest? report = null;
            await _posted.WhenAsync(() => _reports.TryDequeue(out report), "an outage report posted");
            Interlocked.Increment(ref _taken);
            return report!;
        }
    }
}
