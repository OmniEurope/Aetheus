// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Git;

/// <summary>Decision D13 (BACK-013): each protection level against each path a branch moves through.</summary>
public sealed class GitBranchWritePolicyTests
{
    [Theory]
    [InlineData(BranchProtectionLevel.NotProtected, GitBranchWriter.HumanPush, true)]
    [InlineData(BranchProtectionLevel.NotProtected, GitBranchWriter.PullRequestMerge, true)]
    [InlineData(BranchProtectionLevel.NotProtected, GitBranchWriter.PipelineAdvance, true)]
    [InlineData(BranchProtectionLevel.ProtectedExceptPipelines, GitBranchWriter.HumanPush, false)]
    [InlineData(BranchProtectionLevel.ProtectedExceptPipelines, GitBranchWriter.PullRequestMerge, true)]
    [InlineData(BranchProtectionLevel.ProtectedExceptPipelines, GitBranchWriter.PipelineAdvance, true)]
    [InlineData(BranchProtectionLevel.StrictlyProtected, GitBranchWriter.HumanPush, false)]
    [InlineData(BranchProtectionLevel.StrictlyProtected, GitBranchWriter.PullRequestMerge, true)]
    [InlineData(BranchProtectionLevel.StrictlyProtected, GitBranchWriter.PipelineAdvance, false)]
    public void Allows_EachLevelForEachWriter(BranchProtectionLevel level, GitBranchWriter writer, bool expected) =>
        Assert.Equal(expected, GitBranchWritePolicy.Allows(level, writer));

    [Fact]
    public void LevelOf_ABranchNoRuleMatches_IsNotProtected() =>
        Assert.Equal(BranchProtectionLevel.NotProtected,
            GitBranchWritePolicy.LevelOf("feature/x", [Rule("main", BranchProtectionLevel.StrictlyProtected)]));

    [Fact]
    public void LevelOf_SeveralMatchingRules_TheStrictestWins()
    {
        BranchProtectionRule[] rules =
        [
            Rule("*", BranchProtectionLevel.NotProtected),
            Rule("main", BranchProtectionLevel.StrictlyProtected),
            Rule("release/*", BranchProtectionLevel.ProtectedExceptPipelines)
        ];

        Assert.Equal(BranchProtectionLevel.StrictlyProtected, GitBranchWritePolicy.LevelOf("main", rules));
        Assert.Equal(BranchProtectionLevel.ProtectedExceptPipelines, GitBranchWritePolicy.LevelOf("release/2", rules));
        Assert.Equal(BranchProtectionLevel.NotProtected, GitBranchWritePolicy.LevelOf("develop", rules));
    }

    [Fact]
    public void RefusalReason_NamesTheBranchAndWhatMovesIt()
    {
        BranchProtectionRule[] strict = [Rule("main", BranchProtectionLevel.StrictlyProtected)];
        BranchProtectionRule[] exceptPipelines = [Rule("main", BranchProtectionLevel.ProtectedExceptPipelines)];

        Assert.Equal(
            "Branch 'main' is strictly protected: only a merged pull request moves it, pipelines included.",
            GitBranchWritePolicy.RefusalReason("main", strict, GitBranchWriter.PipelineAdvance));
        Assert.Equal(
            "Branch 'main' is strictly protected: only a merged pull request moves it, direct pushes are refused.",
            GitBranchWritePolicy.RefusalReason("main", strict, GitBranchWriter.HumanPush));
        Assert.Equal(
            "Branch 'main' requires a pull request: direct pushes are refused.",
            GitBranchWritePolicy.RefusalReason("main", exceptPipelines, GitBranchWriter.HumanPush));
        Assert.Null(GitBranchWritePolicy.RefusalReason("main", exceptPipelines, GitBranchWriter.PipelineAdvance));
        Assert.Null(GitBranchWritePolicy.RefusalReason("main", strict, GitBranchWriter.PullRequestMerge));
    }

    [Theory]
    [InlineData(BranchProtectionLevel.NotProtected, false)]
    [InlineData(BranchProtectionLevel.ProtectedExceptPipelines, true)]
    [InlineData(BranchProtectionLevel.StrictlyProtected, true)]
    public void SettingTheLevel_KeepsTheColumnThePreviousColourEnforcesInStep(BranchProtectionLevel level, bool requiresPullRequest) =>
        Assert.Equal(requiresPullRequest, Rule("main", level).RequirePullRequest);

    private static BranchProtectionRule Rule(string pattern, BranchProtectionLevel level) =>
        new() { GitInternalRepoId = 1, Pattern = pattern, ProtectionLevel = level };
}
