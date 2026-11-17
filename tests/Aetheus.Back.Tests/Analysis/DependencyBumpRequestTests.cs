// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis.DependencyBump;
using static Aetheus.Back.Tests.Analysis.DependencyBumpTestKit;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// What a bump checks before it reads a file (PLAN-004, lot 4, points 6 to 8): the request comes from a registry and an advisory, and
/// its package name and target end up in a file and, for Composer, in a command line. Nothing is read, written or run: a bump is a
/// pure function of the files it is given.
/// </summary>
public sealed class DependencyBumpRequestTests
{
    private const string PackageJson = "{\n  \"dependencies\": {\n    \"lodash\": \"^4.17.15\"\n  }\n}\n";

    private static DependencyBumpResult Npm(string current, string target, string source = "package-lock.json", string package = "lodash") =>
        Plan(Request("npm", package, current, target, source), ("package.json", PackageJson), ("package-lock.json", "{}"));

    [Theory]
    [InlineData("4.17.21", "4.17.20")]
    [InlineData("4.17.15", "4.17.15")]
    [InlineData("4.17.21", "4.17.21-beta.1")]
    [InlineData("4.17.21-beta.2", "4.17.21-beta.1")]
    public void ATargetThatIsNotAboveTheCurrentVersion_IsRefused_TheSameVersionIncluded(string current, string target) =>
        AssertRefused(Npm(current, target), DependencyBumpRefusal.TargetNotAbove);

    [Fact]
    public void VersionsThatCannotBeOrdered_AreRefusedRatherThanGuessed()
    {
        var result = Npm("not-a-version", "4.17.21");

        AssertRefused(result, DependencyBumpRefusal.TargetNotAbove);
        Assert.Contains("cannot be ordered", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("4.17.21\"; evil")]
    [InlineData("4.17.21\n")]
    [InlineData("latest")]
    [InlineData("^4.17.21")]
    [InlineData("4.17.21 || 5")]
    [InlineData("")]
    public void ATargetThatIsNotAVersion_IsRefusedBeforeItIsWrittenAnywhere(string target) =>
        AssertRefused(Npm("4.17.15", target), DependencyBumpRefusal.InvalidRequest);

    [Theory]
    [InlineData("lodash; rm -rf /")]
    [InlineData("lodash\"")]
    [InlineData("$(whoami)")]
    [InlineData("")]
    public void APackageNameThatIsNotOneOfItsEcosystem_IsRefused(string package) =>
        AssertRefused(Npm("4.17.15", "4.17.21", package: package), DependencyBumpRefusal.InvalidRequest);

    [Theory]
    [InlineData("../package-lock.json")]
    [InlineData("a/../../package-lock.json")]
    [InlineData("C:/repo/package-lock.json")]
    [InlineData("")]
    public void ASourcePathLeavingTheRepository_IsRefused(string source) =>
        AssertRefused(Npm("4.17.15", "4.17.21", source), DependencyBumpRefusal.InvalidRequest);

    [Fact]
    public void AFilePathLeavingTheRepository_IsRefused() =>
        AssertRefused(
            Plan(Request("npm", "lodash", "4.17.15", "4.17.21", "package-lock.json"), ("../package.json", PackageJson), ("package-lock.json", "{}")),
            DependencyBumpRefusal.InvalidRequest);

    [Theory]
    [InlineData("Maven")]
    [InlineData("PyPI")]
    [InlineData("Go")]
    [InlineData("Debian:12")]
    public void AnEcosystemWithoutABump_IsRefusedPlainly(string ecosystem) =>
        AssertRefused(
            Plan(Request(ecosystem, "lodash", "1.0.0", "1.0.1", "package-lock.json"), ("package.json", PackageJson)),
            DependencyBumpRefusal.UnsupportedEcosystem);

    [Fact]
    public void WindowsSeparatorsInPaths_AreReadAsForwardSlashes()
    {
        var result = Plan(Request("npm", "lodash", "4.17.15", "4.17.21", "app\\package-lock.json"), ("app\\package.json", PackageJson), ("app\\package-lock.json", "{}"));

        Assert.Equal("app/package.json", Assert.Single(result.Edits).Path);
        Assert.Equal("app", Assert.Single(result.LockRefreshes).WorkingDirectory);
    }

    [Fact]
    public void ABump_NeverReturnsSuccessWithNothingToDo()
    {
        var done = DependencyBumpResult.Done([], []);

        AssertRefused(done, DependencyBumpRefusal.NothingToChange);
    }

    [Fact]
    public void TheAnswerIsAFunctionOfTheFilesGiven_TheSameFilesGiveTheSameAnswer()
    {
        var first = Npm("4.17.15", "4.17.21");
        var second = Npm("4.17.15", "4.17.21");

        Assert.Equal(first.Edits.Single().Content, second.Edits.Single().Content);
        Assert.Equal(first.LockRefreshes, second.LockRefreshes);
    }
}
