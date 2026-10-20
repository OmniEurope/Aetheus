// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>What a run checks out when its definition declares a <c>source:</c> block.</summary>
public sealed record PipelineWorkspaceSource(int RepositoryId, string CloneUrl, string Branch, string CommitHash);

/// <summary> Resolves the <c>source:</c> block of a pipeline definition into the repository, branch
/// and immutable commit the run checks out, and refuses the run when that cannot be done honestly:
/// an unknown repository, a remote that cannot be fetched, a branch that cannot be pinned, or a
/// tree that is not the one the definition's revision holds. </summary>
public interface IPipelineWorkspaceSourceResolver
{
    /// <param name="definitionRepositoryId">The repository the definition was read from, when the
    /// pipeline is bound to one.</param>
    /// <param name="definitionCommit">The revision the definition was read at.</param>
    /// <param name="pinnedCommit">A commit of the source repository to check out instead of the head of
    /// its branch: a rerun rebuilding exactly what an earlier run built. Null takes the branch head.</param>
    /// <exception cref="BadRequestException">The source cannot be used; the message says why.</exception>
    Task<PipelineWorkspaceSource> ResolveAsync(
        int projectId,
        PipelineSourceDefinition source,
        int? definitionRepositoryId,
        string? definitionCommit,
        CancellationToken ct,
        string? pinnedCommit = null);
}

