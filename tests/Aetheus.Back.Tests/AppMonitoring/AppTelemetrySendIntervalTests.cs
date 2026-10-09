// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary>
/// The send interval a supervised application's telemetry views state: the one the server hands to its
/// Aetheus.Telemetry package (the project's « Conservation des données »), and the one its recent samples
/// show, which is how an application still on a package older than 1.1.0 is told apart.
/// </summary>
public sealed class AppTelemetrySendIntervalTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private readonly AppDbContext _db;
    private readonly AppTelemetrySendIntervalService _sut;

    public AppTelemetrySendIntervalTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _db.Projects.Add(new Project { Id = 1, Name = "standard", OrganizationId = 1 });
        _db.Projects.Add(new Project
        {
            Id = 2,
            Name = "overridden",
            OrganizationId = 1,
            DataRetentionProfile = DataRetentionProfile.Light,
            TelemetryExportIntervalMinutes = 3
        });
        _db.MonitoredApps.Add(new MonitoredApp { Id = 10, ProjectId = 1, Name = "aetheus" });
        _db.MonitoredApps.Add(new MonitoredApp { Id = 20, ProjectId = 2, Name = "other" });
        _db.SaveChanges();
        _sut = new AppTelemetrySendIntervalService(
            new AppDataRetentionRepository(_db), new AppMetricRepository(_db), new FakeTimeProvider(Now));
    }

    public void Dispose() => _db.Dispose();

    private void Sends(int appId, TimeSpan interval, int count, params string[] metrics)
    {
        for (var send = 0; send < count; send++)
        {
            var instant = Now - TimeSpan.FromTicks(interval.Ticks * send) - TimeSpan.FromSeconds(7);
            foreach (var metric in metrics.Length == 0 ? ["process.cpu", "http.server.duration"] : metrics)
                _db.AppMetricSamples.Add(new AppMetricSample { MonitoredAppId = appId, MetricName = metric, Timestamp = instant, Value = send });
        }
        _db.SaveChanges();
    }

    [Fact]
    public async Task TheStandardProfile_HandsFiveMinutes_AndSamplesAtThatPaceAreObservedAsSuch()
    {
        Sends(10, TimeSpan.FromMinutes(5), count: 10);

        var interval = await _sut.GetAsync(10, TestContext.Current.CancellationToken);

        Assert.NotNull(interval);
        Assert.Equal(300, interval.ServerIntervalSeconds);
        Assert.Equal(DataRetentionProfile.Standard, interval.Profile);
        Assert.False(interval.IsProjectOverride);
        Assert.Equal(300, interval.ObservedIntervalSeconds);
    }

    [Fact]
    public async Task AnOldPackage_SendingEveryMinute_IsObservedAtItsOwnPace()
    {
        Sends(10, TimeSpan.FromMinutes(1), count: 30);

        var interval = await _sut.GetAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(300, interval!.ServerIntervalSeconds);
        Assert.Equal(60, interval.ObservedIntervalSeconds);
    }

    [Fact]
    public async Task AProjectOverride_IsTheServerInterval_AndSaysSo()
    {
        var interval = await _sut.GetAsync(20, TestContext.Current.CancellationToken);

        Assert.Equal(180, interval!.ServerIntervalSeconds);
        Assert.Equal(DataRetentionProfile.Light, interval.Profile);
        Assert.True(interval.IsProjectOverride);
        Assert.Null(interval.ObservedIntervalSeconds);
    }

    [Fact]
    public async Task AnotherApplicationsSamples_AreNotObserved()
    {
        Sends(20, TimeSpan.FromMinutes(1), count: 30);

        var interval = await _sut.GetAsync(10, TestContext.Current.CancellationToken);

        Assert.Null(interval!.ObservedIntervalSeconds);
    }

    [Fact]
    public async Task AnUnknownApplication_IsNull()
    {
        Assert.Null(await _sut.GetAsync(404, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ThePointsOfOneSend_AreOneSend_EvenAFewMillisecondsApart()
    {
        var instants = Enumerable.Range(0, 8)
            .SelectMany(send => new[] { Now.AddMinutes(-send), Now.AddMinutes(-send).AddMilliseconds(-40) });

        Assert.Equal(60, TelemetrySendIntervalObserver.ObserveSeconds(instants));
    }

    [Fact]
    public void TheMostRecentSends_Decide_SoAChangedIntervalShowsOnceItRecurs()
    {
        // Three sends at 5 minutes after an hour at 1 minute: the newest gaps are the ones read.
        var instants = new List<DateTime> { Now, Now.AddMinutes(-5), Now.AddMinutes(-10), Now.AddMinutes(-15) };
        instants.AddRange(Enumerable.Range(16, 60).Select(minute => Now.AddMinutes(-minute)));

        Assert.Equal(300, TelemetrySendIntervalObserver.ObserveSeconds(instants));
    }

    [Fact]
    public void AStoppedApplication_LeavesOneLongGap_ThatIsNotTheInterval()
    {
        var instants = new[] { 0, 1, 2, 3, 63, 64, 65 }.Select(minute => Now.AddMinutes(-minute));

        Assert.Equal(60, TelemetrySendIntervalObserver.ObserveSeconds(instants));
    }

    [Fact]
    public void IrregularOrTooFewSends_ClaimNothing()
    {
        Assert.Null(TelemetrySendIntervalObserver.ObserveSeconds([Now, Now.AddMinutes(-1), Now.AddMinutes(-2)]));
        Assert.Null(TelemetrySendIntervalObserver.ObserveSeconds(
            new[] { 0, 1, 3, 7, 13, 21, 31 }.Select(minute => Now.AddMinutes(-minute))));
        Assert.Null(TelemetrySendIntervalObserver.ObserveSeconds([]));
    }
}
