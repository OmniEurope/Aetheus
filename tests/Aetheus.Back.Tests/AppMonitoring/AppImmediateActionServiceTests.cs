// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary> R-29: the immediate probe and the immediate telemetry export answer with what really arrived after the
/// request, never with an older reading, and say when the answer did not come. </summary>
public sealed class AppImmediateActionServiceTests : IDisposable
{
    private const int BackendApp = 1;
    private const int AgentApp = 2;
    private const int AgentServer = 7;

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));
    private readonly IHttpClientFactory _http = Substitute.For<IHttpClientFactory>();
    private readonly CountingHandler _handler = new(HttpStatusCode.OK);
    private readonly AppMonitoringRepository _repo;
    private readonly AppMonitoringService _monitoring;
    private readonly AppImmediateActionService _sut;

    public AppImmediateActionServiceTests()
    {
        _repo = new AppMonitoringRepository(_db);
        _monitoring = new AppMonitoringService(_repo, Substitute.For<INotificationService>(),
            Substitute.For<IEntityChangeNotifier>(), _time, NullLogger<AppMonitoringService>.Instance);
        _http.CreateClient(AppProbeService.ProbeHttpClientName).Returns(_ => new HttpClient(_handler));
        _sut = new AppImmediateActionService(_repo, _monitoring, new AppHttpProber(_http, _time), _time);

        var earlier = _time.GetUtcNow().UtcDateTime.AddMinutes(-10);
        _db.MonitoredApps.AddRange(
            new MonitoredApp
            {
                Id = BackendApp,
                ProjectId = 1,
                Name = "w1",
                ProbeUrl = "https://w1.example/health",
                Enabled = true,
                IngestKeyHash = "hash",
                AnalyticsEnabled = true,
                AnalyticsLastIngestAt = earlier
            },
            new MonitoredApp { Id = AgentApp, ProjectId = 1, Name = "w2", ServerId = AgentServer, ProbeUrl = "http://localhost:5000/health", Enabled = true });
        // An older sample must never be taken for the answer.
        _db.AppHealthSamples.Add(new AppHealthSample { MonitoredAppId = BackendApp, Timestamp = earlier, IsUp = false, Error = "old" });
        _db.SaveChanges();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task ABackendProbedApp_IsProbedDuringTheCall_AndTheFreshSampleIsTheResult()
    {
        var state = await _sut.ProbeNowAsync(BackendApp, Ct);

        Assert.NotNull(state);
        Assert.Equal(1, _handler.Calls);
        Assert.False(state.ByAgent);
        Assert.Equal(Now, state.RequestedAt);
        var result = Assert.IsType<AppHealthSampleDto>(state.Result);
        Assert.True(result.IsUp);
        Assert.Equal(200, result.StatusCode);
        Assert.True(result.Timestamp >= state.RequestedAt);
        Assert.Equal(2, await _db.AppHealthSamples.CountAsync(s => s.MonitoredAppId == BackendApp, Ct));
    }

    [Fact]
    public async Task ASecondClickWithinTheDebounce_DoesNotProbeAgain()
    {
        await _sut.ProbeNowAsync(BackendApp, Ct);
        _time.Advance(TimeSpan.FromSeconds(3));

        var again = await _sut.ProbeNowAsync(BackendApp, Ct);

        Assert.Equal(1, _handler.Calls);
        Assert.NotNull(again?.Result);
        _time.Advance(TimeSpan.FromSeconds(AppImmediateActionDefaults.DebounceSeconds));
        await _sut.ProbeNowAsync(BackendApp, Ct);
        Assert.Equal(2, _handler.Calls);
    }

    [Fact]
    public async Task AnAgentProbedApp_CarriesTheRequestToItsAgent_UntilTheAgentsNextSampleAnswersIt()
    {
        var state = await _sut.ProbeNowAsync(AgentApp, Ct);

        Assert.NotNull(state);
        Assert.True(state.ByAgent);
        Assert.Null(state.Result);
        Assert.False(state.TimedOut);
        Assert.Equal(0, _handler.Calls);
        var config = Assert.Single(await _monitoring.GetProbeConfigsForServerAsync(AgentServer, Ct));
        Assert.Equal(state.RequestedAt, config.ProbeRequestedAt);

        // The agent's clock may lag: the first sample ingested after the request answers it whatever its timestamp.
        _time.Advance(TimeSpan.FromSeconds(40));
        await _monitoring.IngestProbeResultsAsync(
            [new AppProbeResultDto { MonitoredAppId = AgentApp, Timestamp = Now.AddSeconds(-50), IsUp = true, StatusCode = 200, ResponseTimeMs = 12 }], Ct);

        var answered = await _sut.GetProbeNowAsync(AgentApp, Ct);
        Assert.Equal(12, answered?.Result?.ResponseTimeMs);
        Assert.Null(Assert.Single(await _monitoring.GetProbeConfigsForServerAsync(AgentServer, Ct)).ProbeRequestedAt);
    }

    [Fact]
    public async Task AnAgentThatNeverAnswers_IsSaidTimedOut()
    {
        await _sut.ProbeNowAsync(AgentApp, Ct);
        _time.Advance(TimeSpan.FromSeconds(AppImmediateActionDefaults.AgentProbeWaitSeconds + 1));

        var state = await _sut.GetProbeNowAsync(AgentApp, Ct);

        Assert.True(state?.TimedOut);
        Assert.Null(state?.Result);
    }

    [Fact]
    public async Task ADisabledAppOrAnAppWithoutUrl_IsRefusedExplicitly()
    {
        var disabled = await _db.MonitoredApps.SingleAsync(a => a.Id == BackendApp, Ct);
        disabled.Enabled = false;
        await _db.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<ConflictException>(() => _sut.ProbeNowAsync(BackendApp, Ct));

        var noUrl = await _db.MonitoredApps.SingleAsync(a => a.Id == AgentApp, Ct);
        noUrl.ProbeUrl = null;
        await _db.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<ConflictException>(() => _sut.ProbeNowAsync(AgentApp, Ct));
        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task ATelemetryRequest_IsTakenOnce_ThenAnsweredByTheNextMetricsExport()
    {
        var requested = await _sut.RequestTelemetryExportAsync(BackendApp, Ct);
        Assert.NotNull(requested?.RequestedAt);
        Assert.Null(requested.DeliveredAt);
        // The audience event from before the request is not a visit received since.
        Assert.Null(requested.VisitsReceivedAt);

        _time.Advance(TimeSpan.FromSeconds(20));
        var requestId = await _sut.TakeTelemetryExportRequestAsync(BackendApp, Ct);
        Assert.False(string.IsNullOrEmpty(requestId));
        Assert.Null(await _sut.TakeTelemetryExportRequestAsync(BackendApp, Ct));

        var taken = await _sut.GetTelemetryExportRequestAsync(BackendApp, Ct);
        Assert.Equal(Now, taken?.DeliveredAt);
        Assert.Null(taken?.ReceivedAt);

        _time.Advance(TimeSpan.FromSeconds(2));
        await _repo.MarkTelemetryExportReceivedAsync(BackendApp, Now, Ct);
        var received = await _sut.GetTelemetryExportRequestAsync(BackendApp, Ct);
        Assert.Equal(Now, received?.ReceivedAt);
        Assert.False(received?.TimedOut);
    }

    [Fact]
    public async Task AMetricsExportWithoutATakenRequest_AnswersNothing()
    {
        await _sut.RequestTelemetryExportAsync(BackendApp, Ct);
        // A periodic export arriving before the package took the request is not its answer.
        await _repo.MarkTelemetryExportReceivedAsync(BackendApp, Now, Ct);

        var state = await _sut.GetTelemetryExportRequestAsync(BackendApp, Ct);
        Assert.Null(state?.ReceivedAt);
    }

    [Fact]
    public async Task ARequestNoPackageTakes_IsSaidTimedOut_AndANewOneCanThenBeAsked()
    {
        var first = await _sut.RequestTelemetryExportAsync(BackendApp, Ct);
        _time.Advance(TimeSpan.FromSeconds(5));
        var duringWait = await _sut.RequestTelemetryExportAsync(BackendApp, Ct);
        Assert.Equal(first?.RequestedAt, duringWait?.RequestedAt);

        _time.Advance(TimeSpan.FromSeconds(AppImmediateActionDefaults.TelemetryPickupWaitSeconds));
        Assert.True((await _sut.GetTelemetryExportRequestAsync(BackendApp, Ct))?.TimedOut);

        var renewed = await _sut.RequestTelemetryExportAsync(BackendApp, Ct);
        Assert.Equal(Now, renewed?.RequestedAt);
        Assert.False(renewed?.TimedOut);
    }

    [Fact]
    public async Task VisitsReceivedAfterTheRequest_AreReportedWithTheirTime()
    {
        await _sut.RequestTelemetryExportAsync(BackendApp, Ct);
        _time.Advance(TimeSpan.FromSeconds(4));
        var app = await _db.MonitoredApps.SingleAsync(a => a.Id == BackendApp, Ct);
        app.AnalyticsLastIngestAt = Now;
        await _db.SaveChangesAsync(Ct);

        Assert.Equal(Now, (await _sut.GetTelemetryExportRequestAsync(BackendApp, Ct))?.VisitsReceivedAt);
    }

    [Fact]
    public async Task TheTest_OnlyCountsDataIngestedAfterTheClick_AndKeepsTheOlderReadingAsContext()
    {
        var before = Now.AddMinutes(-3);
        var app = await _db.MonitoredApps.SingleAsync(a => a.Id == BackendApp, Ct);
        app.LastIngestAt = before;
        await _db.SaveChangesAsync(Ct);

        var requested = await _sut.RequestTelemetryExportAsync(BackendApp, Ct);
        Assert.Equal(before, requested?.LastIngestAt);
        Assert.Null(requested?.DataReceivedAt);

        _time.Advance(TimeSpan.FromSeconds(45));
        await _repo.TouchIngestAsync(BackendApp, 0, Now, Ct);

        var after = await _sut.GetTelemetryExportRequestAsync(BackendApp, Ct);
        Assert.Equal(Now, after?.DataReceivedAt);
        Assert.Equal(Now, after?.LastIngestAt);
        // Logs or errors arrived, but the package never took the request: no export answer is claimed.
        Assert.Null(after?.ReceivedAt);
        Assert.Null(after?.DeliveredAt);
    }

    [Fact]
    public async Task AnAppThatNeverSentAnything_HasNoReading()
    {
        var state = await _sut.RequestTelemetryExportAsync(BackendApp, Ct);

        Assert.Null(state?.LastIngestAt);
        Assert.Null(state?.DataReceivedAt);
    }

    [Fact]
    public async Task AnAppWithoutIngestKey_CannotBeAskedForItsTelemetry()
    {
        await Assert.ThrowsAsync<ConflictException>(() => _sut.RequestTelemetryExportAsync(AgentApp, Ct));
    }

    [Fact]
    public async Task AnUnknownApp_IsNull()
    {
        Assert.Null(await _sut.ProbeNowAsync(99, Ct));
        Assert.Null(await _sut.RequestTelemetryExportAsync(99, Ct));
        Assert.Null(await _sut.GetProbeNowAsync(99, Ct));
    }

    public void Dispose() => _db.Dispose();

    private sealed class CountingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
