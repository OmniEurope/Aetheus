// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The hosted loop of <see cref="AppProbeWorker"/> on a fake clock: it waits for enrollment, ticks on its
/// period, survives a tick that throws, and leaves cleanly when the host stops it while a probe is in flight.
/// </summary>
public sealed class AppProbeWorkerLoopTests
{
    [Fact]
    public async Task TheLoop_WaitsForEnrollment_SurvivesAFailedTick_AndStopsCleanlyDuringAProbe()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero));
        var enrollment = Substitute.For<IEnrollmentService>();
        var enrollmentChecks = 0;
        enrollment.IsEnrolled.Returns(_ => Interlocked.Increment(ref enrollmentChecks) >= 3);
        var api = Substitute.For<IServerApiClient>();
        api.GetAppProbesAsync(Arg.Any<CancellationToken>()).Returns(new List<AppProbeConfigDto>
        {
            new() { MonitoredAppId = 1, ProbeUrl = "http://localhost/health", ProbeIntervalSeconds = 15, ProbeTimeoutSeconds = 5, ExpectedStatusCode = 200 },
        });
        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clients = 0;
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AppProbeWorker.ProbeHttpClientName).Returns(_ =>
        {
            // The first tick fails outside any probe's own error handling; the second one probes until stopped.
            if (Interlocked.Increment(ref clients) == 1)
                throw new InvalidOperationException("no handler registered");
            return new HttpClient(new HangingHandler(probing));
        });
        var logger = Substitute.For<ILogger<AppProbeWorker>>();
        using var worker = new AppProbeWorker(api, enrollment, factory, time, logger);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await AdvanceUntil(time, () => probing.Task.IsCompleted);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(worker.ExecuteTask);
        Assert.Equal(TaskStatus.RanToCompletion, worker.ExecuteTask.Status);
        Assert.Equal(2, Volatile.Read(ref clients));
        Assert.True(Volatile.Read(ref enrollmentChecks) >= 3);
        var warnings = logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == "Log" && Equals(call.GetArguments()[0], LogLevel.Warning))
            .Select(call => (Message: call.GetArguments()[2]?.ToString(), Error: call.GetArguments()[3]))
            .ToList();
        Assert.Contains(warnings, warning =>
            warning.Message == "App probe tick failed, will retry next interval" && warning.Error is InvalidOperationException);
        await api.DidNotReceive().ReportAppProbeResultsAsync(Arg.Any<List<AppProbeResultDto>>(), Arg.Any<CancellationToken>());
    }

    private static async Task AdvanceUntil(FakeTimeProvider time, Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "The probe loop did not reach the expected state.");
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    // Answers nothing until the request is cancelled, as a probe still in flight when the host stops.
    private sealed class HangingHandler(TaskCompletionSource probing) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            probing.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
