// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Xml.Linq;

namespace Aetheus.Back.Tests.Architecture;

public sealed class CoverageContractAuditTests
{
    private static readonly string[] ProductSuites =
    [
        "tests/Aetheus.Back.Tests",
        "tests/Aetheus.Front.Tests",
        "tests/Aetheus.Agent.Core.Tests"
    ];

    /// <summary>
    /// coverlet.MTP reads its settings from the testconfig.json beside each test executable, and a
    /// present file is authoritative: this whitelist is the whole of what leaves the denominator.
    /// </summary>
    [Fact]
    public void CoverageConfig_ExcludesOnlyCanonicalInfrastructureWhitelist()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tests", "testconfig.json")));
        var coverlet = document.RootElement.GetProperty("platformOptions").GetProperty("Coverlet");
        Assert.Equal("[Aetheus.Back]*,[Aetheus.Front]*,[Aetheus.Agent.Core]*", coverlet.GetProperty("include").GetString());
        var excludedTypes = coverlet.GetProperty("exclude").GetString()!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(
        [
            "[*]Microsoft.AspNetCore.OpenApi.Generated.*",
            "[*]Aetheus.Back.Data.Migrations.*",
            "[*]System.Runtime.CompilerServices.*"
        ], excludedTypes);
        Assert.Equal("**/*.g.cs,**/Migrations/*.cs,**/obj/**/*.cs", coverlet.GetProperty("excludeByFile").GetString());
        Assert.Equal("cobertura", coverlet.GetProperty("format").GetString());
    }

    [Fact]
    public void EveryTestExecutable_CarriesTheSharedCoverageConfig()
    {
        var targets = XDocument.Load(Path.Combine(RepoRoot(), "tests", "Directory.Build.targets"));
        var copied = targets.Descendants("None")
            .Single(item => (string?)item.Attribute("Link") == "testconfig.json");

        Assert.Equal("$(MSBuildThisFileDirectory)testconfig.json", (string?)copied.Attribute("Include"));
        Assert.Equal("PreserveNewest", (string?)copied.Attribute("CopyToOutputDirectory"));
    }

    [Fact]
    public void BothLaunchers_RunTheSameProductSuitesUnderTheSharedCoverageConfig()
    {
        var root = RepoRoot();
        var launcher = File.ReadAllText(Path.Combine(root, "scripts", "launch-core.ps1"));
        var linuxLauncher = File.ReadAllText(Path.Combine(root, "launch-linux.sh"));

        Assert.Contains("@(\"--coverlet\")", launcher, StringComparison.Ordinal);
        Assert.Contains("elseif ($Coverage) { $coverageDir } else", launcher, StringComparison.Ordinal);
        // PLAN-015 lot 0: the integration coverage lands beside the product reports, never among them.
        Assert.Contains("-ResultDirectory (Join-Path $coverageDir \"Integration\")", launcher, StringComparison.Ordinal);
        Assert.Contains("--coverlet --results-directory \"$COVERAGE_DIR/Integration\"", linuxLauncher, StringComparison.Ordinal);
        Assert.DoesNotContain("\"--results-directory\", $coverageDir", launcher, StringComparison.Ordinal);
        Assert.Contains("\"coverage.cobertura.*.xml\"", launcher, StringComparison.Ordinal);
        Assert.Contains("--coverlet --results-directory \"$COVERAGE_DIR\"", linuxLauncher, StringComparison.Ordinal);
        Assert.Contains("normalize-coverage-report.sh", linuxLauncher, StringComparison.Ordinal);
        foreach (var suite in ProductSuites)
        {
            Assert.Contains(suite.Replace('/', '\\'), launcher, StringComparison.Ordinal);
            Assert.Contains(suite, linuxLauncher, StringComparison.Ordinal);
        }
        Assert.Contains("TEST_CONFIGURATION=\"Release\"", linuxLauncher, StringComparison.Ordinal);
    }

    [Fact]
    public void Coverage_IsMergedAndTheDefaultGateIsEighty()
    {
        var root = RepoRoot();
        var launcher = File.ReadAllText(Path.Combine(root, "scripts", "launch-core.ps1"));
        var linuxLauncher = File.ReadAllText(Path.Combine(root, "launch-linux.sh"));
        var defaultPolicies = File.ReadAllText(Path.Combine(
            root, "src", "Aetheus.Back", "Components", "Analysis", "AnalysisDefaultPolicyCatalog.cs"));
        using var tools = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "dotnet-tools.json")));

        Assert.True(tools.RootElement.GetProperty("tools").TryGetProperty("dotnet-reportgenerator-globaltool", out _));
        Assert.Contains("\"quality.coverage.line\"", defaultPolicies, StringComparison.Ordinal);
        // Raised 75 -> 80 on 2026-08-21 (A360-74): the default trailed the product, which had been
        // above 80 for weeks. It stays Warn because every policy in that catalog warns - the catalog is
        // the system-wide default and a blocking default would fail projects that never opted in.
        // `a: 75` above is a different number on purpose: that is the grade-A scale, not the gate.
        Assert.Contains("AnalysisPolicyOperator.LessThan, 80, AnalysisGateBehavior.Warn",
            defaultPolicies, StringComparison.Ordinal);
        Assert.Contains("Expected exactly 3 product coverage reports", launcher, StringComparison.Ordinal);
        Assert.Contains("$_.Directory.Parent.FullName -eq $coverageDir", launcher, StringComparison.Ordinal);
        Assert.Contains("-reporttypes:Html;Cobertura", launcher, StringComparison.Ordinal);
        Assert.Contains("-assemblyfilters:+Aetheus.Back;+Aetheus.Front;+Aetheus.Agent.Core", launcher, StringComparison.Ordinal);
        Assert.Contains("line coverage is below the required 80.1%", launcher, StringComparison.Ordinal);
        Assert.Contains("Expected exactly 3 product coverage reports", linuxLauncher, StringComparison.Ordinal);
        Assert.Contains("-reporttypes:Html;Cobertura", linuxLauncher, StringComparison.Ordinal);
        Assert.Contains("-assemblyfilters:+Aetheus.Back;+Aetheus.Front;+Aetheus.Agent.Core", linuxLauncher, StringComparison.Ordinal);
        Assert.Contains("line coverage is below the required 80.1%", linuxLauncher, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Aetheus.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