public sealed class PipelineWorkspaceSourceResolver(
    IGitLightService gitService,
    IGitLightCliService cli,
    IPipelineGitService pipelineGit,
    IExternalMirrorRefresher mirrors) : IPipelineWorkspaceSourceResolver
{
    /// <summary>How many differing paths a refusal names; the count says the rest.</summary>
    private const int ReportedPaths = 10;

    public async Task<PipelineWorkspaceSource> ResolveAsync(
        int projectId,
        PipelineSourceDefinition source,
        int? definitionRepositoryId,
        string? definitionCommit,
        CancellationToken ct,
        string? pinnedCommit = null)
    {
        var slug = source.Repository.Trim();
        // A mirror is fetched now: the run builds what the remote holds at this moment, not what the
        // last scheduled fetch saw. For the project's own repository there is nothing to fetch.
        var refreshError = await mirrors.RefreshAsync(projectId, slug, ct).ConfigureAwait(false);
        if (refreshError is not null)
            throw new BadRequestException($"Source repository '{slug}' could not be brought up to date: {refreshError}");

        var repositories = await gitService.GetRepositoriesAsync(projectId, ct).ConfigureAwait(false);
        var repository = repositories.FirstOrDefault(candidate =>
                string.Equals(candidate.Slug, slug, StringComparison.OrdinalIgnoreCase))
            ?? throw new BadRequestException($"Source repository '{slug}' is not attached to this pipeline's project.");
        if (repository.IsEmpty || string.IsNullOrWhiteSpace(repository.CloneUrl))
            throw new BadRequestException($"Source repository '{slug}' is empty.");

        var branch = string.IsNullOrWhiteSpace(source.Branch) ? repository.DefaultBranch : source.Branch.Trim();
        var diskPath = gitService.ResolveDiskPath(projectId, repository.Slug);
        var pinned = PipelineRunHelpers.IsGitCommitHash(pinnedCommit) ? pinnedCommit : null;
        var head = await cli.GetCommitsAsync(diskPath, pinned ?? branch, skip: 0, take: 1, ct: ct).ConfigureAwait(false);
        var commit = head.FirstOrDefault()?.Sha;
        if (pinned is not null && !string.Equals(commit, pinned, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException($"Commit '{pinned}' is not in source repository '{slug}' any more.");
        if (!PipelineRunHelpers.IsGitCommitHash(commit))
            throw new BadRequestException($"Branch '{branch}' of source repository '{slug}' could not be pinned to a commit.");

        if (source.MustMatchDefinition)
        {
            await EnsureMatchesDefinitionAsync(
                projectId, source, repositories, repository, diskPath, commit!,
                definitionRepositoryId, definitionCommit, ct).ConfigureAwait(false);
        }

        return new PipelineWorkspaceSource(repository.Id, repository.CloneUrl!, branch, commit!);
    }

    private async Task EnsureMatchesDefinitionAsync(
        int projectId,
        PipelineSourceDefinition source,
        IReadOnlyList<GitLightRepoDto> repositories,
        GitLightRepoDto sourceRepository,
        string sourceDiskPath,
        string sourceCommit,
        int? definitionRepositoryId,
        string? definitionCommit,
        CancellationToken ct)
    {
        if (!PipelineRunHelpers.IsGitCommitHash(definitionCommit))
            throw new BadRequestException(
                "source.must_match_definition needs the definition's repository pinned to a commit.");
        var definitionRepository = (definitionRepositoryId is { } id
                ? repositories.FirstOrDefault(candidate => candidate.Id == id)
                : repositories.Where(candidate => !candidate.IsAdditionalSource).Take(2).ToList() is [var only] ? only : null)
            ?? throw new BadRequestException(
                "source.must_match_definition needs the repository the definition is read from.");

        var definitionDiskPath = gitService.ResolveDiskPath(projectId, definitionRepository.Slug);
        var (reference, referenceName) = await ResolveReferenceAsync(
            source, definitionRepository, definitionDiskPath, definitionCommit!, ct).ConfigureAwait(false);

        var excluded = await ReadExcludedPathsAsync(
            projectId, source.MatchExcludeFile, definitionCommit!, definitionRepository.Id, ct).ConfigureAwait(false);
        var referenceTree = await cli.GetTreeBlobsAsync(definitionDiskPath, reference, ct).ConfigureAwait(false);
        var sourceTree = await cli.GetTreeBlobsAsync(sourceDiskPath, sourceCommit, ct).ConfigureAwait(false);
        if (referenceTree is null || sourceTree is null)
            throw new BadRequestException(
                $"Source repository '{sourceRepository.Slug}' could not be compared with {referenceName}: a tree could not be listed.");

        var differences = Compare(referenceTree, sourceTree, excluded);
        if (differences.Count == 0) return;

        var named = string.Join(", ", differences.Take(ReportedPaths));
        var more = differences.Count > ReportedPaths ? $", and {differences.Count - ReportedPaths} more" : string.Empty;
        throw new BadRequestException(
            $"Source repository '{sourceRepository.Slug}' at {Short(sourceCommit)} is not the tree of {referenceName} "
            + $"{Short(reference)}: {differences.Count} path(s) differ ({named}{more}).");
    }

    /// <summary>
    /// The revision of the definition's repository the source must equal: the head of
    /// <c>match_branch</c> when the block names one, else the revision the definition was read at.
    /// </summary>
    private async Task<(string Commit, string Name)> ResolveReferenceAsync(
        PipelineSourceDefinition source,
        GitLightRepoDto definitionRepository,
        string definitionDiskPath,
        string definitionCommit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.MatchBranch))
            return (definitionCommit, "the definition's revision");

        var branch = source.MatchBranch.Trim();
        var head = await cli.GetCommitsAsync(definitionDiskPath, branch, skip: 0, take: 1, ct: ct).ConfigureAwait(false);
        return head.FirstOrDefault()?.Sha is { } commit && PipelineRunHelpers.IsGitCommitHash(commit)
            ? (commit, $"branch '{branch}' of '{definitionRepository.Slug}'")
            : throw new BadRequestException(
                $"source.match_branch '{branch}' could not be pinned to a commit in repository '{definitionRepository.Slug}'.");
    }

    /// <summary>
    /// The paths on which the two trees disagree, once the excluded paths are taken out of the
    /// definition's tree: missing from the source, present only in the source, or different content.
    /// </summary>
    internal static List<string> Compare(
        IReadOnlyDictionary<string, string> definitionTree,
        IReadOnlyDictionary<string, string> sourceTree,
        IReadOnlyList<string> excluded)
    {
        var expected = definitionTree
            .Where(entry => !excluded.Any(pattern => PipelinePathFilter.Matches(pattern, entry.Key)))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

        var differences = new List<string>();
        foreach (var (path, blob) in expected.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!sourceTree.TryGetValue(path, out var other)) differences.Add($"missing: {path}");
            else if (!string.Equals(blob, other, StringComparison.Ordinal)) differences.Add($"changed: {path}");
        }
        differences.AddRange(sourceTree.Keys
            .Where(path => !expected.ContainsKey(path))
            .Order(StringComparer.Ordinal)
            .Select(path => $"unexpected: {path}"));
        return differences;
    }

    private async Task<IReadOnlyList<string>> ReadExcludedPathsAsync(
        int projectId, string? excludeFile, string definitionCommit, int definitionRepositoryId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(excludeFile)) return [];
        var content = await pipelineGit.ReadProjectConfigAtRevisionAsync(
                projectId, excludeFile, definitionCommit, ct, definitionRepositoryId).ConfigureAwait(false)
            ?? throw new BadRequestException(
                $"source.match_exclude_file '{excludeFile}' was not found at the definition's revision {Short(definitionCommit)}.");
        return ParseExcludedPaths(content);
    }

    /// <summary>One glob per line; blank lines and lines starting with <c>#</c> are skipped.</summary>
    internal static IReadOnlyList<string> ParseExcludedPaths(string content) =>
        content.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

    private static string Short(string commit) => commit.Length > 8 ? commit[..8] : commit;
}
