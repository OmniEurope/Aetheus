// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Git;

public readonly record struct GitPatchResult(string Patch, bool IsTruncated);

public interface IGitLightCliService
{
    Task InitBareRepoAsync(string diskPath, string defaultBranch = "main", CancellationToken ct = default);
    Task DeleteRepoAsync(string diskPath, CancellationToken ct = default);
    /// <summary>Uses <paramref name="afterSha"/> for O(take) cursor pagination, falling back to
    /// O(skip + take) traversal with a deep-skip warning. <paramref name="filter"/> carries column
    /// filters; its branches override <paramref name="refName"/> as history roots.</summary>
    Task<List<GitLightCommitDto>> GetCommitsAsync(string diskPath, string? refName, int skip, int take, string? search = null, string? afterSha = null, CancellationToken ct = default, GitCommitLogFilter? filter = null);
    Task<Dictionary<string, string>> GetCommitMessagesAsync(string diskPath, IReadOnlyCollection<string> shas, CancellationToken ct = default);
    Task<int> GetCommitCountAsync(string diskPath, string? refName, string? search = null, CancellationToken ct = default, GitCommitLogFilter? filter = null);

    /// <summary>The distinct author names of the most recent commits of every branch, for the
    /// commits grid's Author column filter.</summary>
    Task<List<string>> GetCommitAuthorsAsync(string diskPath, CancellationToken ct = default);
    Task<List<GitLightBranchDto>> GetBranchesAsync(string diskPath, string defaultBranch, CancellationToken ct = default);
    Task<List<GitLightTagDto>> GetTagsAsync(string diskPath, CancellationToken ct = default);
    Task CreateBranchAsync(string diskPath, string branchName, string? startRef, CancellationToken ct = default);
    Task DeleteBranchAsync(string diskPath, string branchName, CancellationToken ct = default);
    Task CreateTagAsync(string diskPath, string tagName, string? refName, string? message, CancellationToken ct = default);
    Task DeleteTagAsync(string diskPath, string tagName, CancellationToken ct = default);
    Task<List<GitLightTreeEntryDto>> GetTreeAsync(string diskPath, string refName, string? path, CancellationToken ct = default);
    Task<GitLightBlobDto?> GetBlobAsync(string diskPath, string refName, string path, CancellationToken ct = default);
    Task<Stream?> GetBlobStreamAsync(string diskPath, string refName, string path, CancellationToken ct = default);

    /// <summary><c>git archive --format=zip --prefix=&lt;prefix&gt;/ &lt;ref&gt;</c>, streamed.
    /// Null when the ref names no tree (checked before the first byte, so a bad ref is a 404, not a
    /// cut zip).</summary>
    Task<Stream?> GetArchiveStreamAsync(string diskPath, string refName, string prefix, CancellationToken ct = default);
    Task<(bool Success, string? MergeCommitSha, string? Error)> MergeBranchesAsync(string diskPath, string source, string target, string authorName, string authorEmail, CancellationToken ct = default);

    /// <summary>
    /// Commits <paramref name="content"/> to <paramref name="relativePath"/> on <paramref name="branch"/>
    /// of the (bare) repo at <paramref name="diskPath"/>, via a throwaway temp clone + push. Works for
    /// empty repos (creates the branch). Returns Success=false with an Error on failure; a no-op
    /// (content unchanged) is treated as Success.
    /// </summary>
    Task<(bool Success, string? CommitSha, string? Error)> CommitFileAsync(
        string diskPath, string branch, string relativePath, string content,
        string commitMessage, string authorName, string authorEmail, CancellationToken ct = default);
    /// <summary>Commits N files in one clone + commit + push round-trip (single commit).</summary>
    Task<(bool Success, string? CommitSha, string? Error)> CommitFilesAsync(
        string diskPath, string branch, IReadOnlyList<(string RelativePath, string Content)> files,
        string commitMessage, string authorName, string authorEmail, CancellationToken ct = default);
    /// <summary>Commits file upserts and deletions atomically in one clone, commit and push.</summary>
    Task<(bool Success, string? CommitSha, string? Error)> CommitFileChangesAsync(
        string diskPath, string branch,
        IReadOnlyList<(string RelativePath, string Content)> upserts,
        IReadOnlyList<string> deletions,
        string commitMessage, string authorName, string authorEmail, CancellationToken ct = default);
    Task<PullRequestDiffDto> GetDiffAsync(string diskPath, string fromRef, string toRef, CancellationToken ct = default);
    Task<GitPatchResult> GetCommitPatchAsync(
        string diskPath,
        string fromRef,
        string toRef,
        CancellationToken ct = default);
    Task<GitPatchResult> GetRootCommitPatchAsync(
        string diskPath,
        string commitRef,
        CancellationToken ct = default);

    /// <summary>The paths a range touched, names only. Null when git could not answer, which a
    /// caller must not confuse with "nothing changed".</summary>
    Task<IReadOnlyList<string>?> GetChangedPathsAsync(
        string diskPath,
        string fromRef,
        string toRef,
        CancellationToken ct = default);
    /// <summary>Every file of a revision with its mode and blob id (path to "mode sha"); null when the
    /// listing could not be read in full.</summary>
    Task<IReadOnlyDictionary<string, string>?> GetTreeBlobsAsync(
        string diskPath, string revision, CancellationToken ct = default);
    Task<string> GetCommitGraphAsync(string diskPath, int maxCount, CancellationToken ct = default);
    Task RunGcAsync(string diskPath, CancellationToken ct = default);
    Task InstallPreReceiveHookAsync(string diskPath, CancellationToken ct = default);
    Task WriteProtectionConfigAsync(string diskPath, IReadOnlyList<(string Pattern, bool PreventDeletion, bool PreventForcePush)> rules, CancellationToken ct = default);
    Task<List<GitLightBlameLine>> GetBlameAsync(string diskPath, string refName, string path, CancellationToken ct = default);

    /// <summary>
    /// Detects the actual default branch of a bare repository by reading HEAD.
    /// Falls back to the first existing branch (priority: main, master, develop, trunk) if HEAD is unborn or invalid.
    /// Returns null if the repo has no branches at all.
    /// </summary>
    Task<string?> DetectDefaultBranchAsync(string diskPath, CancellationToken ct = default);
    Task SetHeadAsync(string diskPath, string branch, CancellationToken ct = default);
}
