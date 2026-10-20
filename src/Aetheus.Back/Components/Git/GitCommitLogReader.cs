// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;

namespace Aetheus.Back.Components.Git;

/// <summary>Reads commit pages, counts and filter authors through git log and git rev-list. Page
/// and count share argument construction so totals match the listed scope.</summary>
internal sealed class GitCommitLogReader(GitProcessRunner git, ILogger logger, TimeProvider timeProvider)
{
    private static readonly System.Diagnostics.Metrics.Meter s_meter = new("Aetheus.Git");
    private static readonly System.Diagnostics.Metrics.Counter<long> s_deepSkipCounter = s_meter.CreateCounter<long>(
        "git_deep_skip_total", description: "Count of git --skip calls past the deep threshold");

    // Above this many positions, `--skip=N` starts to noticeably wait on git's
    // O(N) revision walk. Today the repos we host are well under it, but we log
    // a Warning past the threshold so the trigger to plumb a cursor through the
    // grid is loud rather than silent.
    private const int DeepSkipWarningThreshold = 1000;

    // %D supplies ref decorations, %cI supplies the committer date used by date filters, and %S
    // identifies the traversal source ref when --source is enabled.
    private const string LogFormat = "--format=%H%n%h%n%s%n%an%n%ae%n%aI%n%P%n%D%n%cI%n%S";

