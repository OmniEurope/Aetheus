// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Git;

/// <summary>
/// Everything that asks git what changed between two revisions: the numeric summary, the unified
/// patch, and the bare list of paths.
///
/// Extracted from <see cref="GitLightCliService"/> when it crossed the file-size budget
/// (FileSizeAuditTests). The three answers share one question and one bound, and separating them
/// from the rest of the CLI surface (refs, trees, blames, worktrees) makes that visible.
///
/// Every output is bounded. A repository can produce an arbitrarily large diff, and a caller that
/// reads it into memory without a ceiling turns one push into a memory incident.
/// </summary>
internal sealed class GitDiffReader(GitProcessRunner git)
{
    internal const int MaximumCommitPatchChars = 512 * 1024;

    public async Task<PullRequestDiffDto> GetDiffAsync(
        string diskPath, string fromRef, string toRef, CancellationToken ct = default)
    {
        GitArgumentGuard.EnsureRefArgsSafe(fromRef, toRef);
        var (exitCode, output, _) = await git
            .RunGitAsync(diskPath, ["diff", "--numstat", fromRef, toRef], ct).ConfigureAwait(false);
        if (exitCode != 0) return new PullRequestDiffDto();

        var fileDiffs = new List<FileDiffDto>();
        int totalAdd = 0, totalDel = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            int.TryParse(parts[0], out var additions);
            int.TryParse(parts[1], out var deletions);
            totalAdd += additions;
            totalDel += deletions;
            fileDiffs.Add(new FileDiffDto
            {
                Path = parts[2],
                Status = "modified",
                Additions = additions,
                Deletions = deletions
            });
        }

        return new PullRequestDiffDto
        {
            FileDiffs = fileDiffs,
            Stats = new DiffStatsDto
            {
                Additions = totalAdd,
                Deletions = totalDel,
                FilesChanged = fileDiffs.Count
            }
        };
    }

    public async Task<GitPatchResult> GetCommitPatchAsync(
        string diskPath, string fromRef, string toRef, CancellationToken ct = default)
    {
        GitArgumentGuard.EnsureRefArgsSafe(fromRef, toRef);
        var (exitCode, output, _, truncated) = await git.RunGitBoundedAsync(
            diskPath, ["diff", "--patch", fromRef, toRef], MaximumCommitPatchChars, ct).ConfigureAwait(false);
        return exitCode == 0 ? new GitPatchResult(output, truncated) : new GitPatchResult(string.Empty, false);
    }

    /// <summary>
    /// The paths a range touched, names only. Reading the patch to answer a question about strings
    /// would cost megabytes.
    ///
    /// Returns null, not an empty list, when git could not answer: an empty list means "this range
    /// changed nothing", which a caller may act on, and "I do not know" must never be taken for it.
    /// A truncated list is also null, because a filter that decides on a partial list of changed
    /// files decides about files it never saw.
    /// </summary>
    public async Task<IReadOnlyList<string>?> GetChangedPathsAsync(
        string diskPath, string fromRef, string toRef, CancellationToken ct = default)
    {
        GitArgumentGuard.EnsureRefArgsSafe(fromRef, toRef);
        var (exitCode, output, _, truncated) = await git.RunGitBoundedAsync(
            diskPath, ["diff", "--name-only", "--no-renames", fromRef, toRef],
            MaximumCommitPatchChars, ct).ConfigureAwait(false);
        if (exitCode != 0 || truncated) return null;
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>The most a tree listing may weigh: past it the answer is "unknown", never a partial list.</summary>
    internal const int MaximumTreeListingChars = 16 * 1024 * 1024;

    /// <summary>
    /// Every file of a revision with its mode and blob id (<c>path</c> to <c>"mode sha"</c>), or null
    /// when git could not list it in full. Two revisions of two repositories hold the same files exactly
    /// when the two maps are equal: a blob id is the hash of the content.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>?> GetTreeBlobsAsync(
        string diskPath, string revision, CancellationToken ct = default)
    {
        GitArgumentGuard.EnsureRefArgsSafe(revision);
        var (exitCode, output, _, truncated) = await git.RunGitBoundedAsync(
            diskPath, ["ls-tree", "-r", "-z", "--full-tree", revision],
            MaximumTreeListingChars, ct).ConfigureAwait(false);
        if (exitCode != 0 || truncated) return null;

        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<mode> <type> <sha>\t<path>": the path is everything after the first tab.
            var tab = entry.IndexOf('\t', StringComparison.Ordinal);
            var fields = tab < 0 ? [] : entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3) return null;
            blobs[entry[(tab + 1)..]] = fields[0] + " " + fields[2];
        }
        return blobs;
    }

    public async Task<GitPatchResult> GetRootCommitPatchAsync(
        string diskPath, string commitRef, CancellationToken ct = default)
    {
        GitArgumentGuard.EnsureRefArgsSafe(commitRef);
        var (exitCode, output, _, truncated) = await git.RunGitBoundedAsync(
            diskPath, ["show", "--format=", "--no-ext-diff", "--patch", commitRef],
            MaximumCommitPatchChars, ct).ConfigureAwait(false);
        return exitCode == 0 ? new GitPatchResult(output, truncated) : new GitPatchResult(string.Empty, false);
    }
}
