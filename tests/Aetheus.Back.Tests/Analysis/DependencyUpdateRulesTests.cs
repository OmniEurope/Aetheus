// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis.DependencyUpdates;
using Aetheus.Back.Components.Git;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// PLAN-007 (recette R-9): the rules an automatic dependency update follows before anything is written. Patch and minor only,
/// a license that allows commercial resale, and a branch name git accepts and the platform recognizes as its own.
/// </summary>
public sealed class DependencyUpdateRulesTests
{
    private static CveWatchRecommendationDto Bump(string target) =>
        new() { Kind = CveRecommendationKind.BumpDependency, TargetVersion = target, VerifiedAgainstRegistry = true };

    [Theory]
    [InlineData("13.0.1", "13.0.3")]
    [InlineData("13.0.1", "13.4.0")]
    [InlineData("2.0.0-beta.1", "2.0.0-beta.2")]
    public void Choose_PatchOrMinorInTheSameMajor_IsTaken(string current, string target)
    {
        var choice = DependencyUpdateSelection.Choose("NuGet", current, Bump(target));

        Assert.Null(choice.Refusal);
        Assert.Equal(target, choice.Version);
    }

    private static CveWatchRecommendationDto Major(string target) =>
        new() { Kind = CveRecommendationKind.MajorUpgrade, TargetVersion = target, VerifiedAgainstRegistry = true };

    [Theory]
    [InlineData("NuGet", "12.0.3", "13.0.1")]
    [InlineData("NuGet", "12.0.3", "14.0.0")]
    [InlineData("npm", "4.17.1", "5.0.0")]
    public void Choose_MajorUpgrade_IsTakenAndFlaggedMajor(string ecosystem, string current, string target)
    {
        var choice = DependencyUpdateSelection.Choose(ecosystem, current, Major(target));

        Assert.Null(choice.Refusal);
        Assert.Equal(target, choice.Version);
        Assert.True(choice.IsMajor);
    }

    [Fact]
    public void Choose_PatchOrMinor_IsNeverFlaggedMajor() =>
        Assert.False(DependencyUpdateSelection.Choose("NuGet", "13.0.1", Bump("13.4.0")).IsMajor);

