// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Services;

/// <summary>
/// A periodic background worker. With a lease name it runs only in the leader instance, which is the
/// live blue-green colour (decision of 2026-10-02, <see cref="PostgresLeaderLease"/>).
/// </summary>
public abstract class PeriodicBackgroundService(
    TimeSpan interval,
    IPostgresLeaderLease? leaderLease = null,
    string? leaseName = null,
    TimeProvider? timeProvider = null) : BackgroundService
{
    /// <summary>The clock of the worker: the injected provider, or the system one.</summary>
    protected TimeProvider Clock { get; } = timeProvider ?? TimeProvider.System;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease is null || string.IsNullOrWhiteSpace(leaseName)
            ? RunLoopAsync(stoppingToken)
            : leaderLease.RunAsLeaderAsync(leaseName, RunLoopAsync, stoppingToken);

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval, Clock);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ExecuteIterationAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogIterationError(ex);
            }
        }
    }

    protected abstract Task ExecuteIterationAsync(CancellationToken ct);

    protected abstract void LogIterationError(Exception exception);
}
