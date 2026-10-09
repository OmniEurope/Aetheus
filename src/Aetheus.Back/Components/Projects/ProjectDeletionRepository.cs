// SPDX-License-Identifier: EUPL-1.2
using System.Linq.Expressions;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Projects;

/// <summary>
/// Recette R2-122: deleting a project removes everything it owns. The foreign keys alone do not:
/// pipelines, environments, service connections and package feeds are SET NULL (they used to survive
/// detached), monitored apps, project servers and vaults are RESTRICT (the delete failed with 23503),
/// and the tasks of a run are SET NULL from the run. So the rows are removed explicitly, leaf to root,
/// the large tables in bounded batches so no single statement grows with the project's history.
/// </summary>
internal sealed class ProjectDeletionRepository(AppDbContext db) : IProjectDeletionRepository
{
    internal const int BatchSize = 5_000;

    public async Task<ProjectDeletionScope?> ResolveScopeAsync(int projectId, CancellationToken ct = default)
    {
        var project = await db.Projects.AsNoTracking()
            .Where(item => item.Id == projectId)
            .Select(item => new { item.Name, item.OrganizationId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (project is null) return null;

        var environmentIds = await db.Environments.Where(item => item.ProjectId == projectId)
            .Select(item => item.Id).ToListAsync(ct).ConfigureAwait(false);
        var projectServerIds = await db.ProjectServers.Where(item => item.ProjectId == projectId)
            .Select(item => item.Id).ToListAsync(ct).ConfigureAwait(false);
        var pipelineIds = await db.Pipelines.Where(item => item.ProjectId == projectId
                || (item.EnvironmentId != null && environmentIds.Contains(item.EnvironmentId.Value))
                || (item.ProjectServerId != null && projectServerIds.Contains(item.ProjectServerId.Value)))
            .Select(item => item.Id).ToListAsync(ct).ConfigureAwait(false);
        var vaultIds = await db.Vaults.Where(item => item.ProjectId == projectId
                || (item.EnvironmentId != null && environmentIds.Contains(item.EnvironmentId.Value))
                || (item.ProjectServerId != null && projectServerIds.Contains(item.ProjectServerId.Value)))
            .Select(item => item.Id).ToListAsync(ct).ConfigureAwait(false);
        var libraryIds = await db.VariableLibraries.Where(item => item.ProjectId == projectId
                || (item.EnvironmentId != null && environmentIds.Contains(item.EnvironmentId.Value))
                || (item.ProjectServerId != null && projectServerIds.Contains(item.ProjectServerId.Value)))
            .Select(item => item.Id).ToListAsync(ct).ConfigureAwait(false);

        return new ProjectDeletionScope(
            projectId,
            project.Name,
            project.OrganizationId,
            environmentIds,
            projectServerIds,
            pipelineIds,
            await db.MonitoredApps.Where(item => item.ProjectId == projectId).Select(item => item.Id).ToListAsync(ct).ConfigureAwait(false),
            vaultIds,
            libraryIds,
            await db.Releases.Where(item => item.ProjectId == projectId).Select(item => item.Id).ToListAsync(ct).ConfigureAwait(false),
            await db.ServiceConnections.Where(item => item.ProjectId == projectId).Select(item => item.Id).ToListAsync(ct).ConfigureAwait(false),
            await db.PackageFeeds.Where(item => item.ProjectId == projectId).Select(item => item.Id).ToListAsync(ct).ConfigureAwait(false));
    }

    public Task<int> CountActiveRunsAsync(ProjectDeletionScope scope, CancellationToken ct = default) =>
        Runs(scope).CountAsync(run => !PipelineStatusFacts.Terminal.Contains(run.Status), ct);

    public async Task<Dictionary<ProjectDeletionItemKind, int>> CountAsync(
        ProjectDeletionScope scope, CancellationToken ct = default)
    {
        var projectId = scope.ProjectId;
        return new Dictionary<ProjectDeletionItemKind, int>
        {
            [ProjectDeletionItemKind.Pipelines] = scope.PipelineIds.Count,
            [ProjectDeletionItemKind.PipelineRuns] = await Runs(scope).CountAsync(ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.PipelineStepRuns] = await StepRuns(scope).CountAsync(ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.Tasks] = await Tasks(scope).CountAsync(ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.TaskLogLines] = await TaskLogs(scope).CountAsync(ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.Approvals] = await Approvals(scope).CountAsync(ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.Artifacts] = await Artifacts(scope).CountAsync(ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.Environments] = scope.EnvironmentIds.Count,
            [ProjectDeletionItemKind.MonitoredApps] = scope.MonitoredAppIds.Count,
            [ProjectDeletionItemKind.TelemetryRecords] = await ProjectDeletionTelemetry.CountAsync(db, scope.MonitoredAppIds, ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.BackupPolicies] = await db.BackupPolicies.CountAsync(item => item.ProjectId == projectId, ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.ProjectServers] = scope.ProjectServerIds.Count,
            [ProjectDeletionItemKind.Vaults] = scope.VaultIds.Count,
            [ProjectDeletionItemKind.VaultSecrets] = await db.VaultSecrets.CountAsync(item => scope.VaultIds.Contains(item.VaultId), ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.VariableLibraries] = scope.LibraryIds.Count,
            [ProjectDeletionItemKind.ServiceConnections] = scope.ServiceConnectionIds.Count,
            [ProjectDeletionItemKind.PackageFeeds] = scope.PackageFeedIds.Count,
            [ProjectDeletionItemKind.PortReservations] = await PortReservations(scope).CountAsync(ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.Subscriptions] = await db.ProjectSubscriptions.CountAsync(item => item.ProjectId == projectId, ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.Releases] = scope.ReleaseIds.Count,
            [ProjectDeletionItemKind.GitRepositories] = await db.GitInternalRepos.CountAsync(item => item.ProjectId == projectId, ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.GitConnections] = await db.GitConnections.CountAsync(item => item.ProjectId == projectId, ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.AnalysisReports] = await db.AnalysisReports.CountAsync(item => item.ProjectId == projectId, ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.AnalysisFindings] = await db.AnalysisFindings.CountAsync(item => item.ProjectId == projectId, ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.WorkItems] = await db.WorkItems.CountAsync(item => item.ProjectId == projectId, ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.TestSuites] = await db.TestSuites.CountAsync(item => item.ProjectId == projectId, ct).ConfigureAwait(false),
            [ProjectDeletionItemKind.AiTasks] = await db.AiTaskDefinitions.CountAsync(item => item.ProjectId == projectId, ct).ConfigureAwait(false),
        };
    }

    public Task<List<string>> GetArtifactFilePathsAsync(ProjectDeletionScope scope, CancellationToken ct = default) =>
        Artifacts(scope).Select(item => item.FilePath).Where(path => path != "").ToListAsync(ct);

    public Task<List<string>> GetGitRepositorySlugsAsync(ProjectDeletionScope scope, CancellationToken ct = default) =>
        db.GitInternalRepos.Where(item => item.ProjectId == scope.ProjectId).Select(item => item.Slug).ToListAsync(ct);

    public async Task DeleteAsync(ProjectDeletionScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (db.Database.IsRelational())
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(2));

        await DeleteRunHistoryAsync(scope, ct).ConfigureAwait(false);
        await DeleteAllAsync(db.Pipelines.Where(item => scope.PipelineIds.Contains(item.Id)), ct).ConfigureAwait(false);
        await DeleteAnalysisLeavesAsync(scope.ProjectId, ct).ConfigureAwait(false);
        await ProjectDeletionTelemetry.DeleteAsync(this, db, scope.MonitoredAppIds, ct).ConfigureAwait(false);
        await DeleteAllAsync(db.MonitoredApps.Where(item => scope.MonitoredAppIds.Contains(item.Id)), ct).ConfigureAwait(false);
        await DeleteOwnedConfigurationAsync(scope, ct).ConfigureAwait(false);
        await DeleteProjectRowsAsync(scope, ct).ConfigureAwait(false);
    }

    /// <summary>Runs and what hangs off them. Tasks go first: the run and step-run foreign keys only
    /// SET NULL on a task, which would lose track of which tasks were the project's.</summary>
    private async Task DeleteRunHistoryAsync(ProjectDeletionScope scope, CancellationToken ct)
    {
        await DeleteInBatchesAsync(TaskLogs(scope), item => item.Id, ct).ConfigureAwait(false);
        // A task cascades its AI result; the results left are those of the project's runs and AI tasks
        // whose task is not the project's.
        await DeleteInBatchesAsync(Tasks(scope), item => item.Id, ct).ConfigureAwait(false);
        var runIds = Runs(scope).Select(run => run.Id);
        await DeleteAllAsync(AiRunResults(scope), ct).ConfigureAwait(false);
        await DeleteAllAsync(db.DastExecutionLeases.Where(item => runIds.Contains(item.PipelineRunId)
            || scope.EnvironmentIds.Contains(item.EnvironmentId)), ct).ConfigureAwait(false);
        await DeleteAllAsync(Approvals(scope), ct).ConfigureAwait(false);
        await DeleteInBatchesAsync(db.TestResults.Where(item => runIds.Contains(item.PipelineRunId)), item => item.Id, ct).ConfigureAwait(false);
        await DeleteInBatchesAsync(db.RunMetrics.Where(item => runIds.Contains(item.PipelineRunId)), item => item.Id, ct).ConfigureAwait(false);
        await DeleteAllAsync(db.ReleaseRollbacks.Where(item => scope.PipelineIds.Contains(item.PipelineId)
            || scope.ReleaseIds.Contains(item.SourceReleaseId)
            || scope.ReleaseIds.Contains(item.TargetReleaseId)), ct).ConfigureAwait(false);
        var artifactIds = Artifacts(scope).Select(item => item.Id);
        await DeleteAllAsync(db.DependencyTrackOutboxItems.Where(item => item.ProjectId == scope.ProjectId
            || (item.PipelineArtifactId != null && artifactIds.Contains(item.PipelineArtifactId.Value))), ct).ConfigureAwait(false);
        await DeleteAllAsync(Artifacts(scope), ct).ConfigureAwait(false);
        await DeleteInBatchesAsync(StepRuns(scope), item => item.Id, ct).ConfigureAwait(false);
        await DeleteInBatchesAsync(Runs(scope), item => item.Id, ct).ConfigureAwait(false);
    }

    /// <summary>The analysis tables that grow with every scan; the rest cascades from the project.</summary>
    private async Task DeleteAnalysisLeavesAsync(int projectId, CancellationToken ct)
    {
        await DeleteInBatchesAsync(db.AnalysisVulnerabilityObservations.Where(item => item.ProjectId == projectId), item => item.Id, ct).ConfigureAwait(false);
        var findingIds = db.AnalysisFindings.Where(item => item.ProjectId == projectId).Select(item => item.Id);
        await DeleteInBatchesAsync(db.AnalysisFindingOccurrences.Where(item => findingIds.Contains(item.AnalysisFindingId)), item => item.Id, ct).ConfigureAwait(false);
        await DeleteInBatchesAsync(db.AnalysisComponents.Where(item => item.ProjectId == projectId), item => item.Id, ct).ConfigureAwait(false);
        await DeleteInBatchesAsync(db.AnalysisMetrics.Where(item => item.ProjectId == projectId), item => item.Id, ct).ConfigureAwait(false);
    }

    /// <summary>Vaults, libraries, environments, project servers, connections and feeds: vaults first
    /// because their owner keys are RESTRICT. Removing a project server only removes its link to a
    /// server; the server record itself, which may host other things, is never touched.</summary>
    private async Task DeleteOwnedConfigurationAsync(ProjectDeletionScope scope, CancellationToken ct)
    {
        await DeleteAllAsync(db.VaultSecretVersions.Where(item => scope.VaultIds.Contains(item.VaultSecret!.VaultId)), ct).ConfigureAwait(false);
        await DeleteAllAsync(db.VaultSecrets.Where(item => scope.VaultIds.Contains(item.VaultId)), ct).ConfigureAwait(false);
        await DeleteAllAsync(db.Vaults.Where(item => scope.VaultIds.Contains(item.Id)), ct).ConfigureAwait(false);
        await DeleteAllAsync(db.VariableLibraries.Where(item => scope.LibraryIds.Contains(item.Id)), ct).ConfigureAwait(false);
        await DeleteAllAsync(db.Environments.Where(item => scope.EnvironmentIds.Contains(item.Id)), ct).ConfigureAwait(false);
        await DeleteAllAsync(db.ProjectServers.Where(item => scope.ProjectServerIds.Contains(item.Id)), ct).ConfigureAwait(false);
        await DeleteAllAsync(db.ServiceConnections.Where(item => scope.ServiceConnectionIds.Contains(item.Id)), ct).ConfigureAwait(false);
        await DeleteAllAsync(db.PackageFeeds.Where(item => scope.PackageFeedIds.Contains(item.Id)), ct).ConfigureAwait(false);
        await DeleteAllAsync(PortReservations(scope), ct).ConfigureAwait(false);
    }

    private async Task DeleteProjectRowsAsync(ProjectDeletionScope scope, CancellationToken ct)
    {
        var projectId = scope.ProjectId;
        await DeleteAllAsync(PermissionsOf(scope), ct).ConfigureAwait(false);
        await DeleteInBatchesAsync(db.GitCommits.Where(item => item.ProjectId == projectId), item => item.Id, ct).ConfigureAwait(false);

        // A sub-item's parent is RESTRICT, so the board is flattened before its items go.
        var children = await db.WorkItems.Where(item => item.ProjectId == projectId && item.ParentId != null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var child in children) child.ParentId = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await DeleteAllAsync(db.WorkItems.Where(item => item.ProjectId == projectId), ct).ConfigureAwait(false);
        // Set-based deletes bypass the change tracker: forget what it holds so removing the project
        // does not try to delete, a second time, rows that are already gone.
        db.ChangeTracker.Clear();

        var project = await db.Projects.FirstAsync(item => item.Id == projectId, ct).ConfigureAwait(false);
        // External Git: the project's GitConnectionId points at a connection that the project's own
        // cascade also removes; PostgreSQL refuses the double path (23503) unless it is cleared first.
        if (project.GitConnectionId is not null)
        {
            project.GitConnectionId = null;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private IQueryable<ResourcePermission> PermissionsOf(ProjectDeletionScope scope) =>
        db.ResourcePermissions.Where(item => item.ResourceId != null && (
            (item.ResourceType == ResourceType.Project && item.ResourceId == scope.ProjectId)
            || (item.ResourceType == ResourceType.Pipeline && scope.PipelineIds.Contains(item.ResourceId.Value))
            || (item.ResourceType == ResourceType.Environment && scope.EnvironmentIds.Contains(item.ResourceId.Value))
            || (item.ResourceType == ResourceType.Vault && scope.VaultIds.Contains(item.ResourceId.Value))
            || (item.ResourceType == ResourceType.VariableLibrary && scope.LibraryIds.Contains(item.ResourceId.Value))
            || (item.ResourceType == ResourceType.Release && scope.ReleaseIds.Contains(item.ResourceId.Value))
            || (item.ResourceType == ResourceType.ServiceConnection && scope.ServiceConnectionIds.Contains(item.ResourceId.Value))));

    private IQueryable<PipelineRun> Runs(ProjectDeletionScope scope) =>
        db.PipelineRuns.Where(run => scope.PipelineIds.Contains(run.PipelineId));

    private IQueryable<PipelineStepRun> StepRuns(ProjectDeletionScope scope)
    {
        var runIds = Runs(scope).Select(run => run.Id);
        return db.PipelineStepRuns.Where(step => runIds.Contains(step.PipelineRunId));
    }

    /// <summary>The tasks of the project's runs and of its AI tasks.</summary>
    private IQueryable<ServerTask> Tasks(ProjectDeletionScope scope)
    {
        var runIds = Runs(scope).Select(run => run.Id);
        var stepRunIds = StepRuns(scope).Select(step => step.Id);
        var aiTaskIds = AiRunResults(scope).Select(result => result.ServerTaskId);
        return db.Tasks.Where(task => (task.PipelineRunId != null && runIds.Contains(task.PipelineRunId.Value))
            || (task.PipelineStepRunId != null && stepRunIds.Contains(task.PipelineStepRunId.Value))
            || aiTaskIds.Contains(task.Id));
    }

    private IQueryable<AiRunResult> AiRunResults(ProjectDeletionScope scope)
    {
        var runIds = Runs(scope).Select(run => run.Id);
        var definitionIds = db.AiTaskDefinitions.Where(item => item.ProjectId == scope.ProjectId).Select(item => item.Id);
        return db.AiRunResults.Where(item => (item.PipelineRunId != null && runIds.Contains(item.PipelineRunId.Value))
            || (item.AiTaskDefinitionId != null && definitionIds.Contains(item.AiTaskDefinitionId.Value)));
    }

    private IQueryable<TaskLog> TaskLogs(ProjectDeletionScope scope)
    {
        var taskIds = Tasks(scope).Select(task => task.Id);
        return db.TaskLogs.Where(log => taskIds.Contains(log.TaskId));
    }

    private IQueryable<PipelineApproval> Approvals(ProjectDeletionScope scope)
    {
        var runIds = Runs(scope).Select(run => run.Id);
        return db.PipelineApprovals.Where(item => runIds.Contains(item.PipelineRunId)
            || (item.EnvironmentId != null && scope.EnvironmentIds.Contains(item.EnvironmentId.Value)));
    }

    private IQueryable<PipelineArtifact> Artifacts(ProjectDeletionScope scope) =>
        db.PipelineArtifacts.Where(item => item.ProjectId == scope.ProjectId || scope.PipelineIds.Contains(item.PipelineId));

    /// <summary>Declared and allocated ports only: an observed port describes what the host shows,
    /// which deleting a project does not change (its foreign key is cleared instead).</summary>
    private IQueryable<ServerPortReservation> PortReservations(ProjectDeletionScope scope)
    {
        var ownerKeys = scope.ProjectServerIds.Select(PortRegistry.PortRegistryService.ProjectServerOwnerKey)
            .Append(PortRegistry.PortRegistryService.ProjectOwnerKey(scope.ProjectId))
            .ToList();
        return db.ServerPortReservations.Where(item => item.Source != PortReservationSource.Observed
            && (item.ProjectId == scope.ProjectId || ownerKeys.Contains(item.OwnerKey)));
    }

    internal async Task<int> DeleteAllAsync<T>(IQueryable<T> rows, CancellationToken ct) where T : class
    {
        if (db.Database.IsRelational())
            return await rows.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        // InMemory (unit tests) has no set-based delete.
        var tracked = await rows.ToListAsync(ct).ConfigureAwait(false);
        db.RemoveRange(tracked);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return tracked.Count;
    }

    /// <summary>Deletes <paramref name="rows"/> <see cref="BatchSize"/> keys at a time, so the size of
    /// one statement is bounded whatever the history of the project.</summary>
    internal async Task<int> DeleteInBatchesAsync<T, TKey>(
        IQueryable<T> rows, Expression<Func<T, TKey>> key, CancellationToken ct)
        where T : class
        where TKey : struct
    {
        var batch = new KeyBatch<TKey>();
        // The keys are read through a member of a captured object, so EF sends them as one parameter
        // and reuses the same statement for every batch.
        var inBatch = Expression.Lambda<Func<T, bool>>(
            Expression.Call(
                typeof(Enumerable), nameof(Enumerable.Contains), [typeof(TKey)],
                Expression.Property(Expression.Constant(batch), nameof(KeyBatch<TKey>.Keys)),
                key.Body),
            key.Parameters);
        var total = 0;
        while (true)
        {
            batch.Keys = await rows.OrderBy(key).Select(key).Take(BatchSize).ToListAsync(ct).ConfigureAwait(false);
            if (batch.Keys.Count == 0) return total;
            total += await DeleteAllAsync(rows.Where(inBatch), ct).ConfigureAwait(false);
        }
    }

    private sealed class KeyBatch<TKey>
    {
        public List<TKey> Keys { get; set; } = [];
    }
}
