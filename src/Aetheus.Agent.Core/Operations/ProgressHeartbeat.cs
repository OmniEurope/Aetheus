// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Writes a progress line at a fixed interval while a long command runs, the job
/// <c>deploy/scripts/run-with-progress.sh</c> did for the shell steps: a test suite can stay silent for
/// minutes, and a silent step cannot be told apart from a stalled runner. The lines stop as soon as the
/// work ends, whatever its outcome; they never change it.
/// </summary>
internal static class ProgressHeartbeat
{
    internal static async Task<T> RunAsync<T>(
        Func<Task<T>> work, TimeSpan interval, Func<TimeSpan, Task> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var beats = BeatAsync(interval, report, Stopwatch.GetTimestamp(), stop.Token);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await beats.ConfigureAwait(false);
        }
    }

    private static async Task BeatAsync(
        TimeSpan interval, Func<TimeSpan, Task> report, long started, CancellationToken token)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                await report(Stopwatch.GetElapsedTime(started)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The work ended (or the task was cancelled): no more progress lines.
        }
    }
}
