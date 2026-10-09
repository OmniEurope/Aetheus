// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Configuration;

namespace Aetheus.Back.Components.Git;

/// <summary>
/// Content-mutating git operations (commit files, set HEAD, detect default branch). Extracted from
/// the former <c>GitLightCliService.Write.cs</c> partial into a real collaborator; shares the
/// underlying git executor via <see cref="GitProcessRunner"/>. <see cref="GitLightCliService"/>
/// delegates its write methods here so the public <see cref="IGitLightCliService"/> contract is
/// unchanged.
/// </summary>
public class GitLightCliWriter(GitProcessRunner git, ILogger<GitLightCliWriter> logger)
{
    public async Task<(bool Success, string? CommitSha, string? Error)> CommitFileAsync(
        string diskPath, string branch, string relativePath, string content,
        string commitMessage, string authorName, string authorEmail, CancellationToken ct = default)
        => await CommitFilesAsync(diskPath, branch, [(relativePath, content)], commitMessage, authorName, authorEmail, ct).ConfigureAwait(false);

    /// <summary>
    /// Commits N files in a single clone + commit + push round-trip (F-007: the env→project
    /// pipeline copy used to clone once per file inside the HTTP request path).
    /// </summary>
    public async Task<(bool Success, string? CommitSha, string? Error)> CommitFilesAsync(
        string diskPath, string branch, IReadOnlyList<(string RelativePath, string Content)> files,
        string commitMessage, string authorName, string authorEmail, CancellationToken ct = default)
        => await CommitFileChangesAsync(
            diskPath, branch, files, [], commitMessage, authorName, authorEmail, ct).ConfigureAwait(false);

    public async Task<(bool Success, string? CommitSha, string? Error)> CommitFileChangesAsync(
        string diskPath, string branch,
        IReadOnlyList<(string RelativePath, string Content)> upserts,
        IReadOnlyList<string> deletions,
        string commitMessage, string authorName, string authorEmail, CancellationToken ct = default)
    {
        if (upserts.Count == 0 && deletions.Count == 0) return (true, null, null);
        if (!IsValidRefName(branch))
            return (false, null, "invalid branch name");
        if (!TryNormalizeChanges(upserts, deletions, out var paths, out var pathError))
            return (false, null, pathError);
        var tmp = Path.Combine(Path.GetTempPath(), $"aetheus-commit-{Guid.NewGuid():N}");
        try
        {
            return await CommitChangesCoreAsync(
                diskPath, branch, upserts, paths, tmp, commitMessage, authorName, authorEmail, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, null, ex.Message);
        }
        finally
        {
            if (Directory.Exists(tmp))
                try { GitProcessRunner.ClearReadOnlyAttributes(tmp); Directory.Delete(tmp, true); }
                catch (Exception ex) { logger.LogDebug(ex, "Best-effort commit temp cleanup failed: {Dir}", tmp); }
        }
    }

    private static bool TryNormalizeChanges(
        IReadOnlyList<(string RelativePath, string Content)> upserts,
        IReadOnlyList<string> deletions,
        out NormalizedChanges changes,
        out string? error)
    {
        var safeUpserts = new List<string>(upserts.Count);
        foreach (var (relativePath, _) in upserts)
        {
            if (!TryNormalizeRelativePath(relativePath, out var safeRel))
            {
                changes = new([], []);
                error = $"invalid relative path: {relativePath}";
                return false;
            }
            safeUpserts.Add(safeRel);
        }
        var safeDeletions = new List<string>(deletions.Count);
        foreach (var relativePath in deletions)
        {
            if (!TryNormalizeRelativePath(relativePath, out var safeRel))
            {
                changes = new([], []);
                error = $"invalid relative path: {relativePath}";
                return false;
            }
            if (!safeUpserts.Contains(safeRel, StringComparer.Ordinal)) safeDeletions.Add(safeRel);
        }
        changes = new(safeUpserts, safeDeletions);
        error = null;
        return true;
    }

    private async Task<(bool Success, string? CommitSha, string? Error)> CommitChangesCoreAsync(
        string diskPath,
        string branch,
        IReadOnlyList<(string RelativePath, string Content)> upserts,
        NormalizedChanges paths,
        string tmp,
        string commitMessage,
        string authorName,
        string authorEmail,
        CancellationToken ct)
    {
        var cloneError = await CloneTargetBranchAsync(diskPath, branch, tmp, ct).ConfigureAwait(false);
        if (cloneError is not null) return (false, null, cloneError);
        await git.RunGitAsync(tmp, ["config", "user.name", authorName], ct).ConfigureAwait(false);
        await git.RunGitAsync(tmp, ["config", "user.email", authorEmail], ct).ConfigureAwait(false);
        var changeError = await ApplyChangesAsync(tmp, upserts, paths, ct).ConfigureAwait(false);
        if (changeError is not null) return (false, null, changeError);
        return await CommitAndPushAsync(tmp, branch, commitMessage, ct).ConfigureAwait(false);
    }

