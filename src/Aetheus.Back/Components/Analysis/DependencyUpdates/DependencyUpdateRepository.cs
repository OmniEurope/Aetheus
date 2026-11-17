// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.DependencyUpdates;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>One open match an update may answer: its package as the inventory holds it, and where.</summary>
internal sealed record DependencyUpdateCandidate(
    long MatchId, int OrganizationId, int ProjectId, int RepositoryId, string Ecosystem, string PackageName, string Version,
    string SourcePath, string Advisory, string FixedVersion);

/// <summary>A pipeline of the project that may run on an update's branch.</summary>
internal sealed record DependencyUpdatePipeline(
    int Id, string Name, string? TemplateReferenceName, int? SourceRepositoryId, PipelineTriggerType TriggerType, string YamlDefinition);

/// <summary>A pipeline and its owner, the user an automated run of it is authorized against (F-EXEC-1b).</summary>
internal sealed record DependencyUpdateOwnedPipeline(int Id, string? Owner);

/// <summary>A run found for a pipeline on an update's branch and commit.</summary>
internal sealed record DependencyUpdateRunRow(int Id, PipelineStatus Status);

/// <summary>The rows behind the automatic dependency updates (PLAN-007).</summary>
internal sealed class DependencyUpdateRepository(AppDbContext db)
{
    /// <summary>The open matches a fixed version exists for, of the projects whose setting is on, in an ecosystem this lot writes,
    /// on an item an internal repository of the project holds. A batch after <paramref name="afterMatchId"/>, by id.</summary>
    public Task<List<DependencyUpdateCandidate>> GetCandidatesAsync(
        IReadOnlyCollection<string> ecosystems, long afterMatchId, int take, CancellationToken ct)
    {
        var names = ecosystems.ToArray();
        return db.VulnerabilityMatches.AsNoTracking()
            .Where(match => match.Id > afterMatchId
                            && match.ClosedAt == null
                            && match.Status == VulnerabilityMatchStatus.Affected
                            && match.FixedVersion != null
                            && names.Contains(match.Ecosystem)
                            && match.Project.DependencyUpdatesEnabled
                            && match.Project.DeletionRequestedAt == null
                            && match.ProjectInventoryItem != null
                            && match.ProjectInventoryItem.SourcePath != null
                            && match.ProjectInventoryItem.ProjectInventory.GitInternalRepoId != null)
            .OrderBy(match => match.Id)
            .Take(take)
            .Select(match => new DependencyUpdateCandidate(
                match.Id, match.OrganizationId, match.ProjectId, match.ProjectInventoryItem!.ProjectInventory.GitInternalRepoId!.Value,
                match.Ecosystem, match.PackageName, match.Version, match.ProjectInventoryItem.SourcePath!,
                match.Advisory.PrimaryCveId ?? match.Advisory.ExternalId, match.FixedVersion!))
            .ToListAsync(ct);
    }

    /// <summary>Whether an update of this package from this version already has its branch: one branch per package and version, whatever
    /// target a later pass computes. A major update (R-2) is kept apart: <paramref name="major"/> false counts the patch and minor
    /// branches only, <paramref name="major"/> true the major ones only, so a major branch never stands in for the patch or minor one
    /// a later release makes possible, and the reverse.</summary>
    public Task<bool> HasBranchFromAsync(
        int repositoryId, string ecosystem, string packageName, string fromVersion, bool major, CancellationToken ct) =>
        db.DependencyUpdates.AsNoTracking().AnyAsync(update => update.GitInternalRepoId == repositoryId && update.Ecosystem == ecosystem
            && update.PackageName == packageName && update.FromVersion == fromVersion && update.Branch != null
            && update.IsMajorUpgrade == major, ct);

    /// <summary>The update whose lock refresh run this is, tracked, while it waits for that run; null otherwise.</summary>
    public async Task<DependencyUpdate?> FindOfLockRunAsync(int runId, CancellationToken ct) =>
        (await UpdatesReferencingAsync(runId, ct).ConfigureAwait(false))
            .FirstOrDefault(update => update.LockPipelineRunId == runId && update.Status == DependencyUpdateStatus.LockRefreshing);

    // A run completion reaches three handlers (the QA after a CI run, the pull request after a QA run, the lock files after a
    // lock refresh run), each asking which update the run belongs to by another column. One read, tracked, answers the three: the
    // rows that name the run in any of the three. It is kept for the repository's scope, which is the dispatch of that completion,
    // and dropped with the unsaved changes (their entities are no longer tracked).
    private (int RunId, Task<List<DependencyUpdate>> Updates)? _referencing;

    private Task<List<DependencyUpdate>> ReadReferencingAsync(int runId, CancellationToken ct) =>
        db.DependencyUpdates
            .Where(update => (update.CiPipelineRunId == runId || update.QaPipelineRunId == runId || update.LockPipelineRunId == runId)
                             && update.Branch != null && update.CommitSha != null)
            .ToListAsync(ct);

    private async Task<List<DependencyUpdate>> UpdatesReferencingAsync(int runId, CancellationToken ct)
    {
        if (_referencing is { } known && known.RunId == runId) return await known.Updates.ConfigureAwait(false);
        var updates = ReadReferencingAsync(runId, ct);
        _referencing = (runId, updates);
        return await updates.ConfigureAwait(false);
    }

