// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis.DependencyBump;
using static Aetheus.Back.Tests.Analysis.DependencyBumpTestKit;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// PLAN-004, lot 4, point 9: the base image of a Dockerfile and the runtime a repository names, under the contract of the
/// dependency bump: only what the match names changes, the rest of the file stays byte for byte, and what cannot be changed
/// without guessing is refused with its reason.
/// </summary>
public sealed class DependencyBumpImageAndRuntimeTests
{
    private const string MultiStage =
        "# syntax=docker/dockerfile:1\r\nFROM mcr.microsoft.com/dotnet/sdk:10.0.100 AS build\r\nRUN dotnet publish\r\n"
        + "FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/aspnet:10.0.7 AS final\r\nCOPY --from=build /app /app\r\n"
        + "FROM mcr.microsoft.com/dotnet/aspnet:10.0.7\r\nCOPY --from=final /app /app\r\n";

    private static DependencyBumpResult Image(string dockerfile, string image, string current, string target) =>
        Plan(Request("docker", image, current, target, "deploy/Dockerfile"), ("deploy/Dockerfile", dockerfile));

    [Fact]
    public void ABaseImage_MovesOnEveryFromThatNamesIt_AndTouchesNothingElse()
    {
        var result = Image(MultiStage, "mcr.microsoft.com/dotnet/aspnet", "10.0.7", "10.0.14");

        Assert.Equal(DependencyBumpOutcome.Complete, result.Outcome);
        var edit = Assert.Single(result.Edits);
        Assert.Equal("deploy/Dockerfile", edit.Path);
        Assert.Equal(MultiStage.Replace("aspnet:10.0.7", "aspnet:10.0.14", StringComparison.Ordinal), edit.Content);
        Assert.Equal([4, 6], edit.Changes.Select(change => change.Line));
        Assert.Empty(result.LockRefreshes);
    }

    [Fact]
    public void AnImageVariant_IsKept_BecauseTheTargetNamesOnlyTheVersion()
    {
        const string Dockerfile = "FROM node:20.11.0-alpine3.19\n";

        var result = Image(Dockerfile, "node", "20.11.0-alpine3.19", "20.11.1");

        Assert.Equal("FROM node:20.11.1-alpine3.19\n", Assert.Single(result.Edits).Content);
    }

    [Fact]
    public void ARegistryPort_IsNotMistakenForATag()
    {
        const string Dockerfile = "FROM registry.local:5000/team/app:1.2.3 AS base\n";

        var result = Image(Dockerfile, "registry.local:5000/team/app", "1.2.3", "1.2.4");

        Assert.Equal("FROM registry.local:5000/team/app:1.2.4 AS base\n", Assert.Single(result.Edits).Content);
    }

    [Fact]
    public void AnotherTagOfTheSameImage_IsNotWhatTheMatchNamed_AndStaysAsItIs()
    {
        const string Dockerfile = "FROM node:18.1.0\nFROM node:20.11.0\n";

        var result = Image(Dockerfile, "node", "20.11.0", "20.11.1");

        Assert.Equal("FROM node:18.1.0\nFROM node:20.11.1\n", Assert.Single(result.Edits).Content);
    }