    private async Task<string?> CloneTargetBranchAsync(
        string diskPath, string branch, string tmp, CancellationToken ct)
    {
        var (exitClone, _, _) = await git.RunGitAsync(diskPath,
            ["clone", "--depth", "1", "--single-branch", "--no-tags", "--branch", branch, diskPath, tmp],
            ct, timeout: BackendRuntimeDefaults.GitWriteTimeout, ignoreExitCode: true).ConfigureAwait(false);
        if (exitClone == 0) return null;
        var (exitFull, _, errFull) = await git.RunGitAsync(
            diskPath, ["clone", diskPath, tmp], ct, timeout: BackendRuntimeDefaults.GitWriteTimeout)
            .ConfigureAwait(false);
        if (exitFull != 0) return $"clone failed: {errFull}";
        var (exitCheckout, _, _) = await git.RunGitAsync(
            tmp, ["checkout", branch], ct, ignoreExitCode: true).ConfigureAwait(false);
        if (exitCheckout != 0)
            await git.RunGitAsync(tmp, ["checkout", "-b", branch], ct, ignoreExitCode: true).ConfigureAwait(false);
        return null;
    }

    private async Task<string?> ApplyChangesAsync(
        string tmp,
        IReadOnlyList<(string RelativePath, string Content)> upserts,
        NormalizedChanges paths,
        CancellationToken ct)
    {
        for (var i = 0; i < upserts.Count; i++)
        {
            var target = Path.GetFullPath(Path.Combine(tmp, paths.Upserts[i]));
            if (!target.StartsWith(Path.GetFullPath(tmp) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return $"invalid relative path: {upserts[i].RelativePath}";
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, upserts[i].Content, ct).ConfigureAwait(false);
            await git.RunGitAsync(tmp, ["add", "--", paths.Upserts[i]], ct).ConfigureAwait(false);
        }
        foreach (var safeRel in paths.Deletions.Distinct(StringComparer.Ordinal))
            await git.RunGitAsync(
                tmp, ["rm", "--ignore-unmatch", "--", safeRel], ct, ignoreExitCode: true)
                .ConfigureAwait(false);
        return null;
    }

    private async Task<(bool Success, string? CommitSha, string? Error)> CommitAndPushAsync(
        string tmp, string branch, string commitMessage, CancellationToken ct)
    {
        var (exitCommit, _, errCommit) = await git.RunGitAsync(
            tmp, ["commit", "-m", commitMessage], ct, ignoreExitCode: true).ConfigureAwait(false);
        if (exitCommit != 0)
        {
            var (_, statusOut, _) = await git.RunGitAsync(
                tmp, ["status", "--porcelain"], ct, ignoreExitCode: true).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(statusOut)) return (false, null, $"commit failed: {errCommit}");
            var (_, headSha, _) = await git.RunGitAsync(
                tmp, ["rev-parse", "HEAD"], ct, ignoreExitCode: true).ConfigureAwait(false);
            return (true, headSha.Trim(), null);
        }
        var (_, sha, _) = await git.RunGitAsync(
            tmp, ["rev-parse", "HEAD"], ct, ignoreExitCode: true).ConfigureAwait(false);
        var (exitPush, _, errPush) = await git.RunGitAsync(
            tmp, ["push", "origin", $"HEAD:{branch}"], ct,
            timeout: BackendRuntimeDefaults.GitWriteTimeout).ConfigureAwait(false);
        return exitPush == 0
            ? (true, sha.Trim(), null)
            : (false, null, $"push failed: {errPush}");
    }

    private sealed record NormalizedChanges(List<string> Upserts, List<string> Deletions);

