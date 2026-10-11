// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Git;

/// <summary>
/// Decision D13 (BACK-013): whether a <see cref="GitBranchWriter"/> may move a branch, given the
/// protection rules of its repository. When several rules match one branch the strictest level wins,
/// so a broad <c>*</c> rule never loosens a stricter rule on <c>main</c>.
/// </summary>
public static class GitBranchWritePolicy
{
    public static BranchProtectionLevel LevelOf(string branch, IEnumerable<BranchProtectionRule> rules) =>
        rules.Where(rule => GitBranchPatternMatcher.Matches(branch, rule.Pattern))
            .Select(rule => rule.ProtectionLevel)
            .DefaultIfEmpty(BranchProtectionLevel.NotProtected)
            .Max();

    public static bool Allows(BranchProtectionLevel level, GitBranchWriter writer) => writer switch
    {
        GitBranchWriter.PullRequestMerge => true,
        GitBranchWriter.PipelineAdvance => level != BranchProtectionLevel.StrictlyProtected,
        GitBranchWriter.HumanPush => level == BranchProtectionLevel.NotProtected,
        _ => throw new ArgumentOutOfRangeException(nameof(writer), writer, null)
    };

    /// <summary>Why <paramref name="writer"/> may not move <paramref name="branch"/>, or null when it may.</summary>
    public static string? RefusalReason(
        string branch, IEnumerable<BranchProtectionRule> rules, GitBranchWriter writer)
    {
        var level = LevelOf(branch, rules);
        if (Allows(level, writer)) return null;
        return writer == GitBranchWriter.PipelineAdvance
            ? $"Branch '{branch}' is strictly protected: only a merged pull request moves it, pipelines included."
            : level == BranchProtectionLevel.StrictlyProtected
                ? $"Branch '{branch}' is strictly protected: only a merged pull request moves it, direct pushes are refused."
                : $"Branch '{branch}' requires a pull request: direct pushes are refused.";
    }
}
