// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>R-2 (a), against a real PostgreSQL: a database that ends the leader's session on administrator command
/// (what a restart does) is told at information level, and a real failure of the worker stays critical.</summary>
// Serial: it terminates every session holding the advisory lock of a lease name, read from the
// cluster-wide pg_locks, so it would also cut the lease sessions of hosts in other classes' databases.
[Collection(SerialPostgresCollection.Name)]
public sealed class PostgresLeaderLeaseInterruptionLoggingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan FastRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FastHeartbeat = TimeSpan.FromMilliseconds(100);

    private sealed record Line(LogLevel Level, string Message);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<Line> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(new Line(logLevel, formatter(state, exception)));
    }

    private IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = fixture.ConnectionString })
        .Build();

    [Fact]
    public async Task ASessionTheDatabaseEnds_IsAnInformationLine_AndTheWorkerLeadsAgain()
    {
        const string leaseName = "integration:r2-database-ends-session";
        var logger = new CapturingLogger<PostgresLeaderLease>();
        var lease = new PostgresLeaderLease(Configuration(), logger, FastRetryDelay, null, FastHeartbeat);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var attempts = 0;

        var run = lease.RunAsLeaderAsync(leaseName, async ct =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                firstEntered.SetResult();
            else
                secondEntered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, stop.Token);

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await TerminateLeaseSessionAsync(leaseName);
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        var lines = logger.Lines.ToArray();
        Assert.DoesNotContain(lines, line => line.Level >= LogLevel.Warning && line.Message.Contains(leaseName, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Level == LogLevel.Information
            && line.Message.Contains(leaseName, StringComparison.Ordinal)
            && line.Message.Contains("ended its session on administrator command", StringComparison.Ordinal));

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ARealFailureOfTheWorker_StaysCritical_WithItsCause()
    {
        const string leaseName = "integration:r2-real-failure";
        var logger = new CapturingLogger<PostgresLeaderLease>();
        var lease = new PostgresLeaderLease(Configuration(), logger, FastRetryDelay, null, FastHeartbeat);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var attempts = 0;

        var run = lease.RunAsLeaderAsync(leaseName, async ct =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new InvalidOperationException("simulated reconciliation failure");
            secondEntered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, stop.Token);

        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.Contains(logger.Lines, line => line.Level == LogLevel.Critical
            && line.Message.Contains(leaseName, StringComparison.Ordinal)
            && line.Message.Contains("InvalidOperationException: simulated reconciliation failure", StringComparison.Ordinal));

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AStoppingBackend_EndsTheWorkerWithoutAnyWarning()
    {
        const string leaseName = "integration:r2-backend-stops";
        var logger = new CapturingLogger<PostgresLeaderLease>();
        var lease = new PostgresLeaderLease(Configuration(), logger, FastRetryDelay, null, FastHeartbeat);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = lease.RunAsLeaderAsync(leaseName, async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, stop.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logger.Lines, line => line.Level >= LogLevel.Warning);
    }

    /// <summary>Ends, from another connection, the session that holds the advisory lock of <paramref name="leaseName"/>:
    /// what the database does to every session when it restarts.</summary>
    private async Task TerminateLeaseSessionAsync(string leaseName)
    {
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_terminate_backend(l.pid)
            FROM pg_locks l
            WHERE l.locktype = 'advisory' AND l.granted
              AND l.classid::bigint = ((hashtextextended(@name, 0) >> 32) & 4294967295)
              AND l.objid::bigint = (hashtextextended(@name, 0) & 4294967295)
            """, admin);
        command.Parameters.AddWithValue("name", leaseName);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var terminated = 0;
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            terminated++;
        Assert.True(terminated > 0, "No session held the lease: nothing was terminated.");
    }
}
