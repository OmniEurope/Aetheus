// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

public class AppProbeWorkerTests
{
    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        public StubHandler(HttpStatusCode status)
            : this((_, _) => Task.FromResult(new HttpResponseMessage(status)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => sendAsync(request, cancellationToken);
    }

    private static (AppProbeWorker Worker, IServerApiClient Api, FakeTimeProvider Time) Build(
        HttpMessageHandler? handler = null)
    {
        var api = Substitute.For<IServerApiClient>();
        api.GetAppProbesAsync(Arg.Any<CancellationToken>()).Returns(new List<AppProbeConfigDto>
        {
            new() { MonitoredAppId = 1, ProbeUrl = "http://localhost/health", ProbeIntervalSeconds = 30, ProbeTimeoutSeconds = 5, ExpectedStatusCode = 200 }
        });

        var enrollment = Substitute.For<IEnrollmentService>();
        enrollment.IsEnrolled.Returns(true);

        var http = new HttpClient(handler ?? new StubHandler(HttpStatusCode.OK));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero));
        var worker = new AppProbeWorker(api, enrollment, factory, time, Substitute.For<ILogger<AppProbeWorker>>());
        return (worker, api, time);
    }

    [Fact]
    public async Task RunTick_ProbesDueApp_AndReports()
    {
        var (worker, api, _) = Build();
        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await api.Received(1).ReportAppProbeResultsAsync(
            Arg.Is<List<AppProbeResultDto>>(r => r.Count == 1 && r[0].MonitoredAppId == 1 && r[0].IsUp),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunTick_AnImmediateProbeRequest_ProbesBeforeTheInterval_OncePerRequest()
    {
        // R-29: a user asked for an immediate probe; the agent probes at once instead of waiting for the interval.
        var (worker, api, time) = Build();
        api.GetAppProbesAsync(Arg.Any<CancellationToken>()).Returns(new List<AppProbeConfigDto>
        {
            new() { MonitoredAppId = 1, ProbeUrl = "http://localhost/health", ProbeIntervalSeconds = 3600, ProbeTimeoutSeconds = 5, ExpectedStatusCode = 200 }
        });
        await worker.RunTickAsync(TestContext.Current.CancellationToken);
        api.ClearReceivedCalls();
        var requestedAt = time.GetUtcNow().UtcDateTime;
        api.GetAppProbesAsync(Arg.Any<CancellationToken>()).Returns(new List<AppProbeConfigDto>
        {
            new() { MonitoredAppId = 1, ProbeUrl = "http://localhost/health", ProbeIntervalSeconds = 3600, ProbeTimeoutSeconds = 5, ExpectedStatusCode = 200, ProbeRequestedAt = requestedAt }
        });

        time.Advance(TimeSpan.FromSeconds(AppMonitoringDefaults.AgentProbeConfigRefreshSeconds));
        await worker.RunTickAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(AppMonitoringDefaults.AgentProbeTickSeconds));
        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await api.Received(1).ReportAppProbeResultsAsync(
            Arg.Is<List<AppProbeResultDto>>(r => r.Count == 1 && r[0].MonitoredAppId == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunTick_UnexpectedStatus_ReportsDownWithActualStatus()
    {
        var (worker, api, _) = Build(new StubHandler(HttpStatusCode.InternalServerError));
        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await api.Received(1).ReportAppProbeResultsAsync(
            Arg.Is<List<AppProbeResultDto>>(results =>
                results.Count == 1
                && !results[0].IsUp
                && results[0].StatusCode == 500
                && AppProbeFailure.TryParse(results[0].Error) == new AppProbeFailure("status", "localhost", 500, 200)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunTick_RequestFailure_ReportsDownWithoutStatus()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection failed")));
        var (worker, api, _) = Build(handler);
        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await api.Received(1).ReportAppProbeResultsAsync(
            Arg.Is<List<AppProbeResultDto>>(results =>
                results.Count == 1
                && !results[0].IsUp
                && results[0].StatusCode == null
                && AppProbeFailure.TryParse(results[0].Error) == new AppProbeFailure("unreachable", "localhost", null, null)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunTick_DoesNotReProbe_BeforeIntervalElapses()
    {
        var (worker, api, time) = Build();
        await worker.RunTickAsync(TestContext.Current.CancellationToken); // probes, nextDue = +30s

        time.Advance(TimeSpan.FromSeconds(10)); // still before the 30s interval
        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        // Exactly one report (nothing new due -> empty buffer -> no second send).
        await api.Received(1).ReportAppProbeResultsAsync(Arg.Any<List<AppProbeResultDto>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunTick_ReQueuesResults_WhenBackendReportFails()
    {
        var (worker, api, time) = Build();
        var batchSizes = new List<int>();
        api.When(x => x.ReportAppProbeResultsAsync(Arg.Any<List<AppProbeResultDto>>(), Arg.Any<CancellationToken>()))
            .Do(ci => batchSizes.Add(((List<AppProbeResultDto>)ci[0]).Count));
        api.ReportAppProbeResultsAsync(Arg.Any<List<AppProbeResultDto>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("backend down")), Task.CompletedTask);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);       // probe#1 -> send throws -> keep buffered
        time.Advance(TimeSpan.FromSeconds(31));                  // app due again
        await worker.RunTickAsync(TestContext.Current.CancellationToken);       // probe#2 -> send succeeds with BOTH results

        Assert.Equal(2, batchSizes.Count);
        Assert.Equal(1, batchSizes[0]); // first attempt carried 1 result
        Assert.Equal(2, batchSizes[1]); // failed result was re-queued and re-sent alongside the new one
    }
}