    [Fact]
    public void AnImagePinnedByDigest_IsRefused_ThePlanCannotKnowTheDigestOfTheTarget()
    {
        const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        var result = Image($"FROM node:20.11.0@{Digest}\n", "node", $"20.11.0@{Digest}", "20.11.1");

        AssertRefused(result, DependencyBumpRefusal.VersionNotPinned);
        Assert.Contains("digest", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnImageBuiltFromABuildArgument_IsRefused_ThePlanDoesNotRewriteAnArgument()
    {
        var result = Image("ARG BASE=node:20.11.0\nFROM ${BASE}\n", "node", "20.11.0", "20.11.1");

        AssertRefused(result, DependencyBumpRefusal.VersionNotPinned);
    }

    [Fact]
    public void AnImageNoFromNames_IsRefusedAsNotDeclared()
    {
        var result = Image("FROM node:18.1.0\n", "node", "20.11.0", "20.11.1");

        AssertRefused(result, DependencyBumpRefusal.NotDeclared);
    }

    [Fact]
    public void AFromInACommentOrARunLine_IsNotAFrom()
    {
        const string Dockerfile = "# FROM node:20.11.0\nFROM node:20.11.0\nRUN echo FROM node:20.11.0\n";

        var result = Image(Dockerfile, "node", "20.11.0", "20.11.1");

        Assert.Equal("# FROM node:20.11.0\nFROM node:20.11.1\nRUN echo FROM node:20.11.0\n", Assert.Single(result.Edits).Content);
    }

    [Fact]
    public void AnImageTheFilesDoNotHold_IsRefusedAsManifestNotFound()
    {
        var result = Plan(Request("docker", "node", "20.11.0", "20.11.1", "Dockerfile"), ("other.txt", string.Empty));

        AssertRefused(result, DependencyBumpRefusal.ManifestNotFound);
    }

    [Theory]
    [InlineData("node; rm -rf /")]
    [InlineData("node:evil tag")]
    [InlineData("-node")]
    public void AnImageNameThatIsNotAnImageName_IsRefusedBeforeAnyFileIsRead(string image) =>
        AssertRefused(Image("FROM node:20.11.0\n", image, "20.11.0", "20.11.1"), DependencyBumpRefusal.InvalidRequest);

    [Fact]
    public void ATargetThatIsNotAboveTheImage_IsRefused() =>
        AssertRefused(Image("FROM node:20.11.0\n", "node", "20.11.0", "20.10.0"), DependencyBumpRefusal.TargetNotAbove);

    [Fact]
    public void AFloatingTag_IsNotAVersionToMoveForward() =>
        AssertRefused(Image("FROM node:latest\n", "node", "latest", "20.11.1"), DependencyBumpRefusal.TargetNotAbove);

    [Fact]
    public void GlobalJson_MovesTheSdkVersion_AndKeepsEverythingElse()
    {
        const string GlobalJson = "{\n  // pinned\n  \"sdk\": {\n    \"rollForward\": \"latestPatch\",\n    \"version\": \"10.0.401\"\n  }\n}\n";

        var result = Plan(Request("runtime", "dotnet", "10.0.401", "10.0.403", "global.json"), ("global.json", GlobalJson));

        SingleEdit(result, "global.json", GlobalJson, "\"version\": \"10.0.401\"", "\"version\": \"10.0.403\"");
        Assert.Equal(DependencyBumpOutcome.Complete, result.Outcome);
    }

    [Fact]
    public void AnSdkBandAboveARuntimeFix_IsRefused_TheTwoNumbersAreNotTheSameLine() =>
        AssertRefused(
            Plan(Request("runtime", "dotnet", "10.0.403", "10.0.14", "global.json"), ("global.json", "{\"sdk\":{\"version\":\"10.0.403\"}}")),
            DependencyBumpRefusal.TargetNotAbove);

    [Fact]
    public void GlobalJson_ThatStatesAnotherVersion_IsRefusedAsNotDeclared() =>
        AssertRefused(
            Plan(Request("runtime", "dotnet", "10.0.401", "10.0.403", "global.json"), ("global.json", "{\"sdk\":{\"version\":\"10.0.300\"}}")),
            DependencyBumpRefusal.NotDeclared);

    [Theory]
    [InlineData("20.11.0\n", "20.11.1\n")]
    [InlineData("v20.11.0\r\n", "v20.11.1\r\n")]
    [InlineData("20.11.0", "20.11.1")]
    public void Nvmrc_MovesTheVersion_KeepingItsPrefixAndItsLineEnding(string original, string expected)
    {
        var result = Plan(Request("runtime", "nodejs", "20.11.0", "20.11.1", ".nvmrc"), (".nvmrc", original));

        Assert.Equal(expected, Assert.Single(result.Edits).Content);
        Assert.Equal(DependencyBumpOutcome.Complete, result.Outcome);
    }

    [Theory]
    [InlineData("lts/iron\n")]
    [InlineData("20.11.0\n18.0.0\n")]
    public void Nvmrc_ThatIsNotThatOneVersion_IsRefused(string text) =>
        AssertRefused(
            Plan(Request("runtime", "nodejs", "20.11.0", "20.11.1", ".nvmrc"), (".nvmrc", text)),
            DependencyBumpRefusal.NotDeclared);

    [Fact]
    public void PackageJsonEngines_MovesAnExactNodeVersion()
    {
        const string PackageJson = "{\n  \"name\": \"app\",\n  \"engines\": {\n    \"node\": \"20.11.0\",\n    \"npm\": \"10.0.0\"\n  }\n}\n";

        var result = Plan(Request("runtime", "nodejs", "20.11.0", "20.11.1", "package.json"), ("package.json", PackageJson));

        SingleEdit(result, "package.json", PackageJson, "\"node\": \"20.11.0\"", "\"node\": \"20.11.1\"");
    }

    [Fact]
    public void ARangeOfEngines_IsNotAVersionToMoveForward() =>
        AssertRefused(
            Plan(Request("runtime", "nodejs", ">=18", "20.11.1", "package.json"), ("package.json", "{\"engines\":{\"node\":\">=18\"}}")),
            DependencyBumpRefusal.TargetNotAbove);

    [Fact]
    public void ComposerPhp_MovesAnExactRequirement_AndNamesTheCommandThatRefreshesTheLock()
    {
        const string Composer = "{\n  \"require\": {\n    \"php\": \"8.2.10\",\n    \"monolog/monolog\": \"^3.0\"\n  }\n}\n";

        var result = Plan(Request("runtime", "php", "8.2.10", "8.2.20", "app/composer.json"), ("app/composer.json", Composer), ("app/composer.lock", string.Empty));

        SingleEdit(result, "app/composer.json", Composer, "\"php\": \"8.2.10\"", "\"php\": \"8.2.20\"");
        Assert.Equal(DependencyBumpOutcome.NeedsPackageManager, result.Outcome);
        var refresh = Assert.Single(result.LockRefreshes);
        Assert.Equal("app/composer.lock", refresh.LockFile);
        Assert.Equal("app", refresh.WorkingDirectory);
        Assert.Equal("composer update --lock --no-install --no-scripts --no-plugins --no-interaction", refresh.Command);
    }

    [Fact]
    public void GoMod_MovesItsGoLine_AndNoOtherLine()
    {
        const string GoMod = "module example.com/app\n\ngo 1.22.1\n\ntoolchain go1.22.1\n\nrequire example.com/go 1.22.1\n";

        var result = Plan(Request("runtime", "go", "1.22.1", "1.22.5", "go.mod"), ("go.mod", GoMod));

        Assert.Equal(GoMod.Replace("\ngo 1.22.1\n", "\ngo 1.22.5\n", StringComparison.Ordinal), Assert.Single(result.Edits).Content);
    }

    [Fact]
    public void ARuntimeOnAFileThatDoesNotStateIt_IsRefused() =>
        AssertRefused(
            Plan(Request("runtime", "nodejs", "20.11.0", "20.11.1", "Dockerfile"), ("Dockerfile", "FROM node:20.11.0\n")),
            DependencyBumpRefusal.NotDeclared);

    [Fact]
    public void ARuntimeThisBumpDoesNotKnow_IsRefused() =>
        AssertRefused(
            Plan(Request("runtime", "cobol", "1.0.0", "1.0.1", "global.json"), ("global.json", "{}")),
            DependencyBumpRefusal.InvalidRequest);
}
