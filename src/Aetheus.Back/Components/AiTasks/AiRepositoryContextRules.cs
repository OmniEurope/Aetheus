// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.AiTasks;

/// <summary>
/// The limits on the repository content an AI run attaches to its prompt: the <c>context_paths</c>
/// and <c>git_range</c> of a pipeline <c>type: ai</c> step, and the same two settings of a project AI
/// task. One rule for both, so a path a pipeline refuses is refused on a task too. The agent reads
/// them from <c>AETHEUS_AI_CONTEXT_PATHS_JSON</c> and <c>AETHEUS_AI_GIT_RANGE</c>.
/// </summary>
internal static class AiRepositoryContextRules
{
    public const int MaxContextPaths = 32;
    public const int MaxPathLength = 260;
    public const int MaxGitRangeLength = 200;
    private const int MaxRevisionLength = 100;

    /// <summary>A relative path that stays inside the workspace: not rooted, no <c>.</c> or <c>..</c>
    /// segment, at most <see cref="MaxPathLength"/> characters.</summary>
    public static bool IsSafeWorkspaceRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Length > MaxPathLength)
            return false;
        return !value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
    }

    public static bool AreValidContextPaths(IReadOnlyCollection<string> paths) =>
        paths.Count <= MaxContextPaths && paths.All(IsSafeWorkspaceRelativePath);

    /// <summary>The pipeline <c>git_range</c> rule: not blank, at most <see cref="MaxGitRangeLength"/>
    /// characters. A pipeline range may still carry variables substituted at dispatch time.</summary>
    public static bool IsValidGitRange(string? range) =>
        !string.IsNullOrWhiteSpace(range) && range.Length <= MaxGitRangeLength;

    /// <summary>A literal commit range <c>A..B</c> (for example <c>HEAD~10..HEAD</c>), with the
    /// revision characters the agent accepts. A task has no variable to substitute, so its range must
    /// already be one; the agent would otherwise fall back to the last commits without saying so here.</summary>
    public static bool IsCommitRange(string range)
    {
        var parts = range.Split("..");
        return parts.Length == 2 && parts.All(IsRevision);
    }

    private static bool IsRevision(string revision) =>
        revision.Length is > 0 and <= MaxRevisionLength
        && revision[0] is not ('-' or '.')
        && revision.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '/' or '~' or '^' or '.' or '-');
}
