// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Diagnostics;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-1, the fatals of 02:05: the host reboots every night and the database container stops under the backend. Each
/// test stops a real PostgreSQL the leader worker is using, one way a host takes it down, and reads what the lease says.
/// </summary>
public sealed class PostgresLeaderLeaseDatabaseStopIntegrationTests
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
            Lines.Enqueue(new Line(logLevel, formatter(state, exception) + (exception is null ? string.Empty : " || " + Chain(exception))));

        private static string Chain(Exception exception)
        {
            var parts = new List<string>();
            for (var current = exception; current is not null; current = current.InnerException)
                parts.Add(current.GetType().Name);
            return string.Join(" > ", parts);
        }
    }

    /// <summary>Runs the leader worker, takes the database down, then waits for <paramref name="told"/> to hold on the
    /// lines the lease wrote (an event, not a stretch of wall-clock time) within a generous deadline.</summary>
    private static async Task<Line[]> RunWhileTheDatabaseGoesAsync(
        Func<PostgreSqlContainer, Task> takeItDown, Func<Line[], bool> told, TimeSpan? outageWarningAfter = null)
    {
        await using var database = SharedPostgresContainer.RunLabels(new PostgreSqlBuilder(SharedPostgresContainer.Image)).Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        var connectionString = database.GetConnectionString();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString })
            .Build();
        var logger = new CapturingLogger<PostgresLeaderLease>();
        var lease = new PostgresLeaderLease(configuration, logger, FastRetryDelay, null, FastHeartbeat, outageWarningAfter);
        var working = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // The worker's own database use, as the hosted services do: short commands on connections of their own.
        var run = lease.RunAsLeaderAsync("integration:r1-database-stop", async ct =>
        {
            while (true)
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(ct);
                await using var command = new NpgsqlCommand("SELECT pg_sleep(0.05)", connection);
                await command.ExecuteNonQueryAsync(ct);
                working.TrySetResult();
            }
        }, stop.Token);

        await working.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await takeItDown(database);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!told([.. logger.Lines]) && DateTime.UtcNow < deadline)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        return [.. logger.Lines];
    }

    private static async Task DockerAsync(string verb, string containerId)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", [verb, containerId])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task ADatabaseKilledAtOnce_IsToldAtInformationLevel_NeverAFatalNorAWarning()
    {
        // Reproduced on 2026-11-03 before the fix: Critical, "NpgsqlException > EndOfStreamException", the nightly fatal.
        var lines = await RunWhileTheDatabaseGoesAsync(database => DockerAsync("kill", database.Id), IsTheRestartTold);

        AssertTheRestartIsOnlyInformation(lines);
    }

    [Fact]
    public async Task ADatabaseThatStaysDown_BecomesOneWarning_OnceTheOutageIsLongerThanARestart()
    {
        var lines = await RunWhileTheDatabaseGoesAsync(
            database => DockerAsync("kill", database.Id),
            lines => lines.Any(line => line.Level >= LogLevel.Warning),
            outageWarningAfter: TimeSpan.FromMilliseconds(800));

        var warning = Assert.Single(lines, line => line.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("still cannot reach the database after", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABackendStoppingWhileTheDatabaseIsDown_StopsCleanly_WithoutACanceledTask()
    {
        // Candidate #336 (run 3076): on Linux a downed database refuses at once, so the stop landed in the retry wait
        // and the lease threw TaskCanceledException. A long retry delay puts the stop in that wait every time.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2"
            })
            .Build();
        var logger = new CapturingLogger<PostgresLeaderLease>();
        var lease = new PostgresLeaderLease(configuration, logger, TimeSpan.FromMinutes(5), null, FastHeartbeat);
        using var stop = new CancellationTokenSource();

        var run = lease.RunAsLeaderAsync("integration:stop-during-outage", _ => Task.CompletedTask, stop.Token);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (logger.Lines.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(logger.Lines.IsEmpty, "the lease never reported the unreachable database");

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(run.IsCompletedSuccessfully);
    }

    private static bool IsTheRestartTold(Line[] lines) => lines.Any(line => line.Level == LogLevel.Information
        && line.Message.Contains("integration:r1-database-stop", StringComparison.Ordinal)
        && (line.Message.Contains("lost the database", StringComparison.Ordinal)
            || line.Message.Contains("administrator command", StringComparison.Ordinal)));

    private static void AssertTheRestartIsOnlyInformation(Line[] lines)
    {
        var told = string.Join(Environment.NewLine, lines.Select(line => $"{line.Level}: {line.Message}"));
        Assert.True(lines.All(line => line.Level < LogLevel.Warning), told);
        Assert.True(IsTheRestartTold(lines), "The restart was never told at Information level: " + told);
    }
}
