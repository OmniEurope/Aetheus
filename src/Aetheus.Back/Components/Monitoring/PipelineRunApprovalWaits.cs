// SPDX-License-Identifier: EUPL-1.2
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Monitoring;

/// <summary>
/// R-33: how long a finished run spent waiting for approvals, so the "average duration" tile can leave the wait
/// for a person out (a deployment that needs a human approval otherwise reads hours of idle time as work).
/// Each approval waits from <c>RequestedAt</c> to <c>ResolvedAt</c>; a wait never resolved on a finished run
/// lasted until the run completed. Waits that overlap (two stages asking at once) are merged, never added twice,
/// and every wait is clamped to the run's own window.
/// </summary>
public static class PipelineRunApprovalWaits
{
    /// <summary>The time covered by the union of <paramref name="waits"/> inside [<paramref name="runStart"/>,
    /// <paramref name="runEnd"/>]. An open wait (null end) lasts until <paramref name="runEnd"/>.</summary>
    public static TimeSpan Total(
        IEnumerable<(DateTime Start, DateTime? End)> waits, DateTime runStart, DateTime runEnd)
    {
        var intervals = waits
            .Select(wait => (Start: Max(wait.Start, runStart), End: Min(wait.End ?? runEnd, runEnd)))
            .Where(interval => interval.End > interval.Start)
            .OrderBy(interval => interval.Start)
            .ToList();

        var total = TimeSpan.Zero;
        DateTime? currentStart = null;
        var currentEnd = DateTime.MinValue;
        foreach (var (start, end) in intervals)
        {
            if (currentStart is null)
            {
                (currentStart, currentEnd) = (start, end);
            }
            else if (start <= currentEnd)
            {
                currentEnd = Max(currentEnd, end);
            }
            else
            {
                total += currentEnd - currentStart.Value;
                (currentStart, currentEnd) = (start, end);
            }
        }

        return currentStart is null ? total : total + (currentEnd - currentStart.Value);
    }

    /// <summary>Fills <see cref="PipelineRunDto.ApprovalWaitSeconds"/> on list-projected runs. A run that has not
    /// finished keeps zero: the figure only feeds the average of finished runs.</summary>
    public static async Task<List<PipelineRunDto>> ApplyAsync(
        AppDbContext db, List<PipelineRunDto> runs, CancellationToken ct)
    {
        var finished = runs.Where(run => run.CompletedAt is not null).ToList();
        if (finished.Count == 0) return runs;

        var runIds = finished.Select(run => run.Id).ToList();
        var approvals = await db.PipelineApprovals.AsNoTracking()
            .Where(approval => runIds.Contains(approval.PipelineRunId))
            .Select(approval => new { approval.PipelineRunId, approval.RequestedAt, approval.ResolvedAt })
            .ToListAsync(ct).ConfigureAwait(false);
        if (approvals.Count == 0) return runs;

        var byRun = approvals
            .GroupBy(approval => approval.PipelineRunId)
            .ToDictionary(group => group.Key, group => group.Select(a => (a.RequestedAt, a.ResolvedAt)).ToList());
        return runs.Select(run =>
            run.CompletedAt is { } completedAt && byRun.TryGetValue(run.Id, out var waits)
                ? run with { ApprovalWaitSeconds = Total(waits, run.StartedAt, completedAt).TotalSeconds }
                : run).ToList();
    }

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;
}