    /// <summary>The project's pipeline of that name with its owner (the user who saved it last), or null.</summary>
    public Task<DependencyUpdateOwnedPipeline?> FindOwnedPipelineAsync(int projectId, string name, CancellationToken ct) =>
        db.Pipelines.AsNoTracking()
            .Where(pipeline => pipeline.ProjectId == projectId && pipeline.Name == name)
            .OrderBy(pipeline => pipeline.Id)
            .Select(pipeline => new DependencyUpdateOwnedPipeline(pipeline.Id, pipeline.CreatedByUsername))
            .FirstOrDefaultAsync(ct);

    /// <summary>The owner of a pipeline now, or null when the pipeline is gone.</summary>
    public Task<DependencyUpdateOwnedPipeline?> FindOwnedPipelineAsync(int pipelineId, CancellationToken ct) =>
        db.Pipelines.AsNoTracking()
            .Where(pipeline => pipeline.Id == pipelineId)
            .Select(pipeline => new DependencyUpdateOwnedPipeline(pipeline.Id, pipeline.CreatedByUsername))
            .FirstOrDefaultAsync(ct);

    /// <summary>Whether the account exists, and whether it is active; null when it does not exist.</summary>
    public Task<bool?> IsUserActiveAsync(string username, CancellationToken ct) =>
        db.Users.AsNoTracking().Where(user => user.Username == username).Select(user => (bool?)user.IsActive).FirstOrDefaultAsync(ct);

    /// <summary>The update of this package and target in this repository, tracked, or null.</summary>
    public Task<DependencyUpdate?> FindAsync(int repositoryId, string ecosystem, string packageName, string targetVersion, CancellationToken ct) =>
        db.DependencyUpdates.FirstOrDefaultAsync(update => update.GitInternalRepoId == repositoryId && update.Ecosystem == ecosystem
                                                           && update.PackageName == packageName && update.TargetVersion == targetVersion, ct);

    /// <summary>The update whose CI run this is, tracked, or null: the run is not one an update launched.</summary>
    public async Task<DependencyUpdate?> FindOfCiRunAsync(int runId, CancellationToken ct) =>
        (await UpdatesReferencingAsync(runId, ct).ConfigureAwait(false)).FirstOrDefault(update => update.CiPipelineRunId == runId);

    /// <summary>R-30: the update whose QA run this is, tracked, or null: the run is not one an update launched.</summary>
    public async Task<DependencyUpdate?> FindOfQaRunAsync(int runId, CancellationToken ct) =>
        (await UpdatesReferencingAsync(runId, ct).ConfigureAwait(false)).FirstOrDefault(update => update.QaPipelineRunId == runId);

    /// <summary>R-30: the updates whose CI and QA runs are both recorded and whose step after them was not done yet, tracked, oldest first:
    /// the rows the completion event of the QA run may have missed.</summary>
    public Task<List<DependencyUpdate>> GetAwaitingReadyAsync(CancellationToken ct) =>
        db.DependencyUpdates
            .Where(update => update.ReadyHandledAt == null && update.Branch != null && update.CommitSha != null
                             && update.CiPipelineRunId != null && update.QaPipelineRunId != null)
            .OrderBy(update => update.Id)
            .ToListAsync(ct);

    /// <summary>R-30: the updates whose branch was not retired yet and whose vulnerability is now closed, tracked, oldest first.</summary>
    public Task<List<DependencyUpdate>> GetResolvedToRetireAsync(CancellationToken ct) =>
        db.DependencyUpdates
            .Where(update => update.BranchRetiredAt == null && update.Branch != null
                             && update.VulnerabilityMatch != null && update.VulnerabilityMatch.ClosedAt != null)
            .OrderBy(update => update.Id)
            .ToListAsync(ct);

    /// <summary>The name of the project, or null when it is gone.</summary>
    public Task<string?> GetProjectNameAsync(int projectId, CancellationToken ct) =>
        db.Projects.AsNoTracking().Where(project => project.Id == projectId).Select(project => project.Name).FirstOrDefaultAsync(ct);

    /// <summary>The pipelines of the project that read this repository (or the project's default one).</summary>
    public Task<List<DependencyUpdatePipeline>> GetPipelinesAsync(int projectId, int repositoryId, CancellationToken ct) =>
        db.Pipelines.AsNoTracking()
            .Where(pipeline => pipeline.ProjectId == projectId && (pipeline.SourceRepositoryId == null || pipeline.SourceRepositoryId == repositoryId))
            .OrderBy(pipeline => pipeline.Id)
            .Select(pipeline => new DependencyUpdatePipeline(
                pipeline.Id, pipeline.Name, pipeline.TemplateReferenceName, pipeline.SourceRepositoryId, pipeline.TriggerType, pipeline.YamlDefinition))
            .ToListAsync(ct);

