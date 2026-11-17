// SPDX-License-Identifier: EUPL-1.2
using System.Data;
using System.Diagnostics;
using Npgsql;

namespace Aetheus.Back.Services;

public interface IPostgresLeaderLease
{
    Task RunAsLeaderAsync(
        string leaseName,
        Func<CancellationToken, Task> leaderWork,
        CancellationToken stoppingToken);

    Task RunSerializedAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Holds a PostgreSQL session advisory lock while one background worker is active. During
/// blue-green overlap only one backend therefore schedules, reconciles, or prunes data.
/// A heartbeat detects a severed lease connection and cancels the leader work fail-closed.
/// <para>
/// Only the live colour leads (decision of 2026-10-02). A reserve colour, which
/// <see cref="ILiveInstanceProbe"/> reports as <see cref="InstanceServingState.Standby"/>, never
/// takes a lease, and a leader that finds itself in reserve for <see cref="StandbyBeatsBeforeYield"/>
/// heartbeats in a row cancels its work and releases the lease for the live colour. An
/// <see cref="InstanceServingState.Unknown"/> state changes nothing.
/// </para>
/// </summary>
public sealed class PostgresLeaderLease : IPostgresLeaderLease
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a database that cannot be reached is told at information level before it becomes a warning
    /// (R-1): the host restarts nightly and its database is back within a minute or two, which is not an incident.</summary>
    internal static readonly TimeSpan OutageWarningAfter = TimeSpan.FromMinutes(2);

    /// <summary>Consecutive reserve readings before a leader yields: one switch can be in flight.</summary>
    internal const int StandbyBeatsBeforeYield = 3;

    private readonly IConfiguration _configuration;
    private readonly ILogger<PostgresLeaderLease> _logger;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _outageWarningAfter;
    private readonly ILiveInstanceProbe? _liveProbe;

    public PostgresLeaderLease(
        IConfiguration configuration,
        ILogger<PostgresLeaderLease> logger,
        ILiveInstanceProbe? liveProbe = null)
        : this(configuration, logger, RetryDelay, liveProbe)
    {
    }

