// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis.DependencyUpdates;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// Recette R-24: which release an automatic update takes for a base image or a runtime version. A patch release of the same
/// major.minor series only; every other case is refused with its reason, and nothing is read.
/// </summary>
public sealed class DependencyUpdateImageSelectionTests
{
    private const string Digest = "@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static CveWatchRecommendationDto Image(string target) =>
        new() { Kind = CveRecommendationKind.BumpBaseImage, TargetVersion = target };

    private static CveWatchRecommendationDto Runtime(string target) =>
        new() { Kind = CveRecommendationKind.BumpRuntime, TargetVersion = target };

    [Theory]
    [InlineData("10.0.14", "10.0.15", "10.0.15")]
    [InlineData("10.0.14" + Digest, "10.0.15", "10.0.15")]
    [InlineData("10.0.14-noble", "10.0.15", "10.0.15-noble")]
    [InlineData("10.0.14-alpine3.22" + Digest, "10.0.15", "10.0.15-alpine3.22")]
    [InlineData("20.11.0", "20.11.9", "20.11.9")]
    public void Choose_PatchOfTheSameSeriesOfAnImage_IsTakenAsTheTagTheFileWillCarry(string current, string target, string tag)
    {
        var choice = DependencyUpdateSelection.Choose("docker", current, Image(target));

        Assert.Null(choice.Refusal);
        Assert.Equal(tag, choice.Version);
        Assert.False(choice.IsMajor);
    }

    [Theory]
    [InlineData("v20.11.0")]
    [InlineData("8.2.5")]
    public void Choose_PatchOfTheSameSeriesOfARuntime_IsTakenAsTheVersionItself(string current)
    {
        var target = current.TrimStart('v')[..^1] + "9";

        var choice = DependencyUpdateSelection.Choose("runtime", current, Runtime(target));

        Assert.Null(choice.Refusal);
        Assert.Equal(target, choice.Version);
    }

    [Theory]
    [InlineData("10.0.14", "10.1.0")]
    [InlineData("10.0.14" + Digest, "10.1.2")]
    [InlineData("10.0.14", "11.0.0")]
    [InlineData("10.0.14", "9.0.20")]
    public void Choose_NewMinorOrMajorOfAnImage_IsRefusedAsNotAPatch(string current, string target)
    {
        var choice = DependencyUpdateSelection.Choose("docker", current, Image(target));

        Assert.Null(choice.Version);
        Assert.False(choice.IsMajor);
        Assert.True(choice.Refusal!.Contains("series", StringComparison.Ordinal) || choice.Refusal.Contains("not above", StringComparison.Ordinal), choice.Refusal);
    }

    [Fact]
    public void Choose_NewMinorOfARuntime_IsRefusedAsNotAPatch()
    {
        var choice = DependencyUpdateSelection.Choose("runtime", "20.11.0", Runtime("20.12.0"));

        Assert.Contains("20.11 series", choice.Refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CveRecommendationKind.NoFixYet)]
    [InlineData(CveRecommendationKind.EndOfLife)]
    [InlineData(CveRecommendationKind.BumpDependency)]
    [InlineData(CveRecommendationKind.MajorUpgrade)]
    public void Choose_NoFixedReleaseOfAnImage_IsRefused(CveRecommendationKind kind)
    {
        var choice = DependencyUpdateSelection.Choose("docker", "10.0.14", new CveWatchRecommendationDto { Kind = kind, TargetVersion = "10.0.15" });

        Assert.Null(choice.Version);
        Assert.Contains("No release of the image", choice.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_NoRecommendationOrNoTargetOfAnImage_IsRefused()
    {
        Assert.Contains("recommends nothing", DependencyUpdateSelection.Choose("docker", "10.0.14", null).Refusal, StringComparison.Ordinal);
        Assert.Contains("No release", DependencyUpdateSelection.Choose("docker", "10.0.14", Image(" ")).Refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.0.14", "10.0.14")]
    [InlineData("10.0.14", "10.0.13")]
    public void Choose_TargetNotAboveTheCurrentImage_IsRefused(string current, string target) =>
        Assert.Contains("not above", DependencyUpdateSelection.Choose("docker", current, Image(target)).Refusal, StringComparison.Ordinal);

    [Theory]
    [InlineData("10.0.15-rc.1")]
    [InlineData("10.0.15-noble")]
    [InlineData("10.0")]
    public void Choose_TargetThatIsNotAFullRelease_IsRefused(string target) =>
        Assert.NotNull(DependencyUpdateSelection.Choose("docker", "10.0.14", Image(target)).Refusal);

    [Theory]
    [InlineData("10.0")]
    [InlineData("latest")]
    [InlineData("10")]
    public void Choose_CurrentVersionThatIsNotARelease_IsRefused(string current) =>
        Assert.NotNull(DependencyUpdateSelection.Choose("docker", current, Image("10.0.15")).Refusal);

    [Fact]
    public void Ecosystems_HoldImagesRuntimesAndPackagist_ButNotAnythingElse()
    {
        Assert.Contains("docker", DependencyUpdateSelection.Ecosystems);
        Assert.Contains("runtime", DependencyUpdateSelection.Ecosystems);
        Assert.Contains("Packagist", DependencyUpdateSelection.Ecosystems);
        Assert.DoesNotContain("PyPI", DependencyUpdateSelection.Ecosystems);
    }
}