    [Fact]
    public void Choose_MajorUpgradeInTheSameMajor_IsRefusedAsNotTrusted()
    {
        var choice = DependencyUpdateSelection.Choose("NuGet", "12.0.3", Major("12.5.0"));

        Assert.Null(choice.Version);
        Assert.Contains("not trusted", choice.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_MajorUpgradeToAPreReleaseOrNotAbove_IsRefused()
    {
        Assert.Contains("pre-release", DependencyUpdateSelection.Choose("NuGet", "12.0.3", Major("13.0.0-rc.1")).Refusal, StringComparison.Ordinal);
        Assert.Contains("not above", DependencyUpdateSelection.Choose("NuGet", "12.0.3", Major("11.0.0")).Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_MajorUpgradeOfAnEcosystemNotWrittenYet_IsStillRefused()
    {
        var choice = DependencyUpdateSelection.Choose("PyPI", "1.0.0", Major("2.0.0"));

        Assert.Null(choice.Version);
        Assert.Contains("PyPI", choice.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_BumpThatCrossesAMajor_IsRefusedEvenWhenTheWatchCallsItABump()
    {
        var choice = DependencyUpdateSelection.Choose("NuGet", "12.0.3", Bump("13.0.1"));

        Assert.Null(choice.Version);
        Assert.Contains("major", choice.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("13.0.3", "13.0.3")]
    [InlineData("13.0.3", "13.0.1")]
    public void Choose_TargetNotAbove_IsRefused(string current, string target)
    {
        var choice = DependencyUpdateSelection.Choose("NuGet", current, Bump(target));

        Assert.Null(choice.Version);
        Assert.Contains("not above", choice.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_PreReleaseFromAStableVersion_IsRefused()
    {
        var choice = DependencyUpdateSelection.Choose("NuGet", "13.0.1", Bump("13.1.0-rc.1"));

        Assert.Null(choice.Version);
        Assert.Contains("pre-release", choice.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_EcosystemNotWrittenYet_IsRefused()
    {
        var choice = DependencyUpdateSelection.Choose("PyPI", "1.0.0", Bump("1.0.1"));

        Assert.Null(choice.Version);
        Assert.Contains("PyPI", choice.Refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("4.17.1", "4.17.21")]
    [InlineData("4.17.1", "4.18.0")]
    public void Choose_NpmPatchOrMinorInTheSameMajor_IsTaken(string current, string target)
    {
        var choice = DependencyUpdateSelection.Choose("npm", current, Bump(target));

        Assert.Null(choice.Refusal);
        Assert.Equal(target, choice.Version);
    }

    [Fact]
    public void Choose_NpmMajorPreReleaseOrNotAbove_IsRefused()
    {
        Assert.Contains("major", DependencyUpdateSelection.Choose("npm", "4.17.1", Bump("5.0.0")).Refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pre-release", DependencyUpdateSelection.Choose("npm", "4.17.1", Bump("4.18.0-rc.1")).Refusal, StringComparison.Ordinal);
        Assert.Contains("not above", DependencyUpdateSelection.Choose("npm", "4.17.21", Bump("4.17.9")).Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_NoRecommendationOrNoFix_IsRefused()
    {
        Assert.NotNull(DependencyUpdateSelection.Choose("NuGet", "1.0.0", null).Refusal);
        Assert.NotNull(DependencyUpdateSelection.Choose("NuGet", "1.0.0",
            new CveWatchRecommendationDto { Kind = CveRecommendationKind.NoFixYet }).Refusal);
    }

    [Theory]
    [InlineData("MIT")]
    [InlineData("Apache-2.0")]
    [InlineData("Apache-2.0 OR MIT")]
    [InlineData("(MIT OR GPL-3.0-only)")]
    [InlineData("MIT AND BSD-3-Clause")]
    [InlineData("Apache-2.0 WITH LLVM-exception")]
    [InlineData("BSD-3-Clause")]
    public void Allows_LicenseThatPermitsResale_IsAccepted(string expression) =>
        Assert.True(DependencyUpdateLicense.Allows(expression));

    [Theory]
    [InlineData("GPL-3.0-only")]
    [InlineData("AGPL-3.0-or-later")]
    [InlineData("MIT AND GPL-2.0-only")]
    [InlineData("LicenseRef-Proprietary")]
    [InlineData("")]
    [InlineData("MIT OR")]
    [InlineData("(MIT")]
    public void Allows_LicenseThatDoesNotPermitResaleOrIsUnreadable_IsRefused(string expression) =>
        Assert.False(DependencyUpdateLicense.Allows(expression));

    [Fact]
    public void Judge_NoLicenseKnown_RefusesAndSaysWhy()
    {
        var verdict = DependencyUpdateLicense.Judge(null);

        Assert.False(verdict.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Refusal));
    }

    [Fact]
    public void Judge_EveryRecordedLicenseMustAllowResale()
    {
        var verdict = DependencyUpdateLicense.Judge(new DependencyLicenseEvidence(["MIT", "GPL-3.0-only"], true));

        Assert.False(verdict.Allowed);
        Assert.Contains("GPL-3.0-only", verdict.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Judge_AllowedLicense_KeepsWhichVersionItWasReadFrom()
    {
        var verdict = DependencyUpdateLicense.Judge(new DependencyLicenseEvidence(["MIT"], false));

        Assert.True(verdict.Allowed);
        Assert.Equal("MIT", verdict.License);
        Assert.False(verdict.LicenseOfTargetVersion);
    }

    [Theory]
    [InlineData("Newtonsoft.Json", "13.0.3", "aetheus/deps/Newtonsoft.Json-13.0.3")]
    [InlineData("@scope/pkg", "1.2.3", "aetheus/deps/scope-pkg-1.2.3")]
    [InlineData("a..b", "1.0.0", "aetheus/deps/a.b-1.0.0")]
    public void For_GivesAValidBranchOfThePlatform(string package, string version, string expected)
    {
        var branch = DependencyUpdateBranch.For(package, version);

        Assert.Equal(expected, branch);
        Assert.True(DependencyUpdateBranch.IsOurs(branch));
    }

    [Theory]
    [InlineData("x.lock", "")]
    [InlineData("~^:?*[", "")]
    public void For_NothingValidLeft_GivesNoBranch(string package, string version) =>
        Assert.Null(DependencyUpdateBranch.For(package, version));

    [Theory]
    [InlineData("main")]
    [InlineData("aetheus/cve/x-1.0.0")]
    [InlineData("aetheus/deps/x..y")]
    [InlineData("aetheus/deps/x.lock")]
    [InlineData("aetheus/deps/x y")]
    [InlineData("aetheus/deps/")]
    [InlineData(null)]
    public void IsOurs_AnyOtherOrInvalidRef_IsNotOurs(string? branch) =>
        Assert.False(DependencyUpdateBranch.IsOurs(branch));
}