    internal PostgresLeaderLease(
        IConfiguration configuration,
        ILogger<PostgresLeaderLease> logger,
        TimeSpan retryDelay,
        ILiveInstanceProbe? liveProbe = null,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? outageWarningAfter = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryDelay, TimeSpan.Zero);
        _configuration = configuration;
        _logger = logger;
        _retryDelay = retryDelay;
        _liveProbe = liveProbe;
        _heartbeatInterval = heartbeatInterval ?? HeartbeatInterval;
        _outageWarningAfter = outageWarningAfter ?? OutageWarningAfter;
    }

    private bool InReserve => _liveProbe?.State == InstanceServingState.Standby;

    public async Task RunSerializedAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(operation);

        var connectionString = _configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is required for operation locks.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var acquire = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtextextended(@name, 0))", connection))
        {
            acquire.Parameters.AddWithValue("name", operationName);
            await acquire.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseLeaseAsync(connection, operationName, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RunAsLeaderAsync(
        string leaseName,
        Func<CancellationToken, Task> leaderWork,
        CancellationToken stoppingToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseName);
        ArgumentNullException.ThrowIfNull(leaderWork);

        var connectionString = _configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is required for hosted-service leader leases.");

        LeaderInterruption? lastInterruption = null;
        var outage = new DatabaseOutage();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A reserve colour runs the previous release: it never competes for leadership.
                if (InReserve)
                {
                    await Task.Delay(_retryDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(stoppingToken).ConfigureAwait(false);

                if (!await TryAcquireAsync(connection, leaseName, stoppingToken).ConfigureAwait(false))
                {
                    _logger.LogDebug(
                        "PostgreSQL leader lease {LeaseName} is held by another session; trying again in {RetryDelay}", leaseName, _retryDelay);
                    await Task.Delay(_retryDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                lastInterruption = null;
                outage = new DatabaseOutage();
                _logger.LogInformation("Acquired PostgreSQL leader lease {LeaseName}", leaseName);
                using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var work = leaderWork(leaseLost.Token);
                try
                {
                    // Traffic moved to another colour: the lease was handed over, wait for it back.
                    if (await HoldWhileWorkingAsync(connection, leaseName, work, leaseLost, stoppingToken).ConfigureAwait(false))
                        continue;
                    return;
                }
                catch
                {
                    leaseLost.Cancel();
                    try { await work.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (leaseLost.IsCancellationRequested) { }
                    throw;
                }
                finally
                {
                    await ReleaseLeaseAsync(connection, leaseName, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var interruption = LeaderInterruptionClassifier.Classify(ex, stoppingToken);
                if (interruption == LeaderInterruption.DatabaseUnavailable)
                    outage.StartedAt ??= Stopwatch.GetTimestamp();
                LogInterruption(leaseName, ex, interruption, repeated: lastInterruption == interruption, outage);
                lastInterruption = interruption;
                // The backend is stopping: there is nothing to restart.
                if (interruption == LeaderInterruption.HostStopping)
                    return;
                // Stopping while the database is down lands in this wait, outside the try above: it is a clean stop.
                try { await Task.Delay(_retryDelay, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }

    /// <summary>
    /// A worker that restarts because the backend or the database stopped is not a failure (decision of
    /// 2026-10-29): it is told at information level with its reason. A database that cannot be reached is a
    /// warning, said once per outage (the retries that follow are debug lines). Anything else stays critical,
    /// with the exception's type and message in the line so an export carries the cause.
    /// </summary>
    private void LogInterruption(
        string leaseName, Exception ex, LeaderInterruption interruption, bool repeated, DatabaseOutage outage)
    {
        var retrySeconds = (int)_retryDelay.TotalSeconds;
        switch (interruption)
        {
            case LeaderInterruption.HostStopping:
                _logger.LogInformation(
                    "Leader worker {LeaseName} stopped: the backend is stopping ({Reason})", leaseName, ex.GetType().Name);
                break;
            case LeaderInterruption.DatabaseStoppedSession:
                _logger.LogInformation(
                    "Leader worker {LeaseName} restarts in {RetrySeconds} s: the database server ended its session on administrator command (a restart or maintenance of the database)",
                    leaseName, retrySeconds);
                break;
            case LeaderInterruption.DatabaseUnavailable when repeated:
                if (!outage.Warned && Stopwatch.GetElapsedTime(outage.StartedAt!.Value) >= _outageWarningAfter)
                {
                    outage.Warned = true;
                    _logger.LogWarning(
                        "Leader worker {LeaseName} still cannot reach the database after {OutageSeconds} s: {Reason}",
                        leaseName, (int)Stopwatch.GetElapsedTime(outage.StartedAt!.Value).TotalSeconds, ex.Message);
                }
                else
                {
                    _logger.LogDebug(
                        "Leader worker {LeaseName} still cannot reach the database: {Reason}", leaseName, ex.Message);
                }

                break;
            case LeaderInterruption.DatabaseUnavailable:
                _logger.LogInformation(
                    "Leader worker {LeaseName} lost the database ({ExceptionType}: {Reason}); it retries every {RetrySeconds} s until the database answers, and a warning follows if it is still down after {WarnAfterSeconds} s",
                    leaseName, ex.GetType().Name, ex.Message, retrySeconds, (int)_outageWarningAfter.TotalSeconds);
                break;
            default:
                _logger.LogCritical(
                    ex,
                    "Leader worker {LeaseName} failed ({ExceptionType}: {Reason}); its lease was released and the worker will restart without stopping the backend",
                    leaseName, ex.GetType().Name, ex.Message);
                break;
        }
    }

    /// <summary>One leader worker's current database outage: when it began (a <see cref="Stopwatch"/> timestamp) and
    /// whether its warning was told.</summary>
    private sealed class DatabaseOutage
    {
        public long? StartedAt { get; set; }

        public bool Warned { get; set; }
    }

    /// <summary>
    /// Takes the named advisory lock for the caller's own transaction (<c>pg_advisory_xact_lock</c>, audit E4):
    /// PostgreSQL releases it at that transaction's commit or rollback, so it covers work that joins an outer
    /// transaction committed after the caller returns, which <see cref="RunSerializedAsync"/>'s own connection
    /// cannot. It never outlives the transaction, so no failed unlock can leave it on a pooled connection.
    /// </summary>
    internal static async Task LockForTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string lockName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);
        await using var acquire = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@name, 0))", connection, transaction);
        acquire.Parameters.AddWithValue("name", lockName);
        await acquire.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> TryAcquireAsync(NpgsqlConnection connection, string leaseName, CancellationToken ct)
    {
        await using var acquire = new NpgsqlCommand(
            "SELECT pg_try_advisory_lock(hashtextextended(@name, 0))", connection);
        acquire.Parameters.AddWithValue("name", leaseName);
        return (bool)(await acquire.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? false);
    }

    /// <summary>
    /// Heartbeats the lease while the leader work runs. Returns false once the work completed (its
    /// outcome rethrown), true when this instance found itself in reserve and handed the lease over
    /// after cancelling the work.
    /// </summary>
    private async Task<bool> HoldWhileWorkingAsync(
        NpgsqlConnection connection, string leaseName, Task work, CancellationTokenSource leaseLost, CancellationToken stoppingToken)
    {
        var reserveBeats = 0;
        while (!work.IsCompleted && reserveBeats < StandbyBeatsBeforeYield)
        {
            await Task.Delay(_heartbeatInterval, stoppingToken).ConfigureAwait(false);
            await using var heartbeat = new NpgsqlCommand("SELECT 1", connection);
            await heartbeat.ExecuteScalarAsync(stoppingToken).ConfigureAwait(false);
            reserveBeats = InReserve ? reserveBeats + 1 : 0;
        }

        if (work.IsCompleted)
        {
            await work.ConfigureAwait(false);
            return false;
        }

        _logger.LogInformation("Yielding PostgreSQL leader lease {LeaseName}: this instance is a reserve colour", leaseName);
        await leaseLost.CancelAsync().ConfigureAwait(false);
        try { await work.ConfigureAwait(false); }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested) { }
        return true;
    }

    private async Task ReleaseLeaseAsync(NpgsqlConnection connection, string leaseName, CancellationToken stoppingToken)
    {
        try
        {
            if (connection.State != ConnectionState.Open)
            {
                NpgsqlConnection.ClearPool(connection);
                return;
            }

            using var releaseTimeout = new CancellationTokenSource(ReleaseTimeout);
            await using var release = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtextextended(@name, 0))", connection);
            release.Parameters.AddWithValue("name", leaseName);
            var released = (bool)(await release.ExecuteScalarAsync(releaseTimeout.Token).ConfigureAwait(false) ?? false);
            if (released)
                _logger.LogInformation("Released PostgreSQL leader lease {LeaseName}", leaseName);
            else
                _logger.LogWarning("PostgreSQL session did not own leader lease {LeaseName} during release", leaseName);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            // Returning a session with an advisory lock to the pool can wedge leadership indefinitely.
            // If the explicit unlock cannot be proven, invalidate the pool so this physical session is
            // closed on disposal and PostgreSQL releases all of its session-scoped advisory locks.
            NpgsqlConnection.ClearPool(connection);
            // A session the database ended, or a database that cannot be reached, holds no lock any more: the
            // server released it with the session. That is an expected end, said at information level; any other
            // reason a release failed stays a warning.
            if (LeaderInterruptionClassifier.Classify(ex, stoppingToken) == LeaderInterruption.Failure)
                _logger.LogWarning(ex, "Could not explicitly release PostgreSQL leader lease {LeaseName}; invalidated its connection pool", leaseName);
            else
                _logger.LogInformation(
                    "PostgreSQL leader lease {LeaseName} needed no explicit release: its database session had already ended ({ExceptionType}); invalidated its connection pool",
                    leaseName, ex.GetType().Name);
        }
    }
}
