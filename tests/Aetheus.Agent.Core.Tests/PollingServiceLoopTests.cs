// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The hosted loop of <see cref="PollingService"/> on a fake clock: it waits for enrollment, polls on its
/// interval, survives a failed poll (including a cancellation that is not a shutdown) and leaves cleanly when
/// the host stops it during the wait between two polls. Every branch is reached by advancing the clock, not by
/// racing a real delay, so the run is the same each time.
/// </summary>
public sealed class PollingServiceLoopTests
{
    [Fact]
    public async Task TheLoop_WaitsForEnrollment_SurvivesFailedPolls_AndStopsCleanlyBetweenTwoPolls()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero));
        var api = Substitute.For<IServerApiClient>();
        var enrollment = Substitute.For<IEnrollmentService>();
        var enrollmentChecks = 0;
        enrollment.IsEnrolled.Returns(_ => Interlocked.Increment(ref enrollmentChecks) >= 3);
        var checksAtFirstPoll = -1;
        var polls = 0;
        var thirdPollAt = DateTimeOffset.MinValue;
        var thirdPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.GetPendingTasksAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            switch (Interlocked.Increment(ref polls))
            {
                case 1:
                    checksAtFirstPoll = Volatile.Read(ref enrollmentChecks);
                    throw new HttpRequestException("backend unreachable");
                case 2:
                    // A cancellation that is not the host stopping is one more failed poll.
                    throw new OperationCanceledException("request aborted");
                default:
                    thirdPollAt = time.GetUtcNow();
                    thirdPoll.TrySetResult();
                    return Task.FromResult(new List<PendingTaskDto>());
            }
        });
        var refusals = Substitute.For<IDeploymentBuildRefusalOutbox>();
        refusals.ReadAllAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<DeploymentBuildRefusalReport>());
        var health = new AgentRuntimeHealth(time);
        var container = Substitute.For<IContainerExecutor>();
        var options = Options.Create(new AetheusAgentOptions { PollingIntervalSeconds = 5, MaxConcurrentTasks = 2 });
        using var service = new PollingService(
            api, enrollment, [], [], container,
            new ContainerPipelineFunctionExecutor([], container, Path.Combine(Path.GetTempPath(), "aetheus-fn-loop"), _ => null),
            Substitute.For<IDockerStorageMaintenance>(), refusals,
            new AgentState { ServerId = 1, BearerToken = "token" }, health,
            Substitute.For<IAgentProcessRestarter>(), time, options, NullLogger<PollingService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await AdvanceUntil(time, () => thirdPoll.Task.IsCompleted);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(service.ExecuteTask);
        Assert.Equal(TaskStatus.RanToCompletion, service.ExecuteTask.Status);
        Assert.Equal(3, checksAtFirstPoll);
        Assert.Equal(3, Volatile.Read(ref polls));
        Assert.True(health.LastPollingSuccessAt >= thirdPollAt, "The successful poll did not refresh the watchdog.");
    }

    // Moves the fake clock forward in poll-sized steps until the loop has done what the test waits for;
    // each step lets the loop's continuation run before the next one.
    private static async Task AdvanceUntil(FakeTimeProvider time, Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "The polling loop did not reach the expected state.");
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
