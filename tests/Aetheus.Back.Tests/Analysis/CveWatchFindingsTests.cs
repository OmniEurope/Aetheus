// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Shared.Components.VulnerabilityWatch;

namespace Aetheus.Back.Tests.Analysis;

public class CveWatchFindingsTests
{
    private static CveWatchMatchRow Row(
        long id, string version, string externalId = "GHSA-1", string? cve = "CVE-2099-1", VulnerabilityMatchStatus status = VulnerabilityMatchStatus.Affected,
        AnalysisSeverity severity = AnalysisSeverity.High, string ecosystem = "NuGet", string package = "lib", string? fixedVersion = "2.0.0") =>
        new(id, 1, ecosystem, package, version, status, fixedVersion, externalId, cve, severity, "Summary");

    [Fact]
    public void TheFingerprint_IgnoresTheVersion_AndKeepsProjectCveAndPackage()
    {
        var first = CveWatchFindings.Draft(7, [Row(1, "1.0.0")]).Single();
        var bumped = CveWatchFindings.Draft(7, [Row(2, "1.5.0")]).Single();

        Assert.Equal(first.Fingerprint, bumped.Fingerprint);
        Assert.NotEqual(first.Fingerprint, CveWatchFindings.Draft(8, [Row(1, "1.0.0")]).Single().Fingerprint);
        Assert.NotEqual(first.Fingerprint, CveWatchFindings.Draft(7, [Row(1, "1.0.0", cve: "CVE-2099-2")]).Single().Fingerprint);
        Assert.NotEqual(first.Fingerprint, CveWatchFindings.Draft(7, [Row(1, "1.0.0", package: "other")]).Single().Fingerprint);
        Assert.Equal(64, first.Fingerprint.Length);
    }

    [Fact]
    public void TwoVersionsAndTwoAdvisoriesOfOneCve_AreOneFinding_WithTheirOccurrences()
    {
        var drafts = CveWatchFindings.Draft(7, [
            Row(1, "1.0.0", "GHSA-1", severity: AnalysisSeverity.Medium),
            Row(2, "1.1.0", "GHSA-2", severity: AnalysisSeverity.Critical),
            Row(3, "1.2.0", "GHSA-3", status: VulnerabilityMatchStatus.ToVerify)]);

        var draft = Assert.Single(drafts);
        Assert.Equal(3, draft.Matches.Count);
        Assert.Equal(AnalysisSeverity.Critical, draft.Severity);
        Assert.Equal(AnalysisConfidence.High, draft.Confidence);
        Assert.Equal("CVE-2099-1", draft.RuleId);
        Assert.Contains("GHSA-2", draft.HelpUri, StringComparison.Ordinal);
    }

    [Fact]
    public void AMatchToVerify_IsALowConfidenceFinding_NeverAHighOne()
    {
        var draft = CveWatchFindings.Draft(7, [Row(1, "1.0.0", status: VulnerabilityMatchStatus.ToVerify, fixedVersion: null)]).Single();

        Assert.Equal(AnalysisConfidence.Low, draft.Confidence);
        Assert.Contains("could not be compared", draft.Message, StringComparison.Ordinal);
        Assert.Contains("no fixed version is stated", draft.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdvisoryWithoutACve_UsesItsOwnIdentifierAsTheRule()
    {
        var draft = CveWatchFindings.Draft(7, [Row(1, "1.0.0", "GHSA-9", cve: null)]).Single();

        Assert.Equal("GHSA-9", draft.RuleId);
    }

    [Theory]
    [InlineData("NuGet", AnalysisCategory.Dependencies)]
    [InlineData("npm", AnalysisCategory.Dependencies)]
    [InlineData("Debian:12", AnalysisCategory.Container)]
    [InlineData("Alpine:v3.19", AnalysisCategory.Container)]
    [InlineData("Ubuntu:22.04", AnalysisCategory.Container)]
    // What a project runs on is filed where the pipeline function scan-dotnet-runtime-cve already files the .NET runtime
    // CVEs (docs/security/analysis-platform-operations.md): the container category, never a library dependency.
    [InlineData("runtime", AnalysisCategory.Container)]
    [InlineData("docker", AnalysisCategory.Container)]
    public void TheCategory_FollowsTheEcosystem(string ecosystem, AnalysisCategory expected) =>
        Assert.Equal(expected, CveWatchFindings.CategoryOf(ecosystem));

    /// <summary>osv.dev does not know the identifiers the runtime feeds invent: each links to the page of its real source.</summary>
    [Theory]
    [InlineData("GHSA-1", "2.0.0", "https://osv.dev/vulnerability/GHSA-1")]
    [InlineData("DOTNET-10.0-CVE-2026-111497", "10.0.14", "https://msrc.microsoft.com/update-guide/vulnerability/CVE-2026-111497")]
    [InlineData("NODEJS-SECURITY-20", "20.20.2", "https://nodejs.org/en/blog/vulnerability")]
    [InlineData("PHP-8.5-CVE-2026-91768", "8.5.11", "https://www.php.net/ChangeLog-8.php#8.5.11")]
    [InlineData("PHP-8.5-GHSA-w8hw-w9fx-j79w", "8.5.12", "https://www.php.net/ChangeLog-8.php#8.5.12")]
    [InlineData("PHP-7.4-SECURITY-7.4.9", "7.4.9", "https://www.php.net/ChangeLog-7.php#7.4.9")]
    [InlineData("PHP-8.5-CVE-2026-91768", null, "https://www.php.net/ChangeLog-8.php")]
    [InlineData("EOL-nodejs-20", null, "https://endoflife.date/nodejs")]
    [InlineData("EOL-dotnet-7", null, "https://endoflife.date/dotnet")]
    public void TheHelpLink_PointsToTheSourceThatPublishedTheAdvisory(string externalId, string? fixedVersion, string expected)
    {
        var draft = CveWatchFindings.Draft(7, [Row(1, "1.0.0", externalId, ecosystem: "docker", package: "node", fixedVersion: fixedVersion)]).Single();

        Assert.Equal(expected, draft.HelpUri);
    }
}
