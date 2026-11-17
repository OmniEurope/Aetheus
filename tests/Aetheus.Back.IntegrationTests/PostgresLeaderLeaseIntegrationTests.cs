// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

public sealed class PostgresLeaderLeaseIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan FastRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Complete acquisition retries the second backend must lose before the first one lets go.</summary>
    private const int RequiredContentions = 5;

    /// <summary>
    /// The lease's own account of its lost acquisitions (the debug line it writes each time another session holds
    /// the lock): <see cref="Reached"/> completes once <c>required</c> of them were told, so a test waits for the
    /// contention it means to observe instead of for a delay.
    /// </summary>
    private sealed class LostAcquisitions(int required) : ILogger<PostgresLeaderLease>
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public Task Reached => _reached.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("is held by another session", StringComparison.Ordinal)
                && Interlocked.Increment(ref _count) >= required)
                _reached.TrySetResult();
        }
    }

    [Fact]
    public async Task SerializedOperation_BlocksASecondBackendUntilTheFirstReleases()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = fixture.ConnectionString
            })
            .Build();
        var firstLease = new PostgresLeaderLease(configuration, NullLogger<PostgresLeaderLease>.Instance);
        var secondLease = new PostgresLeaderLease(configuration, NullLogger<PostgresLeaderLease>.Instance);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var first = firstLease.RunSerializedAsync(
            "integration:serialized-operation",
            async ct =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task.WaitAsync(ct);
            },
            timeout.Token);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), timeout.Token);

        var second = secondLease.RunSerializedAsync(
            "integration:serialized-operation",
            _ =>
            {
                secondEntered.SetResult();
                return Task.CompletedTask;
            },
            timeout.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
        Assert.False(secondEntered.Task.IsCompleted);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30), timeout.Token);
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task TwoInstances_NeverRunTheSameHostedWorkerConcurrently()
    {
        // A leader lease must release its own session lock before returning the connection to the
        // pool. Disable Npgsql's automatic reset so this test cannot pass merely because pool cleanup
        // happens to issue DISCARD ALL / pg_advisory_unlock_all on close.
        var firstConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            NoResetOnClose = true,
            ApplicationName = "aetheus-leader-first"
        }.ConnectionString;
        var secondConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            NoResetOnClose = true,
            ApplicationName = "aetheus-leader-second"
        }.ConnectionString;
        var firstConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = firstConnectionString
            })
            .Build();
        var secondConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = secondConnectionString
            })
            .Build();
        var firstLease = new PostgresLeaderLease(firstConfiguration, NullLogger<PostgresLeaderLease>.Instance);
        var secondAttempts = new LostAcquisitions(RequiredContentions);
        var secondLease = new PostgresLeaderLease(
            secondConfiguration,
            secondAttempts,
            FastRetryDelay);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstStop = new CancellationTokenSource();
        using var secondStop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var first = firstLease.RunAsLeaderAsync("integration:single-leader", async ct =>
        {
            firstEntered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, firstStop.Token);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);

        var second = secondLease.RunAsLeaderAsync("integration:single-leader", async ct =>
        {
            secondEntered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, secondStop.Token);

        // Observe contention across several complete acquisition retries: wait for the second backend to
        // report that it lost the race RequiredContentions times, not for a stretch of wall-clock time. A
        // window measured in milliseconds depends on timers firing on time, which a loaded machine does not
        // grant (a thread pool stalled for 900 ms leaves the window with a single observation, whatever the
        // product does). The test-only retry interval preserves the production control flow without spending
        // six wall-clock seconds per retry.
        await Task.WhenAny(secondAttempts.Reached, secondEntered.Task)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.False(
            secondEntered.Task.IsCompleted,
            "The second backend entered while the first still held the PostgreSQL lease.");

        firstStop.Cancel();
        await first.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        secondStop.Cancel();
        await second.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class FakeLiveProbe(InstanceServingState state) : ILiveInstanceProbe
    {
        public string InstanceId => "test";
        public InstanceServingState State { get; set; } = state;
    }

    private IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = fixture.ConnectionString })
        .Build();

    [Fact]
    public async Task ReserveColour_NeverTakesTheLease()
    {
        // Decision of 2026-10-02: the reserve colour runs the previous release and must not lead.
        var lease = new PostgresLeaderLease(Configuration(), NullLogger<PostgresLeaderLease>.Instance, FastRetryDelay,
            new FakeLiveProbe(InstanceServingState.Standby));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();

        var run = lease.RunAsLeaderAsync("integration:reserve-never-leads", _ =>
        {
            entered.TrySetResult();
            return Task.CompletedTask;
        }, stop.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(800), TestContext.Current.CancellationToken);
        Assert.False(entered.Task.IsCompleted, "A reserve colour entered leader work.");
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LeaderThatBecomesReserve_YieldsTheLeaseToTheLiveColour()
    {
        var oldColourProbe = new FakeLiveProbe(InstanceServingState.Live);
        var oldColour = new PostgresLeaderLease(Configuration(), NullLogger<PostgresLeaderLease>.Instance, FastRetryDelay,
            oldColourProbe, heartbeatInterval: TimeSpan.FromMilliseconds(100));
        var newColour = new PostgresLeaderLease(Configuration(), NullLogger<PostgresLeaderLease>.Instance, FastRetryDelay,
            new FakeLiveProbe(InstanceServingState.Live));
        var oldEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var old = oldColour.RunAsLeaderAsync("integration:reserve-yields", async ct =>
        {
            oldEntered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { oldCancelled.TrySetResult(); throw; }
        }, stop.Token);
        await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var current = newColour.RunAsLeaderAsync("integration:reserve-yields", async ct =>
        {
            newEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, stop.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        Assert.False(newEntered.Task.IsCompleted, "The new colour led while the old one still held the lease.");

        // Traffic moves to the new colour: the old one reads itself in reserve and hands the lease over.
        oldColourProbe.State = InstanceServingState.Standby;
        await oldCancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await newEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(old.IsCompleted, "The reserve colour must keep waiting for the lease, not stop.");

        stop.Cancel();
        await Task.WhenAll(old, current).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }
}
