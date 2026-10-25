// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Frozen;
using System.Text;

namespace Aetheus.Front.Components.Git;

/// <summary>
/// Pure view helpers extracted from <see cref="GitRepositoryDetail"/> to keep the code-behind
/// within the per-file size budget without a <c>partial</c> split (see CLAUDE.md "Interdictions").
/// Stateless string/pattern formatting only - no component state.
/// </summary>
internal static class GitRepositoryViewHelpers
{
    public static bool BranchMatchesPattern(string branchName, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith('*'))
            return branchName.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(branchName, pattern, StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatBlame(List<GitLightBlameLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var author = line.AuthorName.Length > 15 ? line.AuthorName[..15] : line.AuthorName.PadRight(15);
            sb.AppendLine($"{line.ShortSha} {author} {line.AuthorDate.ToString("d"),-10} │ {line.Line}");
        }
        return sb.ToString();
    }

    public static string FormatSize(long bytes) => DisplayFormatting.Size(bytes);

    public static OmniTone GetPullRequestStatusBadge(PullRequestStatus status) => status switch
    {
        PullRequestStatus.Open => OmniTone.Success,
        PullRequestStatus.Merged => OmniTone.Accent,
        PullRequestStatus.Closed => OmniTone.Danger,
        PullRequestStatus.Draft => OmniTone.Neutral,
        _ => OmniTone.Neutral
    };

    public static string InferLanguage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return LanguagesByExtension.TryGetValue(ext, out var language) ? language : "plaintext";
    }

    /// <summary>The editor language of each known extension; a lookup instead of a string switch.</summary>
    private static readonly FrozenDictionary<string, string> LanguagesByExtension =
        new Dictionary<string, string>
        {
            [".cs"] = "csharp",
            [".js"] = "javascript",
            [".ts"] = "typescript",
            [".json"] = "json",
            [".xml"] = "xml",
            [".csproj"] = "xml",
            [".sln"] = "xml",
            [".props"] = "xml",
            [".yaml"] = "yaml",
            [".yml"] = "yaml",
            [".html"] = "html",
            [".htm"] = "html",
            [".css"] = "css",
            [".scss"] = "scss",
            [".md"] = "markdown",
            [".py"] = "python",
            [".sh"] = "shell",
            [".bash"] = "shell",
            [".ps1"] = "powershell",
            [".sql"] = "sql",
            [".dockerfile"] = "dockerfile",
            [".razor"] = "razor"
        }.ToFrozenDictionary(StringComparer.Ordinal);
}
