// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Aetheus.Back.Configuration;

namespace Aetheus.Back.Components.Git;

public class GitLightCliService(
    GitProcessRunner git, GitLightCliWriter writer,
    ILogger<GitLightCliService> logger, TimeProvider timeProvider) : IGitLightCliService
{
    internal const int MaximumCommitPatchChars = GitDiffReader.MaximumCommitPatchChars;

    // "What changed between two revisions" is its own question with its own bound; it lives in its
    // own reader, and this facade forwards to it.
    private readonly GitDiffReader _diffs = new(git);

    // Walking the history (the commits grid, its total, its filters) is its own reader too.
    private readonly GitCommitLogReader _log = new(git, logger, timeProvider);
    // M-git-2: refs/paths flow from the query string into git argv. Argv is shell-safe (ArgumentList),
    // but a value starting with '-' can still be parsed by git as an OPTION (argument injection, e.g.
    // "--output=..."). git-check-ref-format forbids a leading '-' anyway, so rejecting it here is a
    // free defense-in-depth guard on every user-supplied ref/path before it reaches a git command.
    internal static void EnsureRefArgsSafe(params string?[] values)
    {
        foreach (var v in values)
        {
            if (v is not null && v.StartsWith('-'))
                throw new ArgumentException($"Ref or path may not start with '-': '{v}'.", nameof(values));
        }
    }

    public async Task InitBareRepoAsync(string diskPath, string defaultBranch = "main", CancellationToken ct = default)
    {
        Directory.CreateDirectory(diskPath);
        await RunGitAsync(diskPath, ["init", "--bare", "--initial-branch", defaultBranch], ct).ConfigureAwait(false);
        await InstallPreReceiveHookAsync(diskPath, ct).ConfigureAwait(false);
    }

    public Task DeleteRepoAsync(string diskPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(diskPath))
            return Task.CompletedTask;

        GitProcessRunner.ClearReadOnlyAttributes(diskPath);
        Directory.Delete(diskPath, recursive: true);
        return Task.CompletedTask;
    }
    // --- Write operations (delegated to GitLightCliWriter) ---
    public Task<(bool Success, string? CommitSha, string? Error)> CommitFileAsync(
        string diskPath, string branch, string relativePath, string content,
        string commitMessage, string authorName, string authorEmail, CancellationToken ct = default)
        => writer.CommitFileAsync(diskPath, branch, relativePath, content, commitMessage, authorName, authorEmail, ct);

    public Task<(bool Success, string? CommitSha, string? Error)> CommitFilesAsync(
        string diskPath, string branch, IReadOnlyList<(string RelativePath, string Content)> files,
        string commitMessage, string authorName, string authorEmail, CancellationToken ct = default)
        => writer.CommitFilesAsync(diskPath, branch, files, commitMessage, authorName, authorEmail, ct);

    public Task<(bool Success, string? CommitSha, string? Error)> CommitFileChangesAsync(string diskPath, string branch, IReadOnlyList<(string RelativePath, string Content)> upserts, IReadOnlyList<string> deletions,
        string commitMessage, string authorName, string authorEmail, CancellationToken ct = default) => writer.CommitFileChangesAsync(diskPath, branch, upserts, deletions, commitMessage, authorName, authorEmail, ct);

    public Task<string?> DetectDefaultBranchAsync(string diskPath, CancellationToken ct = default)
        => writer.DetectDefaultBranchAsync(diskPath, ct);

    public Task SetHeadAsync(string diskPath, string branch, CancellationToken ct = default)
        => writer.SetHeadAsync(diskPath, branch, ct);

    // --- History walks (delegated to GitCommitLogReader) ---
    public Task<List<GitLightCommitDto>> GetCommitsAsync(string diskPath, string? refName, int skip, int take, string? search = null, string? afterSha = null, CancellationToken ct = default, GitCommitLogFilter? filter = null)
        => _log.GetCommitsAsync(diskPath, refName, skip, take, search, afterSha, filter, ct);

    public Task<Dictionary<string, string>> GetCommitMessagesAsync(
        string diskPath, IReadOnlyCollection<string> shas, CancellationToken ct = default) =>
        GitCommitMessageReader.ReadAsync(git, diskPath, shas, ct);

    public Task<int> GetCommitCountAsync(string diskPath, string? refName, string? search = null, CancellationToken ct = default, GitCommitLogFilter? filter = null)
        => _log.GetCommitCountAsync(diskPath, refName, search, filter, ct);

    public Task<List<string>> GetCommitAuthorsAsync(string diskPath, CancellationToken ct = default)
        => _log.GetCommitAuthorsAsync(diskPath, ct);

    public async Task<List<GitLightBranchDto>> GetBranchesAsync(string diskPath, string defaultBranch, CancellationToken ct = default)
    {
        var (exitCode, output, _) = await RunGitAsync(diskPath,
            ["for-each-ref", "--format=%(refname:short)%09%(objectname:short)%09%(creatordate:iso-strict)%09%(subject)", "refs/heads/"], ct).ConfigureAwait(false);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output)) return [];

        var branches = new List<GitLightBranchDto>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 4) continue;
            branches.Add(new GitLightBranchDto
            {
                Name = parts[0],
                IsDefault = parts[0].Equals(defaultBranch, StringComparison.Ordinal),
                LastCommitSha = parts[1],
                LastCommitDate = DateTime.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null,
                LastCommitMessage = parts[3]
            });
        }
        return branches;
    }

    public async Task<List<GitLightTagDto>> GetTagsAsync(string diskPath, CancellationToken ct = default)
    {
        var (exitCode, output, _) = await RunGitAsync(diskPath,
            ["for-each-ref", "--format=%(refname:short)%09%(objectname:short)%09%(contents:subject)%09%(taggername)%09%(taggerdate:iso-strict)", "refs/tags/"], ct).ConfigureAwait(false);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output)) return [];

        var tags = new List<GitLightTagDto>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            tags.Add(new GitLightTagDto
            {
                Name = parts[0],
                Sha = parts[1],
                Message = parts.Length > 2 ? NullIfEmpty(parts[2]) : null,
                TaggerName = parts.Length > 3 ? NullIfEmpty(parts[3]) : null,
                TaggerDate = parts.Length > 4 && DateTime.TryParse(parts[4], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null
            });
        }
        return tags;
    }

    public async Task CreateBranchAsync(string diskPath, string branchName, string? startRef, CancellationToken ct = default)
    {
        EnsureRefArgsSafe(branchName, startRef);
        var args = new List<string> { "branch", branchName };
        if (!string.IsNullOrEmpty(startRef)) args.Add(startRef);
        var (exitCode, _, error) = await RunGitAsync(diskPath, args, ct).ConfigureAwait(false);
        if (exitCode != 0) throw new InvalidOperationException($"Failed to create branch: {error}");
    }

    public async Task DeleteBranchAsync(string diskPath, string branchName, CancellationToken ct = default)
    {
        var (exitCode, _, error) = await RunGitAsync(diskPath, ["branch", "-D", branchName], ct).ConfigureAwait(false);
        if (exitCode != 0) throw new InvalidOperationException($"Failed to delete branch: {error}");
    }

    public async Task CreateTagAsync(string diskPath, string tagName, string? refName, string? message, CancellationToken ct = default)
    {
        EnsureRefArgsSafe(tagName, refName);
        var args = new List<string> { "tag" };
        if (!string.IsNullOrEmpty(message))
        {
            args.Add("-a");
            args.Add(tagName);
            args.Add("-m");
            args.Add(message);
        }
        else
        {
            args.Add(tagName);
        }
        if (!string.IsNullOrEmpty(refName)) args.Add(refName);
        var (exitCode, _, error) = await RunGitAsync(diskPath, args, ct).ConfigureAwait(false);
        if (exitCode != 0) throw new InvalidOperationException($"Failed to create tag: {error}");
    }

    public async Task DeleteTagAsync(string diskPath, string tagName, CancellationToken ct = default)
    {
        var (exitCode, _, error) = await RunGitAsync(diskPath, ["tag", "-d", tagName], ct).ConfigureAwait(false);
        if (exitCode != 0) throw new InvalidOperationException($"Failed to delete tag: {error}");
    }

    public async Task<List<GitLightTreeEntryDto>> GetTreeAsync(string diskPath, string refName, string? path, CancellationToken ct = default)
    {
        EnsureRefArgsSafe(refName, path);
        var treePath = string.IsNullOrEmpty(path) ? $"{refName}" : $"{refName}:{path}";
        var (exitCode, output, _) = await RunGitAsync(diskPath, ["ls-tree", "-l", treePath], ct).ConfigureAwait(false);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output)) return [];

        var entries = new List<GitLightTreeEntryDto>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Format: <mode> <type> <hash> <size>\t<name>
            var tabIdx = line.IndexOf('\t');
            if (tabIdx < 0) continue;
            var meta = line[..tabIdx].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var name = line[(tabIdx + 1)..];
            if (meta.Length < 4) continue;

            var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
            entries.Add(new GitLightTreeEntryDto
            {
                Name = name,
                Path = fullPath,
                Type = meta[1] == "tree" ? GitTreeEntryType.Tree : GitTreeEntryType.Blob,
                Size = meta[1] == "blob" && long.TryParse(meta[3], out var s) ? s : null,
                Mode = meta[0]
            });
        }
        return [.. entries.OrderBy(e => e.Type == GitTreeEntryType.Blob).ThenBy(e => e.Name)];
    }

    public async Task<GitLightBlobDto?> GetBlobAsync(string diskPath, string refName, string path, CancellationToken ct = default)
    {
        EnsureRefArgsSafe(refName, path);
        // Get size first
        var (exitStat, statOut, _) = await RunGitAsync(diskPath, ["cat-file", "-s", $"{refName}:{path}"], ct).ConfigureAwait(false);
        if (exitStat != 0) return null;
        long.TryParse(statOut.Trim(), out var size);

        // Oversized blobs are treated as binary without fetching content (guards memory).
        if (size > 1_048_576) // 1MB limit
        {
            return new GitLightBlobDto { Path = path, Size = size, IsBinary = true };
        }

        var (exitCode, content, _) = await RunGitAsync(diskPath, ["show", $"{refName}:{path}"], ct).ConfigureAwait(false);
        if (exitCode != 0) return null;

        // Binary detection via NUL-byte scan of the content (cross-platform). The previous
        // `git diff --no-index /dev/null` trick silently failed on Windows (no /dev/null) and made the
        // result random; scanning the already-fetched content is portable and is git's own heuristic.
        if (content.Contains('\0', StringComparison.Ordinal))
        {
            return new GitLightBlobDto { Path = path, Size = size, IsBinary = true };
        }

        return new GitLightBlobDto
        {
            Path = path,
            Content = content,
            Size = size,
            IsBinary = false
        };
    }

    public Task<Stream?> GetBlobStreamAsync(string diskPath, string refName, string path, CancellationToken ct = default)
    {
        EnsureRefArgsSafe(refName, path);
        var process = StartStreamingGit(diskPath, ["show", $"{refName}:{path}"]);
        if (process is null) return Task.FromResult<Stream?>(null);

        // Hardening (#50): wrap stdout in a Stream that owns the Process so the caller's
        // `using` ensures the git child is killed/disposed even on early stream abandonment.
        return Task.FromResult<Stream?>(new ProcessOwnedStream(process, process.StandardOutput.BaseStream));
    }

    public async Task<Stream?> GetArchiveStreamAsync(string diskPath, string refName, string prefix, CancellationToken ct = default)
    {
        // The ref reaches git as argv like every other read route, so it gets the same guard.
        EnsureRefArgsSafe(refName);
        // Resolve the tree first: once the zip streams, the 200 is on the wire and a git failure could
        // only truncate it. An unknown ref is a 404 instead.
        var (exitCode, _, _) = await RunGitAsync(
            diskPath, ["rev-parse", "--verify", "--quiet", $"{refName}^{{tree}}"], ct, ignoreExitCode: true).ConfigureAwait(false);
        if (exitCode != 0) return null;

        var process = StartStreamingGit(diskPath, ["archive", "--format=zip", $"--prefix={prefix}/", refName]);
        if (process is null) return null;
        // Drain stderr so a chatty git can never block on a full pipe while the response streams.
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
        return new ProcessOwnedStream(process, process.StandardOutput.BaseStream);
    }

    private static Process? StartStreamingGit(string diskPath, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = diskPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        GitProcessStartInfoFactory.NeutralizeInheritedGitEnvironment(psi);
        return Process.Start(psi);
    }

    public async Task<(bool Success, string? MergeCommitSha, string? Error)> MergeBranchesAsync(
        string diskPath, string source, string target, string authorName, string authorEmail, CancellationToken ct = default)
    {
        // M-git-2: source/target reach `git worktree add <target>` and `git merge <source>` as argv - a
        // leading '-' would be parsed as an option. Guard them like the other ref-taking entry points.
        EnsureRefArgsSafe(source, target);
        // Use a temporary worktree for the merge
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"aetheus-merge-{Guid.NewGuid():N}");
        try
        {
            // Create worktree on target branch
            var (exitWt, _, errWt) = await RunGitAsync(diskPath, ["worktree", "add", worktreeDir, target], ct).ConfigureAwait(false);
            if (exitWt != 0) return (false, null, $"Failed to create worktree: {errWt}");

            // Configure author
            await RunGitAsync(worktreeDir, ["config", "user.name", authorName], ct).ConfigureAwait(false);
            await RunGitAsync(worktreeDir, ["config", "user.email", authorEmail], ct).ConfigureAwait(false);

            // Attempt merge
            var (exitMerge, _, errMerge) = await RunGitAsync(worktreeDir, ["merge", $"origin/{source}", "--no-edit"], ct, timeout: BackendRuntimeDefaults.GitWriteTimeout).ConfigureAwait(false);

            // For bare repos, source branch ref is under refs/heads/
            if (exitMerge != 0)
            {
                // Try direct branch ref
                var (exitMerge2, _, errMerge2) = await RunGitAsync(worktreeDir, ["merge", source, "--no-edit"], ct, timeout: BackendRuntimeDefaults.GitWriteTimeout).ConfigureAwait(false);
                if (exitMerge2 != 0)
                {
                    // Abort merge
                    await RunGitAsync(worktreeDir, ["merge", "--abort"], ct, ignoreExitCode: true).ConfigureAwait(false);
                    return (false, null, $"Merge conflict: {errMerge2}");
                }
            }

            // Get merge commit sha
            var (_, sha, _) = await RunGitAsync(worktreeDir, ["rev-parse", "HEAD"], ct).ConfigureAwait(false);
            return (true, sha.Trim(), null);
        }
        finally
        {
            // Cleanup worktree
            await RunGitAsync(diskPath, ["worktree", "remove", worktreeDir, "--force"], ct, ignoreExitCode: true).ConfigureAwait(false);
            if (Directory.Exists(worktreeDir))
                try { Directory.Delete(worktreeDir, true); }
                catch (Exception ex) { logger.LogDebug(ex, "Best-effort worktree cleanup failed: {Dir}", worktreeDir); }
            // Prune any dangling worktree registrations left in .git/worktrees by a crash between
            // `worktree add` and `worktree remove` (this run's or a prior one). Best-effort.
            await RunGitAsync(diskPath, ["worktree", "prune"], ct, ignoreExitCode: true).ConfigureAwait(false);
        }
    }


    public Task<PullRequestDiffDto> GetDiffAsync(
        string diskPath, string fromRef, string toRef, CancellationToken ct = default)
        => _diffs.GetDiffAsync(diskPath, fromRef, toRef, ct);

    public Task<GitPatchResult> GetCommitPatchAsync(
        string diskPath, string fromRef, string toRef, CancellationToken ct = default)
        => _diffs.GetCommitPatchAsync(diskPath, fromRef, toRef, ct);

    public Task<IReadOnlyList<string>?> GetChangedPathsAsync(
        string diskPath, string fromRef, string toRef, CancellationToken ct = default)
        => _diffs.GetChangedPathsAsync(diskPath, fromRef, toRef, ct);

    public Task<IReadOnlyDictionary<string, string>?> GetTreeBlobsAsync(
        string diskPath, string revision, CancellationToken ct = default)
        => _diffs.GetTreeBlobsAsync(diskPath, revision, ct);

    public Task<GitPatchResult> GetRootCommitPatchAsync(
        string diskPath, string commitRef, CancellationToken ct = default)
        => _diffs.GetRootCommitPatchAsync(diskPath, commitRef, ct);

    public async Task RunGcAsync(string diskPath, CancellationToken ct = default)
    {
        await RunGitAsync(diskPath, ["gc", "--auto"], ct, timeout: TimeSpan.FromMinutes(5), ignoreExitCode: true).ConfigureAwait(false);
    }

    public async Task<string> GetCommitGraphAsync(string diskPath, int maxCount, CancellationToken ct = default)
    {
        var (exitCode, output, _) = await RunGitAsync(diskPath,
            ["log", "--graph", "--oneline", "--all", "--decorate", $"--max-count={maxCount}"], ct).ConfigureAwait(false);
        return exitCode == 0 ? output : string.Empty;
    }

    // The pre-receive branch-protection hook. Hoisted to a const field so the method stays pure
    // orchestration (the shell's own case/if branching is not method complexity). The protection config
    // it reads (aetheus-protection, pipe-delimited) is written by WriteProtectionConfigAsync, which
    // rejects '|'/CR/LF in patterns (IsSafeProtectionPattern) so the IFS='|' parse can't be spoofed.
    private const string PreReceiveHookScript = """
        #!/bin/sh
        ZERO="0000000000000000000000000000000000000000"
        CONFIG="$(git rev-parse --git-dir)/aetheus-protection"
        [ ! -f "$CONFIG" ] && exit 0
        while read -r oldrev newrev refname; do
          case "$refname" in refs/heads/*) ;; *) continue ;; esac
          branch="${refname#refs/heads/}"
          while IFS='|' read -r pat del force; do
            m=0
            case "$pat" in
              \*) m=1 ;;
              *\*) p="${pat%\*}"; case "$branch" in "$p"*) m=1 ;; esac ;;
              *) [ "$branch" = "$pat" ] && m=1 ;;
            esac
            [ "$m" -eq 0 ] && continue
            if [ "$newrev" = "$ZERO" ] && [ "$del" = "true" ]; then
              echo "*** Branch '$branch' is protected: deletion not allowed." >&2; exit 1
            fi
            if [ "$oldrev" != "$ZERO" ] && [ "$newrev" != "$ZERO" ] && [ "$force" = "true" ]; then
              git merge-base --is-ancestor "$oldrev" "$newrev" 2>/dev/null || {
                echo "*** Branch '$branch' is protected: force push not allowed." >&2; exit 1
              }
            fi
          done < "$CONFIG"
        done
        exit 0
        """;

    public Task InstallPreReceiveHookAsync(string diskPath, CancellationToken ct = default)
    {
        var hooksDir = Path.Combine(diskPath, "hooks");
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, "pre-receive");

        File.WriteAllText(hookPath, PreReceiveHookScript.ReplaceLineEndings("\n"));

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                using var chmod = Process.Start(new ProcessStartInfo
                {
                    FileName = "chmod",
                    ArgumentList = { "+x", hookPath },
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                chmod?.WaitForExit(5000);
            }
            catch (Exception ex) { logger.LogDebug(ex, "Best-effort chmod failed"); }
        }

        return Task.CompletedTask;
    }

    public Task WriteProtectionConfigAsync(string diskPath, IReadOnlyList<(string Pattern, bool PreventDeletion, bool PreventForcePush)> rules, CancellationToken ct = default)
    {
        var configPath = Path.Combine(diskPath, "aetheus-protection");
        if (rules.Count == 0)
        {
            if (File.Exists(configPath)) File.Delete(configPath);
            return Task.CompletedTask;
        }

        var sb = new StringBuilder();
        foreach (var (pattern, del, force) in rules)
        {
            // The pre-receive hook parses this file line-by-line with IFS='|' (pattern|del|force). A
            // pattern carrying a '|' or a newline would inject extra fields/lines and silently rewrite
            // the protection semantics, so reject it on write (git ref names cannot contain these anyway).
            if (!IsSafeProtectionPattern(pattern))
                throw new BadRequestException($"Invalid branch-protection pattern '{pattern}'.");
            // '\n', never AppendLine: on Windows its CRLF left "true\r" in the last field, so the
            // hook's [ "$force" = "true" ] never matched and force pushes went through.
            sb.Append($"{pattern}|{del.ToString().ToLowerInvariant()}|{force.ToString().ToLowerInvariant()}").Append('\n');
        }

        File.WriteAllText(configPath, sb.ToString());
        return Task.CompletedTask;
    }

    // A branch-protection pattern is a git ref glob (e.g. "main", "release/*", "*"). It can never legally
    // contain the '|' field separator, a CR/LF line separator, or any control character; rejecting those
    // closes the IFS='|' injection vector in the pre-receive hook config.
    private static bool IsSafeProtectionPattern(string pattern) =>
        !string.IsNullOrEmpty(pattern)
        && pattern.Length <= 200
        && !pattern.Any(c => c == '|' || char.IsControl(c));

    public async Task<List<GitLightBlameLine>> GetBlameAsync(string diskPath, string refName, string path, CancellationToken ct = default)
    {
        EnsureRefArgsSafe(refName, path);
        var (exitCode, output, _) = await RunGitAsync(diskPath, ["blame", "--porcelain", refName, "--", path], ct).ConfigureAwait(false);
        if (exitCode != 0) return [];

        return GitBlameParser.Parse(output);
    }

    private Task<(int ExitCode, string Output, string Error)> RunGitAsync(
        string workDir, IReadOnlyList<string> args, CancellationToken ct,
        TimeSpan? timeout = null, bool ignoreExitCode = false)
        => git.RunGitAsync(workDir, args, ct, timeout, ignoreExitCode);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
