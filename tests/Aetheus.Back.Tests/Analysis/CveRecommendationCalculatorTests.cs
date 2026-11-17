// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// The recommendation of a vulnerable package is a pure function of the package, the fixes its advisory states
/// and, when it was checked, the safe versions its registry publishes. One row per decision of the PLAN-004
/// "Action par CVE" list; the majors are compared as numbers (9 and 10 are different majors).
/// </summary>
public sealed class CveRecommendationCalculatorTests
{
    private const string None = "-";

    private static string[] Split(string list) => list == None ? [] : list.Split(',');

    private static SafeVersions? Registry(string spec)
    {
        if (spec == None) return null;
        var parts = spec.Split('|');
        return new SafeVersions(parts[0] == None ? null : parts[0], parts[1] == None ? null : parts[1]);
    }

    private static CveWatchRecommendationDto? Compute(
        string ecosystem, string current, ProjectInventoryItemKind kind, string? source, string fixes, string registry, bool endOfLife = false) =>
        CveRecommendationCalculator.Compute(new CveRecommendationInput(
            ecosystem, current, kind, source, Split(fixes), Registry(registry), endOfLife));

    // fixes of the advisory (all above the current version), registry "sameMajor|newest" or "-" when not checked.
    [Theory]
    [InlineData("npm", "1.4.0", "1.5.1", None, CveRecommendationKind.BumpDependency, "1.5.1")]
    [InlineData("npm", "1.4.0", "1.5.1,1.6.0", None, CveRecommendationKind.BumpDependency, "1.5.1")]
    [InlineData("npm", "1.4.0", "2.3.0,1.5.1", None, CveRecommendationKind.BumpDependency, "1.5.1")]
    [InlineData("npm", "1.4.0", "2.3.0", None, CveRecommendationKind.MajorUpgrade, "2.3.0")]
    [InlineData("npm", "1.4.0", "3.0.0,2.3.0", None, CveRecommendationKind.MajorUpgrade, "2.3.0")]
    [InlineData("NuGet", "9.9.9", "10.0.0", None, CveRecommendationKind.MajorUpgrade, "10.0.0")]
    [InlineData("NuGet", "10.0.1", "10.0.9", None, CveRecommendationKind.BumpDependency, "10.0.9")]
    [InlineData("Packagist", "1.27.0", "1.27.1", None, CveRecommendationKind.BumpDependency, "1.27.1")]
    [InlineData("Packagist", "2.9.0", "3.0.2", None, CveRecommendationKind.MajorUpgrade, "3.0.2")]
    [InlineData("PyPI", "1.0", "1.0.post1", None, CveRecommendationKind.BumpDependency, "1.0.post1")]
    [InlineData("Maven", "2.15.0", "2.15.1", None, CveRecommendationKind.BumpDependency, "2.15.1")]
    public void Dependency_WithoutRegistry_UsesTheOldestFixOfTheAdvisory_NotVerified(
        string ecosystem, string current, string fixes, string registry, CveRecommendationKind kind, string target)
    {
        var result = Compute(ecosystem, current, ProjectInventoryItemKind.Dependency, "package.json", fixes, registry);

        Assert.NotNull(result);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(target, result.TargetVersion);
        Assert.False(result.VerifiedAgainstRegistry);
        Assert.Equal(["package.json"], result.Files);
    }

    [Theory]
    [InlineData("npm", "1.4.0", "1.5.1", "1.6.0|2.3.0", CveRecommendationKind.BumpDependency, "1.6.0")]
    [InlineData("npm", "1.4.0", "1.5.1", "1.6.0|1.6.0", CveRecommendationKind.BumpDependency, "1.6.0")]
    [InlineData("npm", "1.4.0", "2.3.0", "-|2.3.0", CveRecommendationKind.MajorUpgrade, "2.3.0")]
    [InlineData("npm", "1.4.0", "-", "-|4.0.0", CveRecommendationKind.MajorUpgrade, "4.0.0")]
    [InlineData("NuGet", "9.9.9", "10.0.0", "-|10.0.0", CveRecommendationKind.MajorUpgrade, "10.0.0")]
    [InlineData("Packagist", "1.27.0", "1.27.1", "1.27.1|3.12.1", CveRecommendationKind.BumpDependency, "1.27.1")]
    public void Dependency_WithRegistry_UsesTheLatestSafeVersion_Verified(
        string ecosystem, string current, string fixes, string registry, CveRecommendationKind kind, string target)
    {
        var result = Compute(ecosystem, current, ProjectInventoryItemKind.Dependency, "composer.json", fixes, registry);

        Assert.NotNull(result);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(target, result.TargetVersion);
        Assert.True(result.VerifiedAgainstRegistry);
    }

