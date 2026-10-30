// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Git;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Projects;

/// <summary>Delete database rows transactionally only after all project runs end, then remove files
/// after commit to avoid live rows referencing deleted files. Audit removed resources and any files
/// that could not be removed. Recette R-97 (ADR-058): the rows go only once the server tasks prove that
/// what the project deployed is removed. Recette R-51: otherwise the project is marked deleted, waiting for its
/// server cleanup, and goes once a relaunched cleanup proves everything removed.</summary>
public sealed class ProjectDeletionService(
    IProjectDeletionRepository repo,
    IProjectServerCleanupService serverCleanup,
    IDbTransactionScope transaction,
    IArtifactStorageService artifactStorage,
    IGitLightService gitLight,
    IGitLightCliService gitCli,
    PortRegistry.IPortRegistryService portRegistry,
    IAuditService audit,
    IEntityChangeNotifier notifier,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<ProjectDeletionService> logger) : IProjectDeletionService
{
    public async Task<ProjectDeletionPreviewDto?> GetPreviewAsync(int projectId, CancellationToken ct = default)
    {
        var scope = await repo.ResolveScopeAsync(projectId, ct).ConfigureAwait(false);
        if (scope is null) return null;
        var counts = await repo.CountAsync(scope, ct).ConfigureAwait(false);
        var plan = await serverCleanup.GetPlanAsync(projectId, ct).ConfigureAwait(false);
        return new ProjectDeletionPreviewDto
        {
            ProjectId = projectId,
            ProjectName = scope.ProjectName,
            ActiveRunCount = await repo.CountActiveRunsAsync(scope, ct).ConfigureAwait(false),
            Items = [.. counts.Where(item => item.Value > 0).OrderBy(item => item.Key)
                .Select(item => new ProjectDeletionItemDto(item.Key, item.Value))],
            Servers = plan is null ? [] : [.. plan.Servers.Select(server => server.ToDto())],
            RefusedValues = plan is null ? [] : [.. plan.Refused],
            DeletionRequestedAt = scope.DeletionRequestedAt,
        };
    }

    public async Task<ProjectDeletionOutcome?> DeleteAsync(int projectId, IReadOnlyCollection<int> serverTaskIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(serverTaskIds);
        var verdict = await serverCleanup.VerifyAsync(projectId, serverTaskIds, ct).ConfigureAwait(false);
        if (verdict is null) return null;
        if (!verdict.Clean)
            return await MarkPendingCleanupAsync(projectId, verdict, ct).ConfigureAwait(false);

        // The InMemory provider of the unit tests has no transaction to open.
        var removed = transaction.IsRelational
            ? await transaction.ExecuteInTransactionAsync(() => DeleteRowsAsync(projectId, ct), ct).ConfigureAwait(false)
            : await DeleteRowsAsync(projectId, ct).ConfigureAwait(false);
        if (removed is null) return null;

        var (scope, counts, artifactPaths, repositorySlugs) = removed.Value;
        var artifactFailures = await DeleteArtifactFilesAsync(artifactPaths, ct).ConfigureAwait(false);
        var repositoryFailures = await DeleteRepositoriesAsync(projectId, repositorySlugs, ct).ConfigureAwait(false);
        cache.Remove(ProjectService.RepoUrlsCacheKey);

        var description = DescribeDeletion(scope.ProjectName, counts, artifactFailures, repositoryFailures);
        if (verdict.Summary.Length > 0)
            description += "; " + verdict.Summary;
        await audit.LogAsync("Deleted", "Project", projectId, description, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.Project, projectId, EntityChangeOps.Deleted, ct,
            organizationId: scope.OrganizationId).ConfigureAwait(false);
        return ProjectDeletionOutcome.Deleted;
    }

    /// <summary>Recette R-51 (ADR-058): something the project deployed is not proved removed. Its Aetheus data is
    /// kept, whole, so the cleanup can be relaunched (it is what names the servers and the resources), and the
    /// project becomes "deleted, to clean up": listed, unusable, until a relaunched cleanup proves everything
    /// removed. Never a half deletion.</summary>
    private async Task<ProjectDeletionOutcome?> MarkPendingCleanupAsync(int projectId, ProjectServerCleanupVerdict verdict, CancellationToken ct)
    {
        var scope = await repo.ResolveScopeAsync(projectId, ct).ConfigureAwait(false);
        if (scope is null) return null;
        var activeRuns = await repo.CountActiveRunsAsync(scope, ct).ConfigureAwait(false);
        if (activeRuns > 0)
            throw new ConflictException(
                $"Project '{scope.ProjectName}' has {activeRuns} pipeline run(s) in progress. "
                + "Cancel them or wait for them to end, then delete the project.");
        if (!await repo.MarkDeletionPendingAsync(projectId, timeProvider.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false))
            return null;
        await audit.LogAsync("DeletionPendingCleanup", "Project", projectId,
            $"{scope.ProjectName}; kept until its server cleanup ends; {verdict.Summary}", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.Project, projectId, EntityChangeOps.Updated, ct,
            organizationId: scope.OrganizationId).ConfigureAwait(false);
        return ProjectDeletionOutcome.PendingCleanup;
    }

    private async Task<(ProjectDeletionScope Scope, Dictionary<ProjectDeletionItemKind, int> Counts,
        List<string> ArtifactPaths, List<string> RepositorySlugs)?> DeleteRowsAsync(int projectId, CancellationToken ct)
    {
        // Read inside the transaction, so what is counted, refused or deleted is one consistent view.
        var scope = await repo.ResolveScopeAsync(projectId, ct).ConfigureAwait(false);
        if (scope is null) return null;
        var activeRuns = await repo.CountActiveRunsAsync(scope, ct).ConfigureAwait(false);
        if (activeRuns > 0)
            throw new ConflictException(
                $"Project '{scope.ProjectName}' has {activeRuns} pipeline run(s) in progress. "
                + "Cancel them or wait for them to end, then delete the project.");

        var counts = await repo.CountAsync(scope, ct).ConfigureAwait(false);
        var artifactPaths = await repo.GetArtifactFilePathsAsync(scope, ct).ConfigureAwait(false);
        var repositorySlugs = await repo.GetGitRepositorySlugsAsync(scope, ct).ConfigureAwait(false);
        // The registry owns what a project-owned reservation is; the repository then removes the
        // reservations of the project servers and any other declared port still naming the project.
        await portRegistry.ReleaseProjectPortsAsync(projectId, ct).ConfigureAwait(false);
        await repo.DeleteAsync(scope, ct).ConfigureAwait(false);
        return (scope, counts, artifactPaths, repositorySlugs);
    }

    private async Task<int> DeleteArtifactFilesAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var failures = 0;
        foreach (var path in paths)
        {
            try
            {
                await artifactStorage.DeleteArtifactAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The rows are already committed: a file left behind is reported, not a reason to fail.
                failures++;
                logger.LogWarning(ex, "Deleted project artifact file {Path} could not be removed", path);
            }
        }
        return failures;
    }

    private async Task<int> DeleteRepositoriesAsync(int projectId, IReadOnlyList<string> slugs, CancellationToken ct)
    {
        var failures = 0;
        foreach (var slug in slugs)
        {
            try
            {
                await gitCli.DeleteRepoAsync(gitLight.ResolveDiskPath(projectId, slug), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failures++;
                logger.LogWarning(ex, "Deleted project repository {Slug} could not be removed from disk", slug);
            }
        }
        return failures;
    }

    internal static string DescribeDeletion(
        string projectName,
        IReadOnlyDictionary<ProjectDeletionItemKind, int> counts,
        int artifactFileFailures,
        int repositoryFailures)
    {
        var removed = counts.Where(item => item.Value > 0).OrderBy(item => item.Key)
            .Select(item => string.Create(CultureInfo.InvariantCulture, $"{item.Key}={item.Value}"));
        var description = $"{projectName}; removed: {string.Join(", ", removed.DefaultIfEmpty("nothing else"))}";
        if (artifactFileFailures > 0)
            description += string.Create(CultureInfo.InvariantCulture, $"; artifact files left on disk: {artifactFileFailures}");
        if (repositoryFailures > 0)
            description += string.Create(CultureInfo.InvariantCulture, $"; repositories left on disk: {repositoryFailures}");
        return description;
    }
}
