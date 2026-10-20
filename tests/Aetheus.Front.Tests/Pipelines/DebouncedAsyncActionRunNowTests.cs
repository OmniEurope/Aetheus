// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Front.Tests.Pipelines;

/// <summary>Running the debounced reload now: it runs at once and completes only when the action has run,
/// supersedes a pending delayed run, serialises with a running one, reports a failure on the log instead
/// of throwing, and does nothing once disposed.</summary>
public sealed class DebouncedAsyncActionRunNowTests
{
    private readonly ILogger _logger = Substitute.For<ILogger>();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private DebouncedAsyncAction Create(Func<Task> action, TimeSpan? delay = null) =>
        new(action, _logger, "[test]", delay ?? TimeSpan.FromHours(1));

    private int Warnings() => _logger.ReceivedCalls().Count(call =>
        call.GetMethodInfo().Name == nameof(ILogger.Log) && call.GetArguments()[0] is LogLevel.Warning);

    [Fact]
    public async Task RunNow_RunsTheActionAtOnce_AndCompletesOnlyOnceItHasRun()
    {
        var runs = 0;
        await using var debounced = Create(async () => { await Task.Delay(30, _ct); runs++; });

        await debounced.RunNowAsync();

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task RunNow_SupersedesAPendingDelayedRun()
    {
        var runs = 0;
        await using var debounced = Create(() => { runs++; return Task.CompletedTask; });
        await debounced.ScheduleAsync();

        await debounced.RunNowAsync();
        await debounced.DisposeAsync();

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task RunNow_ReportsAFailingActionOnTheLog_AndStaysUsable()
    {
        var calls = 0;
        await using var debounced = Create(() =>
        {
            calls++;
            return calls == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        });

        await debounced.RunNowAsync();
        await debounced.RunNowAsync();

        Assert.Equal(2, calls);
        Assert.Equal(1, Warnings());
    }

    [Fact]
    public async Task RunNow_AfterDisposal_DoesNotRunTheAction()
    {
        var runs = 0;
        var debounced = Create(() => { runs++; return Task.CompletedTask; });
        await debounced.DisposeAsync();

        await debounced.RunNowAsync();

        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task ARunNowWaitingBehindARunningOne_IsAbandonedQuietlyWhenTheOwnerIsDisposed()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var debounced = Create(async () =>
        {
            Interlocked.Increment(ref runs);
            started.TrySetResult();
            await release.Task;
        });
        var first = debounced.RunNowAsync();
        await started.Task.WaitAsync(Aetheus.Front.Tests.TestDoubles.HangGuard.Limit, _ct);
        var waiting = debounced.RunNowAsync();
        var disposing = debounced.DisposeAsync().AsTask();

        await waiting.WaitAsync(Aetheus.Front.Tests.TestDoubles.HangGuard.Limit, _ct);
        release.SetResult();
        await first.WaitAsync(Aetheus.Front.Tests.TestDoubles.HangGuard.Limit, _ct);
        await disposing.WaitAsync(Aetheus.Front.Tests.TestDoubles.HangGuard.Limit, _ct);

        Assert.Equal(1, runs);
        Assert.Equal(0, Warnings());
    }
}