    public async Task<List<GitLightCommitDto>> GetCommitsAsync(
        string diskPath, string? refName, int skip, int take, string? search, string? afterSha,
        GitCommitLogFilter? filter, CancellationToken ct)
    {
        GitLightCliService.EnsureRefArgsSafe(refName);
        var args = BuildCommitLogArgs(refName, skip, take, search, afterSha, filter);

        var (exitCode, output, _) = await git.RunGitAsync(diskPath, args, ct).ConfigureAwait(false);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output)) return [];

        var commits = new List<GitLightCommitDto>();
        foreach (var entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = entry.Split('\n', StringSplitOptions.None);
            if (lines.Length < 6) continue;
            var authorDate = ParseDate(lines[5]) ?? timeProvider.GetUtcNow().UtcDateTime;
            commits.Add(new GitLightCommitDto
            {
                Sha = lines[0],
                ShortSha = lines[1],
                Message = lines[2],
                AuthorName = lines[3],
                AuthorEmail = lines[4],
                AuthorDate = authorDate,
                ParentShas = lines.Length > 6 && !string.IsNullOrWhiteSpace(lines[6])
                    ? [.. lines[6].Split(' ', StringSplitOptions.RemoveEmptyEntries)]
                    : [],
                RefNames = lines.Length > 7 ? ParseRefNames(lines[7]) : [],
                CommitDate = (lines.Length > 8 ? ParseDate(lines[8]) : null) ?? authorDate,
                SourceRef = lines.Length > 9 ? ParseSourceRef(lines[9]) : null
            });
        }
        return commits;
    }

    public async Task<int> GetCommitCountAsync(
        string diskPath, string? refName, string? search, GitCommitLogFilter? filter, CancellationToken ct)
    {
        GitLightCliService.EnsureRefArgsSafe(refName);
        // rev-list honours the same --grep/--author/--since/--until as log, so the total counts exactly
        // what the page lists.
        var args = new List<string> { "rev-list", "--count" };
        AddFilterArgs(args, search, filter);
        AddRevisionArgs(args, refName, filter);
        var (exitCode, output, _) = await git.RunGitAsync(diskPath, args, ct).ConfigureAwait(false);
        return exitCode == 0 && int.TryParse(output.Trim(), out var count) ? count : 0;
    }

    public async Task<List<string>> GetCommitAuthorsAsync(string diskPath, CancellationToken ct)
    {
        // Bounded walk: the most recent 5000 commits across every branch carry the authors worth offering.
        var (exitCode, output, _) = await git.RunGitAsync(diskPath, ["log", "--all", "--format=%an", "--max-count=5000"], ct).ConfigureAwait(false);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output)) return [];
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    // Builds the `git log` argv. Cursor pagination (afterSha) skips git's O(N) prologue; otherwise fall
    // back to --skip and warn past the deep threshold.
    internal List<string> BuildCommitLogArgs(
        string? refName, int skip, int take, string? search, string? afterSha, GitCommitLogFilter? filter)
    {
        var args = new List<string> { "log", LogFormat, "-z", "--source", $"--max-count={take}" };
        AddFilterArgs(args, search, filter);

        if (!string.IsNullOrEmpty(afterSha) && IsHexString(afterSha.AsSpan()))
        {
            // Cursor pagination: walk from the parent of the cursor. The afterSha hex check is
            // defence-in-depth against argument injection (ArgumentList already blocks shell interp).
            args.Add($"{afterSha}^");
            return args;
        }

        if (skip >= DeepSkipWarningThreshold)
        {
            s_deepSkipCounter.Add(1);
            logger.LogWarning(
                "GetCommitsAsync skip={Skip} is past the {Threshold}-deep threshold - git --skip is O(N). " +
                "Plumb a cursor (afterSha = last commit of previous page) through the controller to switch to keyset pagination.",
                skip, DeepSkipWarningThreshold);
        }
        args.Add($"--skip={skip}");
        AddRevisionArgs(args, refName, filter);
        return args;
    }

    /// <summary>
    /// The options that narrow the walk. The legacy <paramref name="search"/> stays a raw pattern; the
    /// grid's Message filter is matched literally (escaped). Both together must both match.
    /// </summary>
    private static void AddFilterArgs(List<string> args, string? search, GitCommitLogFilter? filter)
    {
        var greps = 0;
        if (!string.IsNullOrEmpty(search)) { args.Add($"--grep={search}"); greps++; }
        if (!string.IsNullOrEmpty(filter?.Message)) { args.Add($"--grep={EscapeBasicRegex(filter.Message)}"); greps++; }
        if (greps > 0) args.Add("-i");
        if (greps > 1) args.Add("--all-match");
        AddAuthorArgs(args, filter?.Authors);
        if (filter?.Since is { } since) args.Add($"--since={FormatGitDate(since)}");
        if (filter?.Until is { } until) args.Add($"--until={FormatGitDate(until)}");
    }

    /// <summary> The grid's Branch filter walks exactly the ticked branches, fully qualified so a
    /// name can only resolve to a branch; without it the caller's ref applies, none meaning every
    /// ref. </summary>
    private static void AddRevisionArgs(List<string> args, string? refName, GitCommitLogFilter? filter)
    {
        if (filter?.Branches is { Count: > 0 } branches)
        {
            foreach (var branch in branches)
            {
                GitLightCliService.EnsureRefArgsSafe(branch);
                args.Add($"refs/heads/{branch}");
            }
            return;
        }
        args.Add(string.IsNullOrEmpty(refName) ? "--all" : refName);
    }

    /// <summary> One <c>--author</c> per ticked name (git keeps a commit matching any of them). git
    /// matches the pattern against "Name &lt;email&gt;" as a basic regular expression, so the name
    /// is escaped and anchored on both sides: "Bob" never matches "Bobby" nor "Jim Bob". </summary>
    private static void AddAuthorArgs(List<string> args, IReadOnlyList<string>? authors)
    {
        if (authors is null) return;
        foreach (var author in authors.Where(author => !string.IsNullOrWhiteSpace(author)))
            args.Add($"--author=^{EscapeBasicRegex(author)} <");
    }

    private static string EscapeBasicRegex(string value)
    {
        var escaped = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            if (character is '\\' or '.' or '[' or ']' or '*' or '^' or '$') escaped.Append('\\');
            escaped.Append(character);
        }
        return escaped.ToString();
    }

    private static string FormatGitDate(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTime? ParseDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;

    // S-FEAT-G6T9: turn a `%D` decoration string ("HEAD -> main, origin/main, tag: v1.0") into clean
    // ref labels. The "HEAD -> " pointer prefix is dropped; "tag: " is kept so the UI can style tags.
    private static List<string> ParseRefNames(string decoration)
    {
        if (string.IsNullOrWhiteSpace(decoration)) return [];
        var refs = new List<string>();
        foreach (var raw in decoration.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = raw.StartsWith("HEAD -> ", StringComparison.Ordinal) ? raw["HEAD -> ".Length..] : raw;
            if (name == "HEAD") continue; // detached HEAD pointer with no branch - nothing to badge
            refs.Add(name);
        }
        return refs;
    }

    /// <summary> <c>%S</c> names the revision argument the walk came from. A branch reads as its
    /// short name ("refs/heads/main" or "main" become "main"), another ref keeps its kind
    /// ("tags/v1"), and a commit id passed as the walk's start (a commit page) is no branch at all.
    /// </summary>
    internal static string? ParseSourceRef(string source)
    {
        var value = source.Trim();
        if (value.Length == 0) return null;
        if (value.StartsWith("refs/heads/", StringComparison.Ordinal)) return value["refs/heads/".Length..];
        if (value.StartsWith("refs/", StringComparison.Ordinal)) return value["refs/".Length..];
        var revision = value.TrimEnd('^');
        return revision.Length >= 7 && IsHexString(revision.AsSpan()) ? null : value;
    }

    internal static bool IsHexString(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        return true;
    }
}
