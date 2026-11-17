// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis.DependencyBump;
using static Aetheus.Back.Tests.Analysis.DependencyBumpTestKit;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// Recette R-24: an image the repository pins by digest moves to <c>tag@digest</c> of the target, with the digest the caller read
/// from a registry; it never becomes a tag alone, and a digest is never invented or added to a line that did not pin one.
/// </summary>
public sealed class DependencyBumpImageDigestTests
{
    private const string Old = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string New = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string Image = "mcr.microsoft.com/dotnet/aspnet";

    private static DependencyBumpResult PlanImage(string dockerfile, string current, string target, string? digest) =>
        Plan(new DependencyBumpRequest("docker", Image, current, target, "deploy/Dockerfile", digest), ("deploy/Dockerfile", dockerfile));

    [Fact]
    public void ADigestPinnedImage_MovesToTheTagAndTheDigestOfTheTarget_AndTouchesNothingElse()
    {
        const string Dockerfile = "FROM mcr.microsoft.com/dotnet/sdk:10.0.100 AS build\r\n"
            + $"FROM {Image}:10.0.14@{Old} AS final\r\nCOPY --from=build /app /app\r\n";

        var result = PlanImage(Dockerfile, $"10.0.14@{Old}", "10.0.15", New);

        Assert.Equal(DependencyBumpOutcome.Complete, result.Outcome);
        var edit = Assert.Single(result.Edits);
        Assert.Equal(Dockerfile.Replace($":10.0.14@{Old}", $":10.0.15@{New}", StringComparison.Ordinal), edit.Content);
        Assert.Equal(2, Assert.Single(edit.Changes).Line);
        Assert.DoesNotContain("10.0.15 ", edit.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ADigestPinnedImage_KeepsItsVariant()
    {
        var result = PlanImage($"FROM {Image}:10.0.14-noble@{Old}\n", $"10.0.14-noble@{Old}", "10.0.15-noble", New);

        Assert.Equal($"FROM {Image}:10.0.15-noble@{New}\n", Assert.Single(result.Edits).Content);
    }

    [Fact]
    public void ADigestPinnedImage_WithoutTheDigestOfTheTarget_IsRefusedNotWrittenAsATagOnly()
    {
        var result = PlanImage($"FROM {Image}:10.0.14@{Old}\n", $"10.0.14@{Old}", "10.0.15", null);

        AssertRefused(result, DependencyBumpRefusal.VersionNotPinned);
        Assert.Contains("digest", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void ALineThatPinsNoDigest_IsNeverGivenOne()
    {
        var result = PlanImage($"FROM {Image}:10.0.14\n", "10.0.14", "10.0.15", New);

        AssertRefused(result, DependencyBumpRefusal.InvalidRequest);
    }

    [Theory]
    [InlineData("sha256:abc")]
    [InlineData("latest")]
    [InlineData("sha256:ZZZZ111111111111111111111111111111111111111111111111111111111111")]
    public void ADigestThatIsNotASha256Digest_IsRefused(string digest) =>
        AssertRefused(PlanImage($"FROM {Image}:10.0.14@{Old}\n", $"10.0.14@{Old}", "10.0.15", digest), DependencyBumpRefusal.InvalidRequest);

    [Fact]
    public void OnlyTheLineThatNamesTheCurrentDigestMoves_AnotherPinOfTheSameTagStays()
    {
        const string Other = "sha256:3333333333333333333333333333333333333333333333333333333333333333";
        var dockerfile = $"FROM {Image}:10.0.14@{Old} AS a\nFROM {Image}:10.0.14@{Other} AS b\nFROM {Image}:10.0.14 AS c\n";

        var result = PlanImage(dockerfile, $"10.0.14@{Old}", "10.0.15", New);

        Assert.Equal($"FROM {Image}:10.0.15@{New} AS a\nFROM {Image}:10.0.14@{Other} AS b\nFROM {Image}:10.0.14 AS c\n", Assert.Single(result.Edits).Content);
    }

    [Fact]
    public void ADigestPinnedImageTheFileDoesNotHold_IsRefusedAsNotDeclared() =>
        AssertRefused(PlanImage($"FROM {Image}:10.0.13@{Old}\n", $"10.0.14@{Old}", "10.0.15", New), DependencyBumpRefusal.NotDeclared);

    [Fact]
    public void ATargetThatIsNotAboveADigestPinnedImage_IsRefused() =>
        AssertRefused(PlanImage($"FROM {Image}:10.0.14@{Old}\n", $"10.0.14@{Old}", "10.0.13", New), DependencyBumpRefusal.TargetNotAbove);
}
