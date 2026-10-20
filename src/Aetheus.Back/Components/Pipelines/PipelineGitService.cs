// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Services.Email;

namespace Aetheus.Back.Components.Pipelines;

// Git-strict storage for project-owned pipelines (internal repos only). See IPipelineGitService.
// F-030: depends on the Git module's service contracts only (no cross-slice repository reach-in).
public sealed class PipelineGitService(
    IGitLightService gitService,
    IGitLightCliService cli,
    IPipelineRepository pipelineRepo,
    IGitCommitRecorder commitRecorder,
    IEmailSenderConfigurationResolver senderResolver,
    ILogger<PipelineGitService> logger) : IPipelineGitService
{
    private const string PipelineDir = ".pipeline";

    // The application writes the pipeline files, so every commit is authored by Aetheus with the
    // address it sends mail from (the no-reply sender, else the custom SMTP sender), never by the
    // acting login. The account that asked for the change stays in a "Requested-by" trailer.
    internal const string ServiceAuthorName = "Aetheus";
    internal const string ServiceAuthorEmail = "aetheus@aetheus.local";

    private static bool IsServiceActor(string? actor) =>
        string.IsNullOrWhiteSpace(actor) || string.Equals(actor, "system", StringComparison.OrdinalIgnoreCase);

    internal static string RequestedBy(string? actor) => IsServiceActor(actor) ? string.Empty : $"\n\nRequested-by: {actor}";

    private async Task<string> AuthorEmailAsync(CancellationToken ct)
    {
        var resolution = await senderResolver.ResolveAsync(ct).ConfigureAwait(false);
        var address = resolution.Configuration?.FromAddress ?? resolution.NoReplyAddress;
        return string.IsNullOrWhiteSpace(address) ? ServiceAuthorEmail : address;
    }

    public async Task<string?> ReadProjectPipelineYamlAsync(
        int projectId, string pipelineName, CancellationToken ct = default,
        string? sourceBranch = null, int? sourceRepositoryId = null)
        => await ReadProjectPipelineYamlCoreAsync(
            projectId, pipelineName, sourceBranch, sourceRepositoryId, ct).ConfigureAwait(false);

    public async Task<string?> ReadProjectPipelineYamlAtRevisionAsync(
        int projectId, string pipelineName, string revision, CancellationToken ct = default,
        int? sourceRepositoryId = null)
        => await ReadProjectPipelineYamlCoreAsync(
            projectId, pipelineName, revision, sourceRepositoryId, ct).ConfigureAwait(false);

    public async Task<string?> ReadProjectConfigAtRevisionAsync(
        int projectId, string relativePath, string revision, CancellationToken ct = default,
        int? sourceRepositoryId = null)
    {
        var normalized = relativePath.Replace('\\', '/').Trim();
        if (!normalized.StartsWith(".pipeline/configs/", StringComparison.Ordinal)
            || normalized.Length > 240
            || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
            throw new BadRequestException(
                "Configuration templates must use a safe path under .pipeline/configs/.");
        if (!PipelineRunHelpers.IsGitCommitHash(revision))
            throw new BadRequestException("Configuration templates require an immutable Git revision.");

        var repo = await GetProjectRepoAsync(projectId, sourceRepositoryId, ct).ConfigureAwait(false);
        if (repo is null || repo.IsEmpty) return null;
        var diskPath = SafeDiskPath(projectId, repo);
        if (diskPath is null || !Directory.Exists(diskPath)) return null;
        var blob = await cli.GetBlobAsync(diskPath, revision, normalized, ct).ConfigureAwait(false);
        if (blob is null || blob.IsBinary || blob.Size > 256 * 1024)
            return null;
        return blob.Content;
    }

    public async Task<string?> ReadVersionPropsAtRevisionAsync(
        int projectId, string revision, CancellationToken ct = default, int? sourceRepositoryId = null)
    {
        if (!PipelineRunHelpers.IsGitCommitHash(revision)) return null;
        var repo = await GetProjectRepoAsync(projectId, sourceRepositoryId, ct).ConfigureAwait(false);
        if (repo is null || repo.IsEmpty) return null;
        var diskPath = SafeDiskPath(projectId, repo);
        if (diskPath is null || !Directory.Exists(diskPath)) return null;
        var blob = await cli.GetBlobAsync(diskPath, revision, "Directory.Build.props", ct).ConfigureAwait(false);
        return blob is null || blob.IsBinary || blob.Size > 256 * 1024 ? null : blob.Content;
    }

    private async Task<string?> ReadProjectPipelineYamlCoreAsync(
        int projectId, string pipelineName, string? revision, int? sourceRepositoryId,
        CancellationToken ct)
    {
        var repo = await GetProjectRepoAsync(projectId, sourceRepositoryId, ct).ConfigureAwait(false);
        if (repo is null || repo.IsEmpty) return null;

        var diskPath = SafeDiskPath(projectId, repo);
        // Guard the run's critical path: a DB repo row with no bare repo on disk must fall back to
        // the DB definition, never throw (git would Win32Exception on a missing working directory).
        if (diskPath is null || !Directory.Exists(diskPath)) return null;

        var branch = string.IsNullOrWhiteSpace(revision)
            ? string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "HEAD" : repo.DefaultBranch
            : revision;
        return await ReadPipelineYamlAsync(diskPath, branch, pipelineName, ct).ConfigureAwait(false);
    }

    private async Task<string?> ReadPipelineYamlAsync(
        string diskPath, string branch, string pipelineName, CancellationToken ct)
    {
        // F-019: fast path - the UI writes `.pipeline/{slug}.yaml`, so a single `git show` resolves
        // the common case without enumerating (and parsing) every YAML in the directory.
        var directBlob = await cli.GetBlobAsync(diskPath, branch, $"{PipelineDir}/{PipelineGitPathPolicy.Slugify(pipelineName)}.yaml", ct).ConfigureAwait(false);
        if (MatchesDirectPipeline(directBlob?.Content, pipelineName)) return directBlob!.Content;

        var tree = await cli.GetTreeAsync(diskPath, branch, PipelineDir, ct).ConfigureAwait(false);
        foreach (var file in tree.Where(IsYaml))
        {
            var blob = await cli.GetBlobAsync(diskPath, branch, $"{PipelineDir}/{file.Name}", ct).ConfigureAwait(false);
            if (blob?.Content is null || string.IsNullOrWhiteSpace(blob.Content)) continue;

            var def = YamlParsingHelper.ParseAndValidate(blob.Content, logger);
            var nameInFile = def?.Name;
            if (string.IsNullOrWhiteSpace(nameInFile))
                nameInFile = Path.GetFileNameWithoutExtension(file.Name);

            if (string.Equals(nameInFile, pipelineName, StringComparison.OrdinalIgnoreCase))
                return blob.Content;
        }
        return null;
    }

    private bool MatchesDirectPipeline(string? yaml, string pipelineName)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return false;
        var definition = YamlParsingHelper.ParseAndValidate(yaml, logger);
        var name = string.IsNullOrWhiteSpace(definition?.Name) ? PipelineGitPathPolicy.Slugify(pipelineName) : definition!.Name;
        return string.Equals(name, pipelineName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, PipelineGitPathPolicy.Slugify(pipelineName), StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> GetHeadCommitShaAsync(
        int projectId, CancellationToken ct = default, string? sourceBranch = null,
        int? sourceRepositoryId = null)
    {
        try
        {
            var repo = await GetProjectRepoAsync(projectId, sourceRepositoryId, ct).ConfigureAwait(false);
            return await GetHeadCommitShaAsync(projectId, repo, sourceBranch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Return an observable unresolved source. Workspace pipelines fail closed on this null;
            // pure orchestration pipelines can still run because they do not clone a repository.
            logger.LogWarning(ex, "Could not resolve head commit for project {ProjectId}", projectId);
            return null;
        }
    }

    /// <summary>The head commit of a repository already read: the branch asked for, else its default.</summary>
    private async Task<string?> GetHeadCommitShaAsync(
        int projectId, GitLightRepoDto? repo, string? sourceBranch, CancellationToken ct)
    {
        if (repo is null || repo.IsEmpty) return null;

        var diskPath = SafeDiskPath(projectId, repo);
        if (diskPath is null || !Directory.Exists(diskPath)) return null;

        var branch = string.IsNullOrWhiteSpace(sourceBranch)
            ? string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "HEAD" : repo.DefaultBranch
            : sourceBranch;
        var commits = await cli.GetCommitsAsync(diskPath, branch, skip: 0, take: 1, ct: ct).ConfigureAwait(false);
        return commits.FirstOrDefault()?.Sha;
    }

    public async Task<PipelineSourceDto?> GetPipelineSourceAsync(
        int projectId, string pipelineName, CancellationToken ct = default,
        string? sourceBranch = null, int? sourceRepositoryId = null)
    {
        // A read, so the stored repository row (no default-branch detection, which is two git
        // processes and a possible write), read once and reused for the head commit below.
        var repo = await GetStoredProjectRepoAsync(projectId, sourceRepositoryId, ct).ConfigureAwait(false);
        if (repo is null) return null;

        var branch = string.IsNullOrWhiteSpace(sourceBranch)
            ? string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch
            : sourceBranch;
        string? commit;
        try
        {
            commit = await GetHeadCommitShaAsync(projectId, repo, sourceBranch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Mandatory, as in the public lookup: an unreadable head is an unresolved source the
            // caller handles (workspace pipelines fail closed on it), never a failed page.
            logger.LogWarning(ex, "Could not resolve head commit for project {ProjectId}", projectId);
            commit = null;
        }
        return new PipelineSourceDto
        {
            RepositoryId = repo.Id,
            CloneUrl = repo.CloneUrl ?? string.Empty,
            Path = $"{PipelineDir}/{PipelineGitPathPolicy.Slugify(pipelineName)}.yaml",
            Branch = branch,
            CommitHash = commit
        };
    }

    public async Task<(GitWriteOutcome Outcome, string? Error)> WriteProjectPipelineYamlAsync(
        int projectId, string pipelineName, string yaml, string actor, CancellationToken ct = default,
        string? sourceBranch = null, int? sourceRepositoryId = null)
    {
        var repo = await GetProjectRepoAsync(projectId, sourceRepositoryId, ct).ConfigureAwait(false);
        // No internal repo: the project is external/DB-only - a non-error fallback, not a git failure.
        if (repo is null) return (GitWriteOutcome.NoRepo, null);

        // An internal repo row with no bare repo on disk is a real, surfaceable failure: git is the
        // source of truth, so we must not silently fall back to a DB-only definition.
        var diskPath = SafeDiskPath(projectId, repo);
        if (diskPath is null || !Directory.Exists(diskPath)) return (GitWriteOutcome.Failed, "repository not initialized on disk");

        var branch = string.IsNullOrWhiteSpace(sourceBranch)
            ? string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch
            : sourceBranch;
        var relPath = $"{PipelineDir}/{PipelineGitPathPolicy.Slugify(pipelineName)}.yaml";
        var (ok, _, error) = await cli.CommitFileAsync(
            diskPath, branch, relPath, yaml,
            $"chore(pipeline): update {pipelineName} via Aetheus" + RequestedBy(actor),
            ServiceAuthorName, await AuthorEmailAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await RecordIfCommittedAsync(repo.Id, ok, ct).ConfigureAwait(false);
        if (!ok) logger.LogWarning("Failed to write pipeline '{Name}' to {Repo}: {Error}", pipelineName, repo.Slug, error);
        return (ok ? GitWriteOutcome.Committed : GitWriteOutcome.Failed, error);
    }

    public async Task<(GitWriteOutcome Outcome, string? Error)> ApplyProjectPipelineChangeAsync(
        PipelineGitDefinitionLocation? previous,
        PipelineGitDefinitionLocation? next,
        string? nextYaml,
        string actor,
        CancellationToken ct = default)
    {
        if (next is not null && nextYaml is null)
            throw new ArgumentNullException(nameof(nextYaml));

        var (oldTarget, oldError) = await ResolveWriteTargetAsync(previous, ct).ConfigureAwait(false);
        if (oldError is not null) return (GitWriteOutcome.Failed, oldError);
        var (newTarget, newError) = await ResolveWriteTargetAsync(next, ct).ConfigureAwait(false);
        if (newError is not null) return (GitWriteOutcome.Failed, newError);
        if (oldTarget is null && newTarget is null) return (GitWriteOutcome.NoRepo, null);

        if (TargetsSameBranch(oldTarget, newTarget))
            return await UpdateSameTargetAsync(
                oldTarget!, newTarget!, next!, nextYaml!, actor, ct).ConfigureAwait(false);
        return await MoveTargetAsync(
            previous, oldTarget, next, newTarget, nextYaml, actor, ct).ConfigureAwait(false);
    }

    private static bool TargetsSameBranch(PipelineWriteTarget? oldTarget, PipelineWriteTarget? newTarget) =>
        oldTarget is not null
        && newTarget is not null
        && string.Equals(oldTarget.DiskPath, newTarget.DiskPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(oldTarget.Branch, newTarget.Branch, StringComparison.Ordinal);

    private async Task<(GitWriteOutcome Outcome, string? Error)> UpdateSameTargetAsync(
        PipelineWriteTarget oldTarget,
        PipelineWriteTarget newTarget,
        PipelineGitDefinitionLocation next,
        string nextYaml,
        string actor,
        CancellationToken ct)
    {
        var deletions = string.Equals(oldTarget.RelativePath, newTarget.RelativePath, StringComparison.Ordinal)
            ? Array.Empty<string>()
            : [oldTarget.RelativePath];
        var (ok, _, error) = await cli.CommitFileChangesAsync(
            newTarget.DiskPath, newTarget.Branch,
            [(newTarget.RelativePath, nextYaml)], deletions,
            $"chore(pipeline): update {next.PipelineName} via Aetheus" + RequestedBy(actor),
            ServiceAuthorName, await AuthorEmailAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await RecordIfCommittedAsync(newTarget.RepositoryId, ok, ct).ConfigureAwait(false);
        return (ok ? GitWriteOutcome.Committed : GitWriteOutcome.Failed, error);
    }

    private async Task<(GitWriteOutcome Outcome, string? Error)> MoveTargetAsync(
        PipelineGitDefinitionLocation? previous,
        PipelineWriteTarget? oldTarget,
        PipelineGitDefinitionLocation? next,
        PipelineWriteTarget? newTarget,
        string? nextYaml,
        string actor,
        CancellationToken ct)
    {
        if (newTarget is not null)
        {
            var (written, _, writeError) = await cli.CommitFileChangesAsync(
                newTarget.DiskPath, newTarget.Branch,
                [(newTarget.RelativePath, nextYaml!)], [],
                $"chore(pipeline): add {next!.PipelineName} via Aetheus" + RequestedBy(actor),
                ServiceAuthorName, await AuthorEmailAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            await RecordIfCommittedAsync(newTarget.RepositoryId, written, ct).ConfigureAwait(false);
            if (!written) return (GitWriteOutcome.Failed, writeError);
        }

        if (oldTarget is not null)
        {
            var (deleted, _, deleteError) = await cli.CommitFileChangesAsync(
                oldTarget.DiskPath, oldTarget.Branch, [], [oldTarget.RelativePath],
                $"chore(pipeline): remove {previous!.PipelineName} via Aetheus" + RequestedBy(actor),
                ServiceAuthorName, await AuthorEmailAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            await RecordIfCommittedAsync(oldTarget.RepositoryId, deleted, ct).ConfigureAwait(false);
            if (!deleted)
            {
                if (newTarget is not null)
                {
                    var (rolledBack, _, rollbackError) = await cli.CommitFileChangesAsync(
                        newTarget.DiskPath, newTarget.Branch, [], [newTarget.RelativePath],
                        $"revert(pipeline): roll back {next!.PipelineName} move" + RequestedBy(actor),
                        ServiceAuthorName, await AuthorEmailAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    await RecordIfCommittedAsync(newTarget.RepositoryId, rolledBack, ct).ConfigureAwait(false);
                    if (!rolledBack)
                        return (GitWriteOutcome.Failed,
                            $"{deleteError}; destination rollback also failed: {rollbackError}");
                }
                return (GitWriteOutcome.Failed, deleteError);
            }
        }
        return (GitWriteOutcome.Committed, null);
    }

    private async Task<(PipelineWriteTarget? Target, string? Error)> ResolveWriteTargetAsync(
        PipelineGitDefinitionLocation? location, CancellationToken ct)
    {
        if (location is null) return (null, null);
        var repo = await GetProjectRepoAsync(
            location.ProjectId, location.SourceRepositoryId, ct).ConfigureAwait(false);
        if (repo is null) return (null, null);
        var diskPath = SafeDiskPath(location.ProjectId, repo);
        if (diskPath is null || !Directory.Exists(diskPath))
            return (null, "repository not initialized on disk");
        var branch = string.IsNullOrWhiteSpace(location.SourceBranch)
            ? string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch
            : location.SourceBranch;
        return (new PipelineWriteTarget(
            repo.Id, diskPath, branch, $"{PipelineDir}/{PipelineGitPathPolicy.Slugify(location.PipelineName)}.yaml"), null);
    }

    private sealed record PipelineWriteTarget(int RepositoryId, string DiskPath, string Branch, string RelativePath);

    private async Task RecordIfCommittedAsync(int repositoryId, bool committed, CancellationToken ct)
    {
        if (committed) await commitRecorder.RecordCommitAsync(repositoryId, ct).ConfigureAwait(false);
    }

    public async Task<int> CopyEnvironmentPipelinesToProjectAsync(
        int environmentId, string environmentName, int projectId, string actor, CancellationToken ct = default)
    {
        var repo = await GetProjectRepoAsync(projectId, sourceRepositoryId: null, ct: ct).ConfigureAwait(false);
        if (repo is null) return 0; // external-repo projects: nothing to copy into

        var diskPath = SafeDiskPath(projectId, repo);
        if (diskPath is null || !Directory.Exists(diskPath)) return 0;

        var branch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch;
        var pipelines = await pipelineRepo.GetPipelinesByEnvironmentAsync(environmentId, ct).ConfigureAwait(false);

        // F-007: one clone + one commit + one push for the whole batch - the copy runs inside the
        // UpdateEnvironment request path and must not scale its latency with the pipeline count.
        var files = pipelines
            .Where(p => !string.IsNullOrWhiteSpace(p.YamlDefinition))
            .Select(p => (RelativePath: $"{PipelineDir}/{PipelineGitPathPolicy.Slugify(environmentName)}-{PipelineGitPathPolicy.Slugify(p.Name)}.yaml", Content: p.YamlDefinition))
            .ToList();
        if (files.Count == 0) return 0;

        var (ok, _, error) = await cli.CommitFilesAsync(
            diskPath, branch, files,
            $"chore(pipeline): copy {files.Count} pipeline(s) from environment '{environmentName}' via Aetheus" + RequestedBy(actor),
            ServiceAuthorName, await AuthorEmailAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await RecordIfCommittedAsync(repo.Id, ok, ct).ConfigureAwait(false);
        if (!ok)
        {
            logger.LogWarning("Failed to copy env '{Env}' pipelines to project {ProjectId}: {Error}", environmentName, projectId, error);
            return 0;
        }
        return files.Count;
    }

    // A project with several repositories must bind the pipeline to one repository explicitly.
    // Falling back to the first alphabetical repository made the YAML source and checkout source
    // diverge, which can run a valid pipeline against an empty or unrelated workspace.
    private async Task<GitLightRepoDto?> GetProjectRepoAsync(
        int projectId, int? sourceRepositoryId, CancellationToken ct) =>
        SelectProjectRepo(
            await gitService.GetRepositoriesAsync(projectId, ct).ConfigureAwait(false),
            projectId, sourceRepositoryId);

    /// <summary> The project's repository as stored, without the default-branch detection <see
    /// cref="IGitLightService.GetRepositoriesAsync"/> runs on every call (two git processes per
    /// repository, and a write when it drifts). A read of the source names the stored default
    /// branch, which a push (<c>GitSmartHttpService</c>) and the nightly maintenance keep in step
    /// with the disk. </summary>
    private async Task<GitLightRepoDto?> GetStoredProjectRepoAsync(
        int projectId, int? sourceRepositoryId, CancellationToken ct) =>
        SelectProjectRepo(
            await gitService.GetAccessibleRepositoriesAsync([projectId], ct).ConfigureAwait(false),
            projectId, sourceRepositoryId);

    private static GitLightRepoDto? SelectProjectRepo(
        List<GitLightRepoDto> repos, int projectId, int? sourceRepositoryId)
    {
        if (sourceRepositoryId is { } repositoryId)
            return repos.FirstOrDefault(repo => repo.Id == repositoryId)
                ?? throw new BadRequestException(
                    $"Source repository {repositoryId} does not belong to project {projectId}.");
        // The mirror of an external repository attached beside the project's repository is a source
        // a pipeline names (source: repository:), never where a definition is read by default.
        var own = repos.Where(repo => !repo.IsAdditionalSource).ToList();
        return own.Count switch
        {
            0 => null,
            1 => own[0],
            _ => throw new BadRequestException(
                $"Project {projectId} has multiple repositories; select an explicit pipeline source repository.")
        };
    }

    private string? SafeDiskPath(int projectId, GitLightRepoDto repo)
    {
        try { return gitService.ResolveDiskPath(projectId, repo.Slug); }
        catch (Exception ex) { logger.LogWarning(ex, "Invalid repo path for {Slug}", repo.Slug); return null; }
    }

    private static bool IsYaml(GitLightTreeEntryDto e)
        => e.Type == GitTreeEntryType.Blob
           && (e.Name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
               || e.Name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

}
