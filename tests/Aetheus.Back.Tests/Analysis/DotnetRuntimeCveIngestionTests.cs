// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Functions;
using Aetheus.Back.Components.Analysis;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// Recette R-90: the report the Security stage publishes for the .NET runtime CVEs (<c>scan-dotnet-runtime-cve</c>, then a
/// <c>lint</c> step with <c>analysis_category: container</c>) is read by the same SARIF ingestion as the image scanners'.
/// The producer is the real one, the parser is the real one: the finding reaches the security gate as a High container
/// finding, and it is the same finding whichever runtime version the image ships.
/// </summary>
public sealed class DotnetRuntimeCveIngestionTests
{
    private static DotnetRuntimeCveFinding Finding(string installed)
    {
        var version = DotnetVersion.TryParse(installed)!;
        var pack = new DotnetRuntimePack(
            "Microsoft.AspNetCore.App.Runtime.linux-x64", version,
            $"usr/share/dotnet/shared/Microsoft.AspNetCore.App/{installed}/Microsoft.AspNetCore.App.deps.json");
        var advisory = new DotnetRuntimeAdvisory(
            "CVE-2026-111497", "https://cve.mitre.org/cgi-bin/cvename.cgi?name=CVE-2026-111497",
            DotnetVersion.TryParse("10.0.14")!, "2026-10-25", DotnetVersion.TryParse("10.0.14")!);
        return new DotnetRuntimeCveFinding("aetheus-back.tar", [pack], advisory);
    }

    [Fact]
    public void TheRuntimeCve_IsAHighContainerFinding_OfTheSecurityGateScope()
    {
        var parsed = SarifAnalysisParser.Parse(DotnetRuntimeCveSarif.Build([Finding("10.0.13")]), AnalysisCategory.Container);

        var finding = Assert.Single(parsed);
        Assert.Equal("CVE-2026-111497", finding.RuleId);
        Assert.Equal(AnalysisCategory.Container, finding.Category);
        Assert.Equal(AnalysisSeverity.High, finding.Severity);
        Assert.Equal(AnalysisConfidence.High, finding.Confidence);
        Assert.Contains("CVE-2026-111497", finding.Title, StringComparison.Ordinal);
        Assert.Contains("installed Microsoft.AspNetCore.App.Runtime.linux-x64 10.0.13, fixed in 10.0.14", finding.Message, StringComparison.Ordinal);
        Assert.Equal("https://cve.mitre.org/cgi-bin/cvename.cgi?name=CVE-2026-111497", finding.HelpUri);
        Assert.EndsWith("Microsoft.AspNetCore.App.deps.json", finding.FilePath, StringComparison.Ordinal);
        Assert.Equal(AnalysisGateScopes.Security, AnalysisGateScopes.ForCategory(finding.Category));
    }

    [Fact]
    public void TheFindingKeepsItsIdentityWhileTheImageKeepsAnOlderRuntime()
    {
        var onThirteen = Assert.Single(SarifAnalysisParser.Parse(DotnetRuntimeCveSarif.Build([Finding("10.0.13")]), AnalysisCategory.Container));
        var onTwelve = Assert.Single(SarifAnalysisParser.Parse(DotnetRuntimeCveSarif.Build([Finding("10.0.12")]), AnalysisCategory.Container));

        Assert.Equal(onThirteen.Fingerprint, onTwelve.Fingerprint);
    }

    [Fact]
    public void ARunWithNoCve_IsAnEmptyReportThatClosesTheFindingsOfTheLastRun()
    {
        Assert.Empty(SarifAnalysisParser.Parse(DotnetRuntimeCveSarif.Build([]), AnalysisCategory.Container));
    }
}
