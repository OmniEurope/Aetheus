// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Services;

/// <summary>
/// Covers the initial-connect retry loop (findings #2/#3: a backend down at first load, or a failed
/// manual reconnect, must still arm a retry - SignalR's automatic-reconnect only engages after a FIRST
/// successful connect) and the reconnect-preserves-tasks fix (finding #4: ReconnectAsync must not blank
/// the visible task list for the duration of the attempt) added to TaskTrackerService.
/// </summary>
public class TaskTrackerServiceReconnectTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    public TaskTrackerServiceReconnectTests() => BunitTestHelper.RegisterServices(this);

    private TaskTrackerService CreateService() => new(
        Services.GetRequiredService<ApiClient>(),
        Services.GetRequiredService<AuthStateProvider>(),
        Services.GetRequiredService<HubConnectionFactory>(),
        NullLogger<TaskTrackerService>.Instance);

    private static ServerTaskDto MakeTask(int id, TaskExecutionStatus status = TaskExecutionStatus.Running) =>
        new() { Id = id, Name = $"t-{id}", Status = status, CreatedAt = DateTime.UtcNow.AddSeconds(-id) };

    private static void CallUpsert(TaskTrackerService sut, ServerTaskDto task) =>
        typeof(TaskTrackerService).GetMethod("Upsert", Priv)!.Invoke(sut, [task]);

    private static object? GetPrivate(TaskTrackerService sut, string field) =>
        typeof(TaskTrackerService).GetField(field, Priv)!.GetValue(sut);

    // ── finding #2/#3: a failed initial connect arms a retry loop ─────────────

    [Fact]
    public async Task StartAsync_InitialConnectFails_SchedulesRetryLoop()
    {
        // The harness's HubConnectionFactory always fails to connect (ImmediateFailHandler), so this
        // exercises the real failure path exactly as a backend-down-at-login would.
        var sut = CreateService();
        var attempts = new List<int>();
        sut.OnReconnectAttempt += d => attempts.Add(d);

        await sut.StartAsync(Xunit.TestContext.Current.CancellationToken);

        // The hub stays assigned (matches the established "swallow and keep the hub" contract the
        // other TaskTrackerService test files already rely on) - but a retry loop must now be armed
        // instead of leaving the connection dead forever.
        Assert.NotNull(GetPrivate(sut, "_hub"));
        Assert.NotNull(GetPrivate(sut, "_startRetryCts"));
        // The loop announces its first (zero-delay) attempt synchronously before StartAsync returns.
        Assert.Contains(0, attempts);

        await sut.StopAsync(); // stop the background loop before the test ends
    }

    [Fact]
    public async Task StartAsync_SecondCallWhileRetrying_StaysIdempotent()
    {
        var sut = CreateService();
        await sut.StartAsync(Xunit.TestContext.Current.CancellationToken);
        var hubBefore = GetPrivate(sut, "_hub");

        // A second StartAsync call while the retry loop is still armed must remain a no-op (the
        // existing "_hub is not null -> return" guard), not spawn a competing retry loop.
        await sut.StartAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Same(hubBefore, GetPrivate(sut, "_hub"));

        await sut.StopAsync();
    }

    // ── finding #4: ReconnectAsync must not blank the visible list ────────────

    [Fact]
    public async Task ReconnectAsync_PreservesVisibleTasks_DuringTheAttempt()
    {
        var sut = CreateService();
        await sut.StartAsync(Xunit.TestContext.Current.CancellationToken); // creates a (never-connected) hub, arms the retry loop
        CallUpsert(sut, MakeTask(1));
        CallUpsert(sut, MakeTask(2));
        Assert.Equal(2, sut.Count);

        // ReconnectAsync tears down the old hub and starts a fresh (also failing) one. The visible
        // list must NOT be wiped to empty just because the attempt is in flight / fails again.
        await sut.ReconnectAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(2, sut.Count);

        await sut.StopAsync();
    }

    [Fact]
    public async Task StopAsync_ClearTasksFalse_TearsDownHubButKeepsTasks()
    {
        var sut = CreateService();
        await sut.StartAsync(Xunit.TestContext.Current.CancellationToken);
        CallUpsert(sut, MakeTask(10));
        Assert.Equal(1, sut.Count);

        await sut.StopAsync(clearTasks: false);

        Assert.Null(GetPrivate(sut, "_hub")); // hub genuinely torn down
        Assert.Equal(1, sut.Count);           // but the visible list survives
    }

    [Fact]
    public async Task StopAsync_Default_StillClearsTasks()
    {
        // Regression guard: the new overload's default must match the pre-existing logout/dispose
        // contract (clear on stop) - only ReconnectAsync opts into clearTasks: false.
        var sut = CreateService();
        await sut.StartAsync(Xunit.TestContext.Current.CancellationToken);
        CallUpsert(sut, MakeTask(20));
        Assert.Equal(1, sut.Count);

        await sut.StopAsync();

        Assert.Equal(0, sut.Count);
    }

    [Fact]
    public async Task RetryLoop_FailsTwiceThenSucceeds_StopsAndPublishesSeed()
    {
        var connectCalls = 0;
        var seededTaskIds = new List<int>();
        var announcedDelays = new List<int>();

        var connected = await TaskTrackerService.RetryUntilConnectedAsync(
            _ =>
            {
                connectCalls++;
                if (connectCalls < 3)
                    return Task.FromResult(false);
                seededTaskIds.Add(42);
                return Task.FromResult(true);
            },
            [TimeSpan.Zero],
            announcedDelays.Add,
            () => true,
            Xunit.TestContext.Current.CancellationToken);

        Assert.True(connected);
        Assert.Equal(3, connectCalls);
        Assert.Equal([0, 0, 0], announcedDelays);
        Assert.Equal([42], seededTaskIds);
    }

    // ── wake: the browser says the network is back ────────────────────────────

    [Fact]
    public async Task RetryLoop_Wake_CutsAnHourLongBackoffShort()
    {
        // Without the wake the loop would sleep a full hour before its first attempt; the bounded
        // wait below turns that into a failure instead of a hung test.
        var connectCalls = 0;

        var connected = await TaskTrackerService.RetryUntilConnectedAsync(
            _ => { connectCalls++; return Task.FromResult(true); },
            [TimeSpan.FromHours(1)],
            _ => { },
            () => true,
            Xunit.TestContext.Current.CancellationToken,
            armWake: () => Task.CompletedTask)
            .WaitAsync(HangGuard.Limit, Xunit.TestContext.Current.CancellationToken);

        Assert.True(connected);
        Assert.Equal(1, connectCalls);
    }

    [Fact]
    public async Task RetryLoop_WakeDuringFailedAttempt_CutsTheNextBackoffShort()
    {
        var sut = CreateService();
        var connectCalls = 0;
        var waits = 0;

        Task ArmWake() => ++waits == 1
            ? Task.CompletedTask
            : (Task)typeof(TaskTrackerService).GetMethod("ArmWake", Priv)!.Invoke(sut, null)!;

        var connected = await TaskTrackerService.RetryUntilConnectedAsync(
            _ =>
            {
                if (++connectCalls == 1)
                {
                    sut.RequestImmediateReconnect();
                    return Task.FromResult(false);
                }
                return Task.FromResult(true);
            },
            [TimeSpan.FromHours(1)],
            _ => { },
            () => true,
            Xunit.TestContext.Current.CancellationToken,
            armWake: ArmWake)
            .WaitAsync(HangGuard.Limit, Xunit.TestContext.Current.CancellationToken);

        Assert.True(connected);
        Assert.Equal(2, connectCalls);
    }

    [Fact]
    public async Task RetryLoop_Wake_DoesNotRestartTheScheduleFromZero()
    {
        // A wake that does not actually fix connectivity must keep climbing the schedule, or a flapping
        // "online" event would hammer a backend that is genuinely down at the zero-delay step.
        var connectCalls = 0;
        var announcedDelays = new List<int>();

        var connected = await TaskTrackerService.RetryUntilConnectedAsync(
            _ => Task.FromResult(++connectCalls == 4),
            [TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)],
            announcedDelays.Add,
            () => true,
            Xunit.TestContext.Current.CancellationToken,
            armWake: () => Task.CompletedTask)
            .WaitAsync(HangGuard.Limit, Xunit.TestContext.Current.CancellationToken);

        Assert.True(connected);
        Assert.Equal([0, 5, 30, 30], announcedDelays);
    }

    [Fact]
    public void RequestImmediateReconnect_WithNothingRetrying_IsHarmless()
    {
        var sut = CreateService();

        var ex = Record.Exception(sut.RequestImmediateReconnect);

        Assert.Null(ex);
    }

    // ── what the connection-lost dialog is told ───────────────────────────────

    [Fact]
    public async Task StartAsync_ConnectFails_ExposesTheReasonForTheDialog()
    {
        // The harness hub always fails to connect, exactly like a backend that is down at login.
        var sut = CreateService();

        await sut.StartAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(sut.LastFailureReason));

        await sut.StopAsync();
    }

    [Fact]
    public async Task ReconnectAsync_SessionGone_SaysSoAndLeavesTheConnectionAlone()
    {
        var sut = CreateService();
        await sut.StartAsync(Xunit.TestContext.Current.CancellationToken);
        var hubBefore = GetPrivate(sut, "_hub");
        await Services.GetRequiredService<AuthStateProvider>().LogoutAsync();

        var outcome = await sut.ReconnectAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(TaskTrackerService.ReconnectOutcome.SessionExpired, outcome);
        Assert.Null(sut.LastFailureReason);
        Assert.Same(hubBefore, GetPrivate(sut, "_hub"));

        await sut.StopAsync();
    }

    [Fact]
    public async Task StartAsync_ConnectAttemptNeverAnswers_GivesUpAtItsDeadlineAndKeepsRetrying()
    {
        // A stalled negotiation must time out and expose the failure and retry countdown.
        var auth = Services.GetRequiredService<AuthStateProvider>();
        var clock = new ObservedFakeClock(DateTimeOffset.UnixEpoch);
        var sut = new TaskTrackerService(
            Services.GetRequiredService<ApiClient>(),
            auth,
            new BunitTestHelper.BlockingHubConnectionFactory(Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), auth),
            NullLogger<TaskTrackerService>.Instance)
        {
            ConnectAttemptTimeout = TimeSpan.FromMilliseconds(200),
            Clock = clock
        };
        var attempts = new System.Collections.Concurrent.ConcurrentQueue<int>();
        sut.OnReconnectAttempt += attempts.Enqueue;

        // The attempt deadlines and the backoff run on the simulated clock: each time the service has
        // armed its next timer (an attempt waiting on its deadline, or a backoff), the clock is moved past it.
        var starting = sut.StartAsync(Xunit.TestContext.Current.CancellationToken);
        while (attempts.Count < 3)
        {
            await clock.WhenAnyPendingAsync();
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        await HangGuard.AwaitAsync(starting, "the first attempt giving up");

        // The third announcement follows the second retry's failure: that retry was a real attempt on
        // the same hub (not a "connection is not Disconnected" refusal) and it timed out too.
        Assert.True(attempts.Count >= 3, $"expected the loop to keep scheduling attempts, saw {attempts.Count}");
        Assert.Contains("did not answer", sut.LastFailureReason, StringComparison.Ordinal);

        await sut.StopAsync();
    }

    [Fact]
    public async Task ReconnectAsync_ConnectStillFails_ReportsRetrying()
    {
        var sut = CreateService();
        await sut.StartAsync(Xunit.TestContext.Current.CancellationToken);

        var outcome = await sut.ReconnectAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(TaskTrackerService.ReconnectOutcome.Retrying, outcome);

        await sut.StopAsync();
    }
}
