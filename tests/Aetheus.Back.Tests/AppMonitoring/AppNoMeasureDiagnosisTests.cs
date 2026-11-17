// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary>R-27: an empty Performance or Database view says why no server measure ever arrived, from what the server
/// holds about the application (key, enabled flag, refusal log, last reception).</summary>
public sealed class AppNoMeasureDiagnosisTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
    private const string CurrentHash = "CURRENTHASH-aaaaaaaaaaaa";
    private const string PreviousHash = "PREVIOUSHASH-bbbbbbbbbbb";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Now));
    private readonly IngestKeyRejectionLog _rejections;
    private readonly AppDbContext _db;
    private readonly AppTelemetryService _sut;

    public AppNoMeasureDiagnosisTests()
    {
        _rejections = new IngestKeyRejectionLog(_time, NullLogger<IngestKeyRejectionLog>.Instance);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _sut = new AppTelemetryService(
            new AppMonitoringRepository(_db), new AppMetricRepository(_db),
            new AppLogRepository(_db), new AppErrorRepository(_db), new AppVisitorRepository(_db),
            new IngestKeyHasher(new TokenHashKeys(new JwtOptions { SigningKey = "unit-test-signing-key-least-32-bytes!!" })),
            Substitute.For<IIngestService>(), Substitute.For<IAuditService>(),
            new ConfigurationBuilder().Build(), _rejections, _time);
    }

    public void Dispose() => _db.Dispose();

    private static MonitoredApp App(Action<MonitoredApp>? configure = null)
    {
        var app = new MonitoredApp
        {
            Id = 1,
            ProjectId = 1,
            Name = "wordmorrow",
            Enabled = true,
            IngestKeyHash = CurrentHash,
            IngestKeyExpiresAt = Now.AddDays(30)
        };
        configure?.Invoke(app);
        return app;
    }

    [Fact]
    public void NoIngestKey_IsTheFirstCause_EvenWhenTheAppIsDisabled()
    {
        var result = AppNoMeasureDiagnosis.Build(
            App(app => { app.IngestKeyHash = null; app.Enabled = false; }), Now, _rejections);

        Assert.Equal(AppNoMeasureCause.NoIngestKey, result.Cause);
    }

    [Fact]
    public void ADisabledApp_IsReported()
    {
        var result = AppNoMeasureDiagnosis.Build(App(app => app.Enabled = false), Now, _rejections);

        Assert.Equal(AppNoMeasureCause.AppDisabled, result.Cause);
    }

    [Fact]
    public void AnExpiredKey_IsReported_WithItsExpiryDate()
    {
        var expired = Now.AddDays(-2);

        var result = AppNoMeasureDiagnosis.Build(App(app => app.IngestKeyExpiresAt = expired), Now, _rejections);

        Assert.Equal(AppNoMeasureCause.KeyExpired, result.Cause);
        Assert.Equal(expired, result.CauseAt);
    }

    [Fact]
    public void ARefusalOfAKeyTheAppStores_IsReported_WithTheDateOfTheLastRefusal()
    {
        _rejections.Record(PreviousHash);
        _time.Advance(TimeSpan.FromMinutes(5));

        var result = AppNoMeasureDiagnosis.Build(
            App(app => app.PreviousIngestKeyHash = PreviousHash), _time.GetUtcNow().UtcDateTime, _rejections);

        Assert.Equal(AppNoMeasureCause.KeyRefused, result.Cause);
        Assert.Equal(Now, result.CauseAt);
    }

    [Fact]
    public void ARefusalOfAKeyOfAnotherApp_IsNotAttributedToThisOne()
    {
        _rejections.Record("SOMEONE-ELSES-KEY-hash");

        var result = AppNoMeasureDiagnosis.Build(App(), Now, _rejections);

        Assert.Equal(AppNoMeasureCause.NothingReceived, result.Cause);
        Assert.Null(result.CauseAt);
    }

    [Fact]
    public void AValidKeyWithNothingEverReceived_SaysSo_WithoutALastReception()
    {
        var result = AppNoMeasureDiagnosis.Build(App(), Now, _rejections);

        Assert.Equal(AppNoMeasureCause.NothingReceived, result.Cause);
        Assert.Null(result.LastReceptionAt);
    }

    [Fact]
    public void WhenOtherMeasuresArrive_TheMissingOneIsReported_WithTheLastReception()
    {
        var last = Now.AddMinutes(-3);

        var result = AppNoMeasureDiagnosis.Build(App(app => app.LastIngestAt = last), Now, _rejections);

        Assert.Equal(AppNoMeasureCause.MeasureMissing, result.Cause);
        Assert.Equal(last, result.LastReceptionAt);
    }

    [Fact]
    public async Task APerformanceView_WithoutAnyMeasure_CarriesTheReason()
    {
        _db.Projects.Add(new Project { Id = 1, Name = "Wordmorrow", Description = "d" });
        _db.MonitoredApps.Add(App(app => app.LastIngestAt = Now.AddHours(-1)));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var report = await _sut.GetPerformanceAsync(1, TestContext.Current.CancellationToken);

        Assert.Null(report.MeasuredAt);
        Assert.Equal(AppNoMeasureCause.MeasureMissing, report.NoMeasure?.Cause);
        Assert.Equal(Now.AddHours(-1), report.NoMeasure?.LastReceptionAt);
    }

    [Fact]
    public async Task ADatabaseView_WithoutAnyMeasure_CarriesTheReason()
    {
        _db.Projects.Add(new Project { Id = 1, Name = "Wordmorrow", Description = "d" });
        _db.MonitoredApps.Add(App(app => app.IngestKeyHash = null));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var report = await _sut.GetDatabaseSizeAsync(1, TestContext.Current.CancellationToken);

        Assert.Null(report.MeasuredAt);
        Assert.Equal(AppNoMeasureCause.NoIngestKey, report.NoMeasure?.Cause);
    }

    [Fact]
    public async Task AViewWithAMeasure_CarriesNoReason()
    {
        _db.MonitoredApps.Add(App(app => app.LastIngestAt = Now));
        _db.AppMetricSamples.Add(new AppMetricSample
        {
            MonitoredAppId = 1,
            Timestamp = Now,
            MetricName = DatabaseSizeMetrics.DatabaseSizeMetric,
            Value = 1_000_000,
            AttributesJson = """{"db.namespace":"wordmorrow"}"""
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var report = await _sut.GetDatabaseSizeAsync(1, TestContext.Current.CancellationToken);

        Assert.NotNull(report.MeasuredAt);
        Assert.Null(report.NoMeasure);
    }
}
