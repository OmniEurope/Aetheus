// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Services;

/// <summary>
/// A periodic background worker. It runs only in the leader instance, which is the live blue-green
/// colour (decision of 2026-10-02, <see cref="PostgresLeaderLease"/>).
/// </summary>
public abstract class PeriodicBackgroundService(
    TimeSpan interval,
    IPostgresLeaderLease leaderLease,
    string leaseName,
    TimeProvider? timeProvider = null) : LeaderBackgroundService(leaderLease, leaseName)
{
    /// <summary>The clock of the worker: the injected provider, or the system one.</summary>
    protected TimeProvider Clock { get; } = timeProvider ?? TimeProvider.System;

    protected sealed override Task RunLeaderLoopAsync(CancellationToken stoppingToken) =>
        RunLoopAsync(stoppingToken);

    /// <summary>The loop. A worker that wakes on something besides its period overrides it and calls <see cref="RunIterationAsync"/> itself.</summary>
    protected virtual async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval, Clock);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!await RunIterationAsync(stoppingToken).ConfigureAwait(false)) break;
        }
    }

    /// <summary>One iteration, its failure logged and never fatal. False once the worker is stopping.</summary>
    protected async Task<bool> RunIterationAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Reported to the slow-request diagnostics (R1-40): this iteration may be what a pause overlapped.
            using var tracked = BackgroundWorkTracker.Shared.Begin(GetType().Name);
            await ExecuteIterationAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogIterationError(ex);
        }

        return true;
    }

    protected abstract Task ExecuteIterationAsync(CancellationToken ct);

    protected abstract void LogIterationError(Exception exception);
}
