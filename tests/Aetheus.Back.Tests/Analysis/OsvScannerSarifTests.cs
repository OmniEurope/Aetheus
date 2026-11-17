// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>Recette R-2: the SARIF OSV-Scanner v2.7.0 writes for a JavaScript lockfile (shape captured from a real run of the
/// pinned image against a package-lock.json holding lodash 4.17.15 and minimist 1.2.0, trimmed to three results) is read by the
/// same parser as every other scanner, with the severity the gate counts: the CVSS score of the rule, as Trivy's reports carry it.</summary>
public sealed class OsvScannerSarifTests
{
    private const string OsvReport = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": { "driver": { "name": "osv-scanner", "rules": [
              { "id": "CVE-2020-28500", "deprecatedIds": ["GHSA-29mw-wpgm-hmr9"],
                "shortDescription": { "text": "CVE-2020-28500: Regular Expression Denial of Service (ReDoS) in lodash" },
                "properties": { "security-severity": "5.3" } },
              { "id": "CVE-2020-8203",
                "shortDescription": { "text": "CVE-2020-8203: Prototype Pollution in lodash" },
                "properties": { "security-severity": "7.4" } },
              { "id": "CVE-2021-44906",
                "shortDescription": { "text": "CVE-2021-44906: Prototype Pollution in minimist" },
                "properties": { "security-severity": "9.8" } }
            ] } },
            "results": [
              { "ruleId": "CVE-2020-28500", "level": "warning", "kind": "fail",
                "message": { "text": "Package 'lodash@4.17.15' is vulnerable to 'CVE-2020-28500' (also known as 'GHSA-29mw-wpgm-hmr9')." },
                "partialFingerprints": { "primaryLocationLineHash": "2c71c552ca0e5d92b90ab0eaa1a6856e75e52b5df92061db541d2151b9da65d2" },
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "file:///src/package-lock.json", "index": -1 } } }] },
              { "ruleId": "CVE-2020-8203", "level": "warning", "kind": "fail",
                "message": { "text": "Package 'lodash@4.17.15' is vulnerable to 'CVE-2020-8203'." },
                "partialFingerprints": { "primaryLocationLineHash": "7a1" },
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "file:///src/package-lock.json", "index": -1 } } }] },
              { "ruleId": "CVE-2021-44906", "level": "warning", "kind": "fail",
                "message": { "text": "Package 'minimist@1.2.0' is vulnerable to 'CVE-2021-44906'." },
                "partialFingerprints": { "primaryLocationLineHash": "7a2" },
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "file:///src/web/package-lock.json", "index": -1 } } }] }
            ]
          }]
        }
        """;

    [Fact]
    public void Parse_ReadsTheOsvScannerReportAsDependencyFindings()
    {
        var findings = SarifAnalysisParser.Parse(OsvReport, AnalysisCategory.Dependencies);

        Assert.Equal(3, findings.Count);
        Assert.All(findings, finding =>
        {
            Assert.Equal("osv-scanner", finding.ToolName);
            Assert.Equal(AnalysisCategory.Dependencies, finding.Category);
        });
        Assert.Equal(["package-lock.json", "package-lock.json", "web/package-lock.json"], findings.Select(finding => finding.FilePath));
        Assert.Contains("lodash@4.17.15", findings[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_TheSeverityIsTheCvssScoreOfTheRule_NotTheWarningLevelOsvScannerWritesForEveryResult()
    {
        var findings = SarifAnalysisParser.Parse(OsvReport, AnalysisCategory.Dependencies);

        // Every result carries level "warning" (which alone would be Medium); the rule's score decides.
        Assert.Equal(
            [AnalysisSeverity.Medium, AnalysisSeverity.High, AnalysisSeverity.Critical],
            findings.Select(finding => finding.Severity));
    }

}
