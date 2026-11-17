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

        // Decision 2026-11-10: a deployment configuration never comes from untrusted content, so the mirror of an
        // external repository is never read here either, under the same rule as a definition (null, never a throw:
        // each caller says the template is not read from a mirror, see IsExternalMirrorDefinitionSourceAsync).
        var repo = await GetStoredDefinitionRepoAsync(projectId, sourceRepositoryId, ct).ConfigureAwait(false);
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
        var repo = await GetStoredProjectRepoAsync(projectId, sourceRepositoryId, ct).ConfigureAwait(false);
        if (repo is null || repo.IsEmpty) return null;
        var diskPath = SafeDiskPath(projectId, repo);
        if (diskPath is null || !Directory.Exists(diskPath)) return null;
        var blob = await cli.GetBlobAsync(diskPath, revision, "Directory.Build.props", ct).ConfigureAwait(false);
        return blob is null || blob.IsBinary || blob.Size > 256 * 1024 ? null : blob.Content;
    }

    public async Task<bool> IsExternalMirrorDefinitionSourceAsync(
        int projectId, int? sourceRepositoryId, CancellationToken ct = default)
    {
        var repositories = await gitService.GetAccessibleRepositoriesAsync([projectId], ct).ConfigureAwait(false);
        if (sourceRepositoryId is { } named)
            return repositories.Exists(repo => repo.Id == named && repo.IsExternalMirror);
        var own = repositories.Where(repo => !repo.IsAdditionalSource && !repo.IsConfiguration).ToList();
        return own.Exists(repo => repo.IsExternalMirror) && !own.Exists(repo => !repo.IsExternalMirror);
    }

    private async Task<string?> ReadProjectPipelineYamlCoreAsync(
        int projectId, string pipelineName, string? revision, int? sourceRepositoryId,
        CancellationToken ct)
    {
        var repo = await GetStoredDefinitionRepoAsync(projectId, sourceRepositoryId, ct).ConfigureAwait(false);
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
            var repo = await GetStoredProjectRepoAsync(projectId, sourceRepositoryId, ct).ConfigureAwait(false);
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
        if (!ok)
        {
            logger.LogWarning("Failed to write pipeline '{Name}' to {Repo}: {Error}", pipelineName, repo.Slug, error);
            return (GitWriteOutcome.Failed, error);
        }
        var followed = await KeepIntegrationBranchInStepAsync(
            [new PipelineWriteTarget(repo.Id, diskPath, branch, relPath, DefaultBranchOf(repo))], ct).ConfigureAwait(false);
        return (GitWriteOutcome.Committed, followed);
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

        var (outcome, error) = TargetsSameBranch(oldTarget, newTarget)
            ? await UpdateSameTargetAsync(
                oldTarget!, newTarget!, next!, nextYaml!, actor, ct).ConfigureAwait(false)
            : await MoveTargetAsync(
                previous, oldTarget, next, newTarget, nextYaml, actor, ct).ConfigureAwait(false);
        if (outcome != GitWriteOutcome.Committed) return (outcome, error);

        var followed = await KeepIntegrationBranchInStepAsync(
            [.. new[] { newTarget, oldTarget }.OfType<PipelineWriteTarget>()], ct).ConfigureAwait(false);
        return (GitWriteOutcome.Committed, followed);
    }

    /// <summary>R-38. A definition written on the branch the deployments advance (the repository's default branch, which the
    /// deploy pipelines' <c>advance-branch</c> step moves onto each deployed release) puts a commit on it that no release
    /// built from the integration branch contains: the next "Advance main" would find the branch diverged and refuse.
    /// The integration branch therefore takes that branch back in a merge, so every later release descends from it and the
    /// branch stays fast-forwardable. A copy of the change committed on the integration branch would not do: it is a
    /// different commit, so the branch would still hold one the releases lack. The merge writes the integration ref
    /// directly, it is not a push, so it triggers no pipeline. Returns null when nothing was needed or the merge was
    /// made, else the reason it could not be made (the pipeline change itself is stored either way): the caller reports
    /// it, the branch has to be merged by hand.</summary>
    private async Task<string?> KeepIntegrationBranchInStepAsync(
        IReadOnlyList<PipelineWriteTarget> committedTargets, CancellationToken ct)
    {
        string? failure = null;
        foreach (var target in committedTargets.DistinctBy(t => (t.DiskPath.ToUpperInvariant(), t.Branch)))
        {
            if (!string.Equals(target.Branch, target.DefaultBranch, StringComparison.Ordinal)
                || string.Equals(target.Branch, IntegrationBranch, StringComparison.Ordinal))
                continue;
            var branches = await cli.GetBranchesAsync(target.DiskPath, target.DefaultBranch, ct).ConfigureAwait(false);
            if (!branches.Exists(b => string.Equals(b.Name, IntegrationBranch, StringComparison.Ordinal))) continue;

            var (merged, _, error) = await cli.MergeBranchesAsync(
                target.DiskPath, target.Branch, IntegrationBranch,
                ServiceAuthorName, await AuthorEmailAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            if (merged)
            {
                await RecordIfCommittedAsync(target.RepositoryId, true, ct).ConfigureAwait(false);
                continue;
            }
            logger.LogWarning(
                "Pipeline change committed on '{Branch}' but '{Integration}' could not take it back: {Error}",
                target.Branch, IntegrationBranch, error);
            failure = $"the change is committed on '{target.Branch}' but '{IntegrationBranch}' could not take it back ({error}): "
                + $"merge '{target.Branch}' into '{IntegrationBranch}' by hand, or the next deployment cannot advance '{target.Branch}'.";
        }
        return failure;
    }

    // The branch the integration work happens on, which the default branch is meant to follow.
    private const string IntegrationBranch = GitDevelopBranchProvisioner.BranchName;

    private static string DefaultBranchOf(GitLightRepoDto repo) =>
        string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch;

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
            repo.Id, diskPath, branch, $"{PipelineDir}/{PipelineGitPathPolicy.Slugify(location.PipelineName)}.yaml",
            DefaultBranchOf(repo)), null);
    }

    private sealed record PipelineWriteTarget(
        int RepositoryId, string DiskPath, string Branch, string RelativePath, string DefaultBranch);

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
        files = await WithoutFilesTheRepositoryAlreadyHoldsAsync(diskPath, branch, files, projectId, ct).ConfigureAwait(false);
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

    // R-95: a copy never overwrites a file the project's repository already holds. A file with the same
    // content has nothing to write; one with other content is a change Aetheus has not seen and is kept
    // (logged: the copy runs in the background, there is no caller to answer to).
    private async Task<List<(string RelativePath, string Content)>> WithoutFilesTheRepositoryAlreadyHoldsAsync(
        string diskPath, string branch, List<(string RelativePath, string Content)> files, int projectId,
        CancellationToken ct)
    {
        var toWrite = new List<(string RelativePath, string Content)>();
        foreach (var file in files)
        {
            var existing = await cli.GetBlobAsync(diskPath, branch, file.RelativePath, ct).ConfigureAwait(false);
            if (existing is null || (!existing.IsBinary && string.IsNullOrWhiteSpace(existing.Content)))
            {
                toWrite.Add(file);
                continue;
            }
            if (!existing.IsBinary && PipelineRepositoryDefinitionGuard.Same(existing.Content, file.Content)) continue;
            logger.LogWarning(
                "Not copying to {Path} in project {ProjectId}: the repository already holds a file there with different content.",
                file.RelativePath, projectId);
        }
        return toWrite;
    }

    // A project with several repositories must bind the pipeline to one repository explicitly.
    // Falling back to the first alphabetical repository made the YAML source and checkout source
    // diverge, which can run a valid pipeline against an empty or unrelated workspace.
    // The repository of a write: its default branch is detected on the disk first (see
    // GetStoredProjectRepoAsync for the reads, which do not pay for that).
    // R-12 (decision R-23): the external repository is never modified, and its internal mirror is refreshed by a forced
    // fetch of every ref, which would erase a commit Aetheus made on it. A mirror is therefore never a write target: a
    // project whose source is one is "external/DB-only" for definitions (NoRepo), exactly as a project with no repository.
    private async Task<GitLightRepoDto?> GetProjectRepoAsync(
        int projectId, int? sourceRepositoryId, CancellationToken ct)
    {
        var repositories = await gitService.GetRepositoriesAsync(projectId, ct).ConfigureAwait(false);
        if (sourceRepositoryId is { } named && repositories.Exists(repo => repo.Id == named && repo.IsExternalMirror))
            return null;
        return SelectProjectRepo(repositories.Where(repo => !repo.IsExternalMirror).ToList(), projectId, sourceRepositoryId);
    }

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

    /// <summary>The repository a pipeline definition is read from, as stored. Decision 2026-11-10 (point 4): the
    /// mirror of an external repository is untrusted (whoever can push to the external repository writes it), so it
    /// is never a definition source, exactly as it is never a write target (<see cref="GetProjectRepoAsync"/>): a
    /// pipeline whose source repository is a mirror reads nothing here and keeps Aetheus's stored definition, one
    /// with no named source reads the project's internal repository only. The mirror stays the run's workspace
    /// (<see cref="GetPipelineSourceAsync"/>); only the explicit import reads its <c>.pipeline/</c>.</summary>
    private async Task<GitLightRepoDto?> GetStoredDefinitionRepoAsync(
        int projectId, int? sourceRepositoryId, CancellationToken ct)
    {
        var repositories = await gitService.GetAccessibleRepositoriesAsync([projectId], ct).ConfigureAwait(false);
        if (sourceRepositoryId is { } named && repositories.Exists(repo => repo.Id == named && repo.IsExternalMirror))
            return null;
        return SelectProjectRepo(repositories.Where(repo => !repo.IsExternalMirror).ToList(), projectId, sourceRepositoryId);
    }

    private static GitLightRepoDto? SelectProjectRepo(
        List<GitLightRepoDto> repos, int projectId, int? sourceRepositoryId)
    {
        if (sourceRepositoryId is { } repositoryId)
            return repos.FirstOrDefault(repo => repo.Id == repositoryId)
                ?? throw new BadRequestException(
                    $"Source repository {repositoryId} does not belong to project {projectId}.");
        // The mirror of an external repository attached beside the project's repository is a source
        // a pipeline names (source: repository:), never where a definition is read by default.
        var own = repos.Where(repo => !repo.IsAdditionalSource && !repo.IsConfiguration).ToList();
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