    /// <summary>The latest run of a pipeline on this branch and commit, or null.</summary>
    public Task<DependencyUpdateRunRow?> FindRunAsync(int pipelineId, string branch, string commit, CancellationToken ct) =>
        db.PipelineRuns.AsNoTracking()
            .Where(run => run.PipelineId == pipelineId && run.BranchName == branch && run.CommitHash == commit)
            .OrderByDescending(run => run.Id)
            .Select(run => new DependencyUpdateRunRow(run.Id, run.Status))
            .FirstOrDefaultAsync(ct);

    /// <summary>The latest run of each wanted pipeline, branch and commit, in one query for the whole page (never one per row). Keyed by
    /// pipeline and branch, as the update rows name them; a wanted run that does not exist is absent. The query reads a superset (the
    /// pipelines, branches and commits asked for), and only the exact triples count.</summary>
    public async Task<Dictionary<(int PipelineId, string Branch), DependencyUpdateRunRow>> FindLatestRunsAsync(
        IReadOnlyCollection<(int PipelineId, string Branch, string Commit)> wanted, CancellationToken ct)
    {
        if (wanted.Count == 0) return [];
        var pipelineIds = wanted.Select(item => item.PipelineId).Distinct().ToArray();
        var branches = wanted.Select(item => item.Branch).Distinct().ToArray();
        var commits = wanted.Select(item => item.Commit).Distinct().ToArray();
        var runs = await db.PipelineRuns.AsNoTracking()
            .Where(run => pipelineIds.Contains(run.PipelineId) && branches.Contains(run.BranchName!) && commits.Contains(run.CommitHash!))
            .Select(run => new { run.Id, run.PipelineId, run.BranchName, run.CommitHash, run.Status })
            .ToListAsync(ct).ConfigureAwait(false);
        var found = new Dictionary<(int, string), DependencyUpdateRunRow>();
        foreach (var (pipelineId, branch, commit) in wanted)
        {
            var latest = runs.Where(run => run.PipelineId == pipelineId && run.BranchName == branch && run.CommitHash == commit)
                .OrderByDescending(run => run.Id).FirstOrDefault();
            if (latest is not null) found[(pipelineId, branch)] = new DependencyUpdateRunRow(latest.Id, latest.Status);
        }

        return found;
    }

    /// <summary>The updates still waiting for their lock refresh run (<see cref="DependencyUpdateStatus.LockRefreshing"/>) with a branch
    /// and a commit, tracked, oldest first: the rows the run's completion event may have missed.</summary>
    public Task<List<DependencyUpdate>> GetLockRefreshingAsync(CancellationToken ct) =>
        db.DependencyUpdates
            .Where(update => update.Status == DependencyUpdateStatus.LockRefreshing && update.Branch != null && update.CommitSha != null)
            .OrderBy(update => update.Id)
            .ToListAsync(ct);

    /// <summary>The status of each run asked for, in one query; a run that does not exist is absent.</summary>
    public Task<Dictionary<int, PipelineStatus>> GetRunStatusesAsync(IReadOnlyCollection<int> runIds, CancellationToken ct)
    {
        var ids = runIds.ToArray();
        return db.PipelineRuns.AsNoTracking().Where(run => ids.Contains(run.Id)).ToDictionaryAsync(run => run.Id, run => run.Status, ct);
    }

    /// <summary>The names of pipelines by id; a deleted pipeline is absent.</summary>
    public Task<Dictionary<int, string>> GetPipelineNamesAsync(IReadOnlyCollection<int> pipelineIds, CancellationToken ct)
    {
        var ids = pipelineIds.ToArray();
        return db.Pipelines.AsNoTracking().Where(pipeline => ids.Contains(pipeline.Id)).ToDictionaryAsync(pipeline => pipeline.Id, pipeline => pipeline.Name, ct);
    }

    /// <summary>The updates of a project, newest first, at most <paramref name="take"/>.</summary>
    public Task<List<DependencyUpdate>> GetOfProjectAsync(int projectId, int take, CancellationToken ct) =>
        db.DependencyUpdates.AsNoTracking()
            .Where(update => update.ProjectId == projectId)
            .OrderByDescending(update => update.UpdatedAt).ThenByDescending(update => update.Id)
            .Take(take)
            .ToListAsync(ct);

    /// <summary>The project's setting, or null when the project does not exist.</summary>
    public Task<bool?> GetEnabledAsync(int projectId, CancellationToken ct) =>
        db.Projects.AsNoTracking().Where(project => project.Id == projectId)
            .Select(project => (bool?)project.DependencyUpdatesEnabled).FirstOrDefaultAsync(ct);

    /// <summary>Writes the project's setting. False when the project does not exist.</summary>
    public async Task<bool> SetEnabledAsync(int projectId, bool enabled, DateTime now, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(candidate => candidate.Id == projectId, ct).ConfigureAwait(false);
        if (project is null) return false;
        project.DependencyUpdatesEnabled = enabled;
        project.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public void Add(DependencyUpdate update) => db.DependencyUpdates.Add(update);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    /// <summary>Forgets what a failed save left tracked, so the next candidate does not try to save it again.</summary>
    public void DiscardUnsavedChanges()
    {
        _referencing = null;
        foreach (var entry in db.ChangeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
            entry.State = EntityState.Detached;
    }
}
