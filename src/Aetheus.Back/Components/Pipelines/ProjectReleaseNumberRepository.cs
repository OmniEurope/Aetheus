// SPDX-License-Identifier: EUPL-1.2
using System.Data;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary> The per-project, per-series release counter behind <c>BUILD_RELEASENUMBER</c>, the last
/// number of the application version. Same reservation contract as <see
/// cref="PipelineBuildNumberRepository"/>: the work shares one transaction whose row lock on the
/// project makes two concurrent releases of one project take two numbers, never the same one. It
/// implements <see cref="IPipelineReleaseNumberRepository"/> (audit AUD-07), whose members name the
/// queries a run needs, and <see cref="IProjectReleaseSeriesRepository"/>, the administration of the
/// same counters.
///
/// A series (<c>major.minor</c>) counts from 0 on its own. Every number handed out is written to
/// <see cref="ProjectReleaseNumberAllocation"/>, which outlives the run that took it: that is what
/// lets an administrator set the next number back without ever giving a version twice. </summary>
internal sealed class ProjectReleaseNumberRepository(AppDbContext db, TimeProvider timeProvider)
    : IPipelineReleaseNumberRepository, IProjectReleaseSeriesRepository
{
    /// <summary>Takes the next release number of the series (0 for its first release), or null when the project no longer exists.</summary>
    public async Task<int?> AllocateNextAsync(int projectId, string series, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(series);
        return await InLockedProjectAsync(projectId, bumpCounter: true, async () =>
        {
            var row = await FindSeriesAsync(projectId, series, ct).ConfigureAwait(false);
            var number = await FirstFreeNumberAsync(projectId, series, row?.NextNumber ?? 0, ct).ConfigureAwait(false);
            db.ProjectReleaseNumberAllocations.Add(new ProjectReleaseNumberAllocation
            {
                ProjectId = projectId,
                Series = series,
                Number = number,
                AllocatedAt = timeProvider.GetUtcNow().UtcDateTime
            });
            UpsertSeries(row, projectId, series, number + 1);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (int?)number;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The last release number the series handed out (0 before its first release), or null when the project does not exist.</summary>
    public async Task<int?> GetLatestAsync(int projectId, string series, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(series);
        var projectExists = await db.Projects.AsNoTracking().AnyAsync(project => project.Id == projectId, ct).ConfigureAwait(false);
        if (!projectExists) return null;
        var last = await db.ProjectReleaseNumberAllocations.AsNoTracking()
            .Where(allocation => allocation.ProjectId == projectId && allocation.Series == series)
            .MaxAsync(allocation => (int?)allocation.Number, ct).ConfigureAwait(false);
        return last ?? 0;
    }

    /// <summary>
    /// The release number of a run, or null when it has none. Read for the run that started another
    /// (UPSTREAM_RUN_ID), which only the engine writes: a launch parameter of that name is refused.
    /// </summary>
    public async Task<ReleaseNumberAssignment?> GetRunReleaseAsync(int runId, CancellationToken ct)
    {
        var run = await db.PipelineRuns.AsNoTracking()
            .Where(candidate => candidate.Id == runId && candidate.ReleaseNumber != null)
            .Select(candidate => new { candidate.ReleaseSeries, candidate.ReleaseNumber })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return run is null ? null : new ReleaseNumberAssignment(run.ReleaseSeries, run.ReleaseNumber!.Value);
    }

    public async Task<List<ProjectReleaseSeriesDto>?> GetSeriesAsync(int projectId, CancellationToken ct)
    {
        var projectExists = await db.Projects.AsNoTracking().AnyAsync(project => project.Id == projectId, ct).ConfigureAwait(false);
        if (!projectExists) return null;
        var rows = await db.ProjectReleaseSeries.AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .Select(row => new ProjectReleaseSeriesDto { Series = row.Series, NextNumber = row.NextNumber })
            .ToListAsync(ct).ConfigureAwait(false);
        // Newest series first, numerically: "1.10" is above "1.9", which a string order would invert.
        return [.. rows
            .OrderByDescending(row => SeriesKey(row.Series).Major)
            .ThenByDescending(row => SeriesKey(row.Series).Minor)];
    }

    public async Task<SetNextReleaseNumberOutcome> SetNextAsync(
        int projectId, string series, int number, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(series);
        var outcome = await InLockedProjectAsync(projectId, bumpCounter: false, async () =>
        {
            var used = await db.ProjectReleaseNumberAllocations.AsNoTracking()
                .AnyAsync(allocation => allocation.ProjectId == projectId
                    && allocation.Series == series && allocation.Number == number, ct).ConfigureAwait(false);
            if (used) return (SetNextReleaseNumberOutcome?)SetNextReleaseNumberOutcome.NumberAlreadyUsed;
            var row = await FindSeriesAsync(projectId, series, ct).ConfigureAwait(false);
            UpsertSeries(row, projectId, series, number);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return SetNextReleaseNumberOutcome.Set;
        }, ct).ConfigureAwait(false);
        return outcome ?? SetNextReleaseNumberOutcome.ProjectNotFound;
    }

    /// <summary>
    /// Runs <paramref name="work"/> once the project row is locked, inside one transaction (joined
    /// when the caller has one, as <see cref="PipelineBuildNumberRepository"/> does). The lock is what
    /// serializes the allocations of one project: the series row they read and rewrite cannot change
    /// under them, and two concurrent first releases of a series cannot both insert its row. Null
    /// when the project is gone.
    /// </summary>
    private async Task<T?> InLockedProjectAsync<T>(
        int projectId, bool bumpCounter, Func<Task<T?>> work, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            // InMemory (unit tests): no bulk update and no row locks.
            var tracked = await db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, ct).ConfigureAwait(false);
            if (tracked is null) return default;
            if (bumpCounter) tracked.ReleaseCounter++;
            return await work().ConfigureAwait(false);
        }

        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false)
            : null;
        // The UPDATE takes the row lock and holds it until the transaction commits. Setting the counter
        // to itself is the lock without the bump, for the one caller that hands nothing out.
        var rows = bumpCounter
            ? await db.Projects.Where(project => project.Id == projectId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    project => project.ReleaseCounter, project => project.ReleaseCounter + 1), ct).ConfigureAwait(false)
            : await db.Projects.Where(project => project.Id == projectId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    project => project.ReleaseCounter, project => project.ReleaseCounter), ct).ConfigureAwait(false);
        T? result = default;
        if (rows > 0) result = await work().ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);

        // ExecuteUpdateAsync bypasses the change tracker: a tracked Project must not write back a stale counter.
        var trackedProject = db.ChangeTracker.Entries<Project>().FirstOrDefault(entry => entry.Entity.Id == projectId);
        if (trackedProject is not null)
            trackedProject.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        return result;
    }

    private Task<ProjectReleaseSeries?> FindSeriesAsync(int projectId, string series, CancellationToken ct) =>
        db.ProjectReleaseSeries.FirstOrDefaultAsync(row => row.ProjectId == projectId && row.Series == series, ct);

    private void UpsertSeries(ProjectReleaseSeries? row, int projectId, string series, int nextNumber)
    {
        if (row is null)
            db.ProjectReleaseSeries.Add(new ProjectReleaseSeries { ProjectId = projectId, Series = series, NextNumber = nextNumber });
        else
            row.NextNumber = nextNumber;
    }

    /// <summary>The first number from <paramref name="start"/> that no run of the series took: an
    /// administrator may have set the counter back below numbers already handed out.</summary>
    private async Task<int> FirstFreeNumberAsync(int projectId, string series, int start, CancellationToken ct)
    {
        var taken = await db.ProjectReleaseNumberAllocations.AsNoTracking()
            .Where(allocation => allocation.ProjectId == projectId && allocation.Series == series && allocation.Number >= start)
            .Select(allocation => allocation.Number)
            .OrderBy(number => number)
            .ToListAsync(ct).ConfigureAwait(false);
        var candidate = start;
        foreach (var number in taken)
        {
            if (number != candidate) break;
            candidate++;
        }
        return candidate;
    }

    private static (int Major, int Minor) SeriesKey(string series)
    {
        var parts = series.Split('.');
        return parts.Length == 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
            ? (major, minor)
            : (-1, -1);
    }

    Task<int?> IPipelineReleaseNumberRepository.AllocateNextReleaseNumberAsync(int projectId, string series, CancellationToken ct)
        => AllocateNextAsync(projectId, series, ct);

    Task<int?> IPipelineReleaseNumberRepository.GetLatestReleaseNumberAsync(int projectId, string series, CancellationToken ct)
        => GetLatestAsync(projectId, series, ct);
}