    public async Task<(bool Success, string? CommitSha, string? Error)> ApplyPatchAsync(
        string diskPath, string branch, string patch,
        string commitMessage, string authorName, string authorEmail, CancellationToken ct = default)
    {
        if (!IsValidRefName(branch))
            return (false, null, "invalid branch name");
        if (string.IsNullOrWhiteSpace(patch)
            || patch.Length > 1_000_000
            || !patch.Contains("diff --git ", StringComparison.Ordinal))
            return (false, null, "invalid or empty patch");

        var tmp = Path.Combine(Path.GetTempPath(), $"aetheus-ai-patch-{Guid.NewGuid():N}");
        try
        {
            var (cloneCode, _, cloneError) = await git.RunGitAsync(
                diskPath,
                ["clone", "--depth", "1", "--single-branch", "--no-tags", "--branch", branch, diskPath, tmp],
                ct,
                timeout: BackendRuntimeDefaults.GitWriteTimeout,
                ignoreExitCode: true).ConfigureAwait(false);
            if (cloneCode != 0)
                return (false, null, $"clone failed: {cloneError}");

            await git.RunGitAsync(tmp, ["config", "user.name", authorName], ct).ConfigureAwait(false);
            await git.RunGitAsync(tmp, ["config", "user.email", authorEmail], ct).ConfigureAwait(false);
            var patchPath = Path.Combine(tmp, ".aetheus-ai-proposed.patch");
            await File.WriteAllTextAsync(patchPath, patch, ct).ConfigureAwait(false);
            var (applyCode, _, applyError) = await git.RunGitAsync(
                tmp, ["apply", "--index", "--whitespace=nowarn", "--", patchPath],
                ct, timeout: BackendRuntimeDefaults.GitWriteTimeout, ignoreExitCode: true).ConfigureAwait(false);
            File.Delete(patchPath);
            if (applyCode != 0)
                return (false, null, $"patch apply failed: {applyError}");

            var (commitCode, _, commitError) = await git.RunGitAsync(
                tmp, ["commit", "-m", commitMessage], ct, ignoreExitCode: true).ConfigureAwait(false);
            if (commitCode != 0)
                return (false, null, $"commit failed: {commitError}");
            var (_, sha, _) = await git.RunGitAsync(
                tmp, ["rev-parse", "HEAD"], ct, ignoreExitCode: true).ConfigureAwait(false);
            var (pushCode, _, pushError) = await git.RunGitAsync(
                tmp, ["push", "origin", $"HEAD:{branch}"], ct,
                timeout: BackendRuntimeDefaults.GitWriteTimeout, ignoreExitCode: true).ConfigureAwait(false);
            return pushCode == 0
                ? (true, sha.Trim(), null)
                : (false, null, $"push failed: {pushError}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, null, ex.Message);
        }
        finally
        {
            if (Directory.Exists(tmp))
            {
                try
                {
                    GitProcessRunner.ClearReadOnlyAttributes(tmp);
                    Directory.Delete(tmp, true);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Best-effort AI patch temp cleanup failed: {Dir}", tmp);
                }
            }
        }
    }

    private static bool TryNormalizeRelativePath(string relativePath, out string safeRelativePath)
    {
        safeRelativePath = relativePath.Replace('\\', '/');
        return !string.IsNullOrWhiteSpace(safeRelativePath)
            && !Path.IsPathRooted(safeRelativePath)
            && safeRelativePath.Split('/').All(seg => seg is not (".." or ""));
    }

    public async Task<string?> DetectDefaultBranchAsync(string diskPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(diskPath)) return null;

        var (lsCode, lsOut, _) = await git.RunGitAsync(diskPath,
            ["for-each-ref", "--format=%(refname:short)", "refs/heads/"], ct, ignoreExitCode: true).ConfigureAwait(false);
        if (lsCode != 0 || string.IsNullOrWhiteSpace(lsOut)) return null;

        var branches = lsOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (branches.Count == 0) return null;

        var (headCode, headOut, _) = await git.RunGitAsync(diskPath,
            ["symbolic-ref", "--quiet", "--short", "HEAD"], ct, ignoreExitCode: true).ConfigureAwait(false);
        if (headCode == 0)
        {
            var headBranch = headOut.Trim();
            if (!string.IsNullOrEmpty(headBranch) &&
                branches.Contains(headBranch, StringComparer.Ordinal))
                return headBranch;
        }

        foreach (var preferred in new[] { "main", "master", "develop", "trunk" })
        {
            if (branches.Contains(preferred, StringComparer.Ordinal))
                return preferred;
        }
        return branches[0];
    }

    public async Task SetHeadAsync(string diskPath, string branch, CancellationToken ct = default)
    {
        // Defense-in-depth, consistent with CommitFilesAsync: validate the ref name before it is
        // interpolated into refs/heads/<branch> and passed to git symbolic-ref. Callers feed this from
        // branch detection today, but the public contract must not assume that.
        if (!IsValidRefName(branch))
            throw new ArgumentException($"Invalid branch name '{branch}'.", nameof(branch));
        await git.RunGitAsync(diskPath, ["symbolic-ref", "HEAD", $"refs/heads/{branch}"], ct).ConfigureAwait(false);
    }

    /// <summary>F-038: conservative refname validation (subset of git-check-ref-format).</summary>
    private static bool IsValidRefName(string branch) =>
        !string.IsNullOrWhiteSpace(branch) && branch.Length <= 255
        && !HasForbiddenRefEdge(branch) && !HasForbiddenRefSequence(branch)
        && branch.AsSpan().IndexOfAny(ForbiddenRefCharacters) < 0 && !branch.Any(char.IsControl);

    private static readonly System.Buffers.SearchValues<char> ForbiddenRefCharacters =
        System.Buffers.SearchValues.Create(" ~^:?*[\\");

    private static bool HasForbiddenRefEdge(string branch) =>
        branch.StartsWith('-') || branch.StartsWith('/') || branch.EndsWith('/')
        || branch.EndsWith(".lock", StringComparison.Ordinal) || branch.EndsWith('.');

    private static bool HasForbiddenRefSequence(string branch) =>
        branch.Contains("..", StringComparison.Ordinal) || branch.Contains("//", StringComparison.Ordinal);
}