    [Fact]
    public void Dependency_RegistryPublishesNoSafeVersion_IsNoFixYet_EvenWhenTheAdvisoryNamesAFix()
    {
        // The advisory says 1.5.1 but the registry lists nothing above 1.4.0 that is free of advisories:
        // nothing safe is published, so nothing is recommended.
        var result = Compute("npm", "1.4.0", ProjectInventoryItemKind.Dependency, null, "1.5.1", "-|-");

        Assert.NotNull(result);
        Assert.Equal(CveRecommendationKind.NoFixYet, result.Kind);
        Assert.Null(result.TargetVersion);
        Assert.True(result.VerifiedAgainstRegistry);
    }

    [Fact]
    public void Dependency_NoFixAnywhere_IsNoFixYet()
    {
        var result = Compute("npm", "1.4.0", ProjectInventoryItemKind.Dependency, null, None, None);

        Assert.NotNull(result);
        Assert.Equal(CveRecommendationKind.NoFixYet, result.Kind);
        Assert.Null(result.TargetVersion);
        Assert.False(result.VerifiedAgainstRegistry);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void NoFix_ButTheVersionIsOutOfSupport_IsEndOfLife()
    {
        var result = Compute("NuGet", "6.0.1", ProjectInventoryItemKind.Runtime, "global.json", None, None, endOfLife: true);

        Assert.NotNull(result);
        Assert.Equal(CveRecommendationKind.EndOfLife, result.Kind);
        Assert.Null(result.TargetVersion);
    }

    [Fact]
    public void EndOfLife_NeverHidesAFix_AFixedVersionStillGetsItsBump()
    {
        var result = Compute("NuGet", "6.0.1", ProjectInventoryItemKind.Runtime, "global.json", "6.0.36", None, endOfLife: true);

        Assert.NotNull(result);
        Assert.Equal(CveRecommendationKind.BumpRuntime, result.Kind);
        Assert.Equal("6.0.36", result.TargetVersion);
    }

    [Fact]
    public void EndOfLife_NeedsNoOrderOfVersions()
    {
        // An ecosystem with no comparator cannot say what is newer, but "no fix is coming" does not depend on it.
        var result = Compute("runtime", "6.0.1", ProjectInventoryItemKind.Runtime, "global.json", None, None, endOfLife: true);

        Assert.NotNull(result);
        Assert.Equal(CveRecommendationKind.EndOfLife, result.Kind);
    }

    [Fact]
    public void NoFix_InAnEcosystemWithNoOrderOfVersions_IsNotClaimed()
    {
        // "No fix yet" would be a statement about versions nobody can order: no recommendation at all.
        Assert.Null(Compute("Hex", "8.0.3", ProjectInventoryItemKind.Runtime, "global.json", None, None));
        Assert.Null(Compute("Hex", "1.21.0", ProjectInventoryItemKind.Dependency, "mix.lock", None, None));
    }

    [Fact]
    public void NoFix_ForAVersionTheComparatorCannotRead_IsNotClaimed() =>
        Assert.Null(Compute("npm", "not-a-version", ProjectInventoryItemKind.Dependency, "package.json", None, None));

    [Theory]
    [InlineData(ProjectInventoryItemKind.Runtime, CveRecommendationKind.BumpRuntime, "global.json")]
    [InlineData(ProjectInventoryItemKind.BaseImage, CveRecommendationKind.BumpBaseImage, "Dockerfile")]
    [InlineData(ProjectInventoryItemKind.SystemPackage, CveRecommendationKind.OsUpdate, null)]
    public void NonDependencyItems_GetTheActionOfTheirKind_WithTheOldestFixAboveTheCurrentVersion(
        ProjectInventoryItemKind itemKind, CveRecommendationKind kind, string? source)
    {
        // Fixes of two release branches: the one nearest above the current version is the target, in any major.
        var result = Compute("NuGet", "8.0.3", itemKind, source, "9.0.1,8.0.10", "9.0.1|9.0.1");

        Assert.NotNull(result);
        Assert.Equal(kind, result.Kind);
        Assert.Equal("8.0.10", result.TargetVersion);
        Assert.False(result.VerifiedAgainstRegistry);
        Assert.Equal(source is null ? Array.Empty<string>() : new[] { source }, result.Files);
    }

    [Fact]
    public void OsPackage_UsesTheDpkgOrderOfTheDistribution()
    {
        var result = Compute("Debian:12", "1.2.3-1", ProjectInventoryItemKind.SystemPackage, null, "1.2.3-1+deb12u2,1.2.3-1+deb12u1", None);

        Assert.NotNull(result);
        Assert.Equal(CveRecommendationKind.OsUpdate, result.Kind);
        Assert.Equal("1.2.3-1+deb12u1", result.TargetVersion);
    }

    [Theory]
    [InlineData(ProjectInventoryItemKind.Runtime, CveRecommendationKind.NoFixYet)]
    [InlineData(ProjectInventoryItemKind.BaseImage, CveRecommendationKind.NoFixYet)]
    [InlineData(ProjectInventoryItemKind.SystemPackage, CveRecommendationKind.NoFixYet)]
    public void NonDependencyItems_WithoutAFix_AreNoFixYet(ProjectInventoryItemKind itemKind, CveRecommendationKind kind)
    {
        var result = Compute("NuGet", "8.0.3", itemKind, "Dockerfile", None, None);

        Assert.NotNull(result);
        Assert.Equal(kind, result.Kind);
        Assert.Null(result.TargetVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankSourcePath_NamesNoFile(string? source)
    {
        var result = Compute("npm", "1.0.0", ProjectInventoryItemKind.Dependency, source, "1.0.1", None);

        Assert.NotNull(result);
        Assert.Empty(result.Files);
    }

    [Theory]
    [InlineData("sbom")]
    [InlineData("server:vps-1")]
    public void AnInventoryOrigin_IsNeverAFileToChange(string origin)
    {
        var result = Compute("NuGet", "10.0.5", ProjectInventoryItemKind.Dependency, origin, "10.0.14", None);

        Assert.NotNull(result);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void ARuntimePackFromTheSbom_IsRaisedThroughTheSdkOrTheBaseImage_NeverThroughTheSbom()
    {
        var result = CveRecommendationCalculator.Compute(new CveRecommendationInput(
            "NuGet", "10.0.5", ProjectInventoryItemKind.Runtime, "sbom", ["10.0.14"], null, false,
            RuntimeFiles: ["global.json"], ImageFiles: ["deploy/docker/Dockerfile.back"]));

        Assert.NotNull(result);
        Assert.Equal(CveRecommendationKind.BumpRuntime, result.Kind);
        Assert.Equal("10.0.14", result.TargetVersion);
        Assert.Equal(["global.json", "deploy/docker/Dockerfile.back"], result.Files);
    }

    [Fact]
    public void ARuntimePackFromTheSbom_WithNoFileKnownByTheInventory_NamesNoFile()
    {
        var result = Compute("NuGet", "10.0.5", ProjectInventoryItemKind.Runtime, "sbom", "10.0.14", None);

        Assert.NotNull(result);
        Assert.Equal(CveRecommendationKind.BumpRuntime, result.Kind);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void ABaseImageFromAServer_IsRaisedFromTheDockerfileOfTheInventory()
    {
        var result = CveRecommendationCalculator.Compute(new CveRecommendationInput(
            "docker", "20.11.0", ProjectInventoryItemKind.BaseImage, "server:vps-1", ["20.11.1"], null, false,
            RuntimeFiles: ["global.json"], ImageFiles: ["Dockerfile"]));

        Assert.NotNull(result);
        Assert.Equal(["Dockerfile"], result.Files);
    }

    [Fact]
    public void ASystemPackageFromTheSbom_NamesNoFile()
    {
        var result = CveRecommendationCalculator.Compute(new CveRecommendationInput(
            "Debian:12", "3.0.1-1", ProjectInventoryItemKind.SystemPackage, "sbom", ["3.0.2-1"], null, false, ["global.json"], ["Dockerfile"]));

        Assert.NotNull(result);
        Assert.Equal(CveRecommendationKind.OsUpdate, result.Kind);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void ARuntimeWithItsOwnFile_KeepsThatFileOnly()
    {
        var result = CveRecommendationCalculator.Compute(new CveRecommendationInput(
            "runtime", "8.0.3", ProjectInventoryItemKind.Runtime, "global.json", ["8.0.10"], null, false, ["other/global.json"], ["Dockerfile"]));

        Assert.NotNull(result);
        Assert.Equal(["global.json"], result.Files);
    }
}
