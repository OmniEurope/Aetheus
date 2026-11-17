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

    /// <summary>Commits multiple files through one clone, commit and push round trip.</summary>
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

    // BACK-010: a symbolic link committed in the repository (.pipeline -> /etc/x) would be checked out
    // as a real link on Linux, and the write below would follow it out of the temporary clone. The
    // clone checks links out as plain files holding the link text instead; ApplyChangesAsync still
    // refuses any link it finds on the way.
    private const string NoSymlinksConfig = "core.symlinks=false";

    internal async Task<string?> CloneTargetBranchAsync(
        string diskPath, string branch, string tmp, CancellationToken ct)
    {
        var (exitClone, _, _) = await git.RunGitAsync(diskPath,
            ["clone", "-c", NoSymlinksConfig, "--depth", "1", "--single-branch", "--no-tags", "--branch", branch, diskPath, tmp],
            ct, timeout: BackendRuntimeDefaults.GitWriteTimeout, ignoreExitCode: true).ConfigureAwait(false);
        if (exitClone == 0) return null;
        var (exitFull, _, errFull) = await git.RunGitAsync(
            diskPath, ["clone", "-c", NoSymlinksConfig, diskPath, tmp], ct, timeout: BackendRuntimeDefaults.GitWriteTimeout)
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
        // With core.symlinks=false a committed link is a plain file in the clone, and git keeps its
        // link mode: writing over it would commit a link to the new text. Refuse a path that is, or
        // goes through, a link the repository holds.
        if (await FindCommittedLinkAsync(tmp, paths.Upserts, ct).ConfigureAwait(false) is { } committedLink)
            return $"invalid relative path: {committedLink} is a symbolic link in the repository";
        for (var i = 0; i < upserts.Count; i++)
        {
            var root = Path.GetFullPath(tmp);
            var target = Path.GetFullPath(Path.Combine(tmp, paths.Upserts[i]));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return $"invalid relative path: {upserts[i].RelativePath}";
            if (HasLinkOnTheWay(root, target))
                return $"invalid relative path: {upserts[i].RelativePath} goes through a symbolic link";
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

    /// <summary>
    /// BACK-010: the first path among <paramref name="upserts"/> and their parent directories that the
    /// checked-out commit records as a symbolic link (mode 120000), or null. A clone without a commit
    /// yet has nothing to follow; the file-system check below still runs either way.
    /// </summary>
    private async Task<string?> FindCommittedLinkAsync(string tmp, IReadOnlyList<string> upserts, CancellationToken ct)
    {
        var prefixes = upserts.SelectMany(PathAndParents).Distinct(StringComparer.Ordinal).ToList();
        if (prefixes.Count == 0) return null;
        var (exit, output, _) = await git.RunGitAsync(
            tmp, ["ls-tree", "HEAD", "--", .. prefixes], ct, ignoreExitCode: true).ConfigureAwait(false);
        if (exit != 0) return null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("120000 ", StringComparison.Ordinal)) continue;
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            return tab >= 0 ? line[(tab + 1)..].Trim() : line.Trim();
        }
        return null;
    }

    // "a/b/c" -> "a", "a/b", "a/b/c".
    private static IEnumerable<string> PathAndParents(string path)
    {
        var segments = path.Split('/');
        for (var i = 1; i <= segments.Length; i++)
            yield return string.Join('/', segments, 0, i);
    }

    /// <summary>
    /// BACK-010: true when a component of <paramref name="target"/> below <paramref name="root"/> that
    /// already exists, the target itself included, is a symbolic link or another reparse point
    /// (a Windows junction). The lexical check above cannot see one; writing through it would leave
    /// the clone. A dangling link counts: its LinkTarget is read without following it.
    /// </summary>
    internal static bool HasLinkOnTheWay(string root, string target)
    {
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, target).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            var info = new FileInfo(current);
            if (info.LinkTarget is not null) return true;
            if (!info.Exists && !Directory.Exists(current)) return false;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
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

        // Fast path, no process: a bare repository keeps HEAD as a plain file ("ref: refs/heads/<name>").
        // When it points to a branch that really exists (loose ref file or packed-refs entry), that is
        // exactly what the git path below returns. Anything else (detached HEAD, dangling HEAD, other
        // layout) falls through to git, so results are identical. Stateless: nothing to invalidate.
        var fileHeadBranch = await TryReadHeadBranchAsync(diskPath, ct).ConfigureAwait(false);
        if (fileHeadBranch is not null) return fileHeadBranch;

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

    private const string HeadRefPrefix = "ref: refs/heads/";

    private static async Task<string?> TryReadHeadBranchAsync(string diskPath, CancellationToken ct)
    {
        var headFile = Path.Combine(diskPath, "HEAD");
        if (!File.Exists(headFile)) return null;
        var head = (await File.ReadAllTextAsync(headFile, ct).ConfigureAwait(false)).Trim();
        if (!head.StartsWith(HeadRefPrefix, StringComparison.Ordinal)) return null;
        var branch = head[HeadRefPrefix.Length..];
        if (!IsValidRefName(branch)) return null;

        if (File.Exists(Path.Combine(diskPath, "refs", "heads", branch))) return branch;

        var packedRefs = Path.Combine(diskPath, "packed-refs");
        if (!File.Exists(packedRefs)) return null;
        var suffix = " refs/heads/" + branch;
        var lines = await File.ReadAllLinesAsync(packedRefs, ct).ConfigureAwait(false);
        return lines.Any(line => line.EndsWith(suffix, StringComparison.Ordinal)) ? branch : null;
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
