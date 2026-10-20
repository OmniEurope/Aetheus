// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Exceptions;

namespace Aetheus.Back.Tests.Analysis;

public sealed class SarifAnalysisParserTests
{
    [Theory]
    // A container scanner reports the repository under its /src mount.
    [InlineData("file:///src/tests/security-rules/aetheus-security.py", "tests/security-rules/aetheus-security.py")]
    [InlineData("/src/src/Aetheus.Back/Program.cs", "src/Aetheus.Back/Program.cs")]
    // A path relative to the repository is already right, its own "src" folder included.
    [InlineData("src/Aetheus.Back/Program.cs", "src/Aetheus.Back/Program.cs")]
    [InlineData("deploy/scripts/run.sh", "deploy/scripts/run.sh")]
    public void R504_TheReportedPath_IsRelativeToTheRepository(string uri, string expected)
    {
        var sarif = $$"""
            {
              "version": "2.1.0",
              "runs": [{
                "tool": { "driver": { "name": "Ruff" } },
                "results": [{
                  "ruleId": "F821",
                  "message": { "text": "Undefined name" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "{{uri}}" }, "region": { "startLine": 8 } } }]
                }]
              }]
            }
            """;

        var finding = Assert.Single(SarifAnalysisParser.Parse(sarif, AnalysisCategory.CodeQuality));

        Assert.Equal(expected, finding.FilePath);
    }

    [Fact]
    public void Parse_ExtractsRuleLocationSeverityAndCwe()
    {
        var sarif = """
            {
              "version": "2.1.0",
              "runs": [{
                "tool": { "driver": {
                  "name": "OpenGrep",
                  "rules": [{
                    "id": "cs.sql-injection",
                    "shortDescription": { "text": "SQL injection" },
                    "helpUri": "https://example.test/rules/sql-injection",
                    "properties": { "tags": ["security", "CWE-89"], "security-severity": "9.2" }
                  }]
                }},
                "results": [{
                  "ruleId": "cs.sql-injection",
                  "message": { "text": "Untrusted SQL input" },
                  "locations": [{ "physicalLocation": {
                    "artifactLocation": { "uri": "file:///workspace/src/Query.cs" },
                    "region": { "startLine": 42, "endLine": 43 }
                  }}]
                }]
              }]
            }
            """;

        var finding = Assert.Single(SarifAnalysisParser.Parse(sarif, AnalysisCategory.Sast));

        Assert.Equal("OpenGrep", finding.ToolName);
        Assert.Equal("cs.sql-injection", finding.RuleId);
        Assert.Equal("SQL injection", finding.Title);
        Assert.Equal("CWE-89", finding.Cwe);
        Assert.Equal(AnalysisSeverity.Critical, finding.Severity);
        Assert.Equal("workspace/src/Query.cs", finding.FilePath);
        Assert.Equal(42, finding.StartLine);
        Assert.Equal(43, finding.EndLine);
        Assert.Equal(64, finding.Fingerprint.Length);
        Assert.Equal(64, finding.LocationHash.Length);
    }

    [Fact]
    public void Parse_SecretCategoryNeverPersistsReportedSecret()
    {
        const string secret = "super-secret-token-value";
        var sarif = $$"""
            { "runs": [{ "tool": { "driver": { "name": "Gitleaks" } }, "results": [{
              "ruleId": "generic-api-key",
              "level": "error",
              "message": { "text": "Leaked value {{secret}}" },
              "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "config/app.env" }, "region": { "startLine": 3 } } }]
            }] }] }
            """;

        var finding = Assert.Single(SarifAnalysisParser.Parse(sarif, AnalysisCategory.Secrets));

        Assert.DoesNotContain(secret, finding.Message, StringComparison.Ordinal);
        Assert.Equal("Potential secret detected by rule generic-api-key.", finding.Message);
    }

    [Fact]
    public void Parse_SameRulePathAndSymbolKeepsFingerprintAcrossLineChanges()
    {
        var first = Sarif(10);
        var second = Sarif(120);

        var firstFinding = Assert.Single(SarifAnalysisParser.Parse(first, AnalysisCategory.CodeQuality));
        var secondFinding = Assert.Single(SarifAnalysisParser.Parse(second, AnalysisCategory.CodeQuality));

        Assert.Equal(firstFinding.Fingerprint, secondFinding.Fingerprint);
        Assert.NotEqual(firstFinding.LocationHash, secondFinding.LocationHash);
    }

    [Fact]
    public void Parse_SameSemanticFindingKeepsFingerprintAcrossFileMoves()
    {
        var first = Sarif(10, "src/old/app.ts");
        var moved = Sarif(10, "src/new/app.ts");

        var firstFinding = Assert.Single(SarifAnalysisParser.Parse(first, AnalysisCategory.CodeQuality));
        var movedFinding = Assert.Single(SarifAnalysisParser.Parse(moved, AnalysisCategory.CodeQuality));

        Assert.Equal(firstFinding.Fingerprint, movedFinding.Fingerprint);
        Assert.NotEqual(firstFinding.LocationHash, movedFinding.LocationHash);
    }

    [Fact]
    public void R2055_TwoHistoryLeaksOfOneAuthor_AreTwoFindings()
    {
        // gitleaks `git` mode fills author, email, date and commit message: read first in key order,
        // `author` made every leak of one author a single finding.
        var monaco = GitleaksHistory("aaaa1111", "generic-api-key has detected secret for file Prometheus.Front/wwwroot/lib/editor.main.js.");
        var token = GitleaksHistory("bbbb2222", "generic-api-key has detected secret for file Prometheus.Agent.Linux/appsettings.json.");

        var first = Assert.Single(SarifAnalysisParser.Parse(monaco, AnalysisCategory.Secrets));
        var second = Assert.Single(SarifAnalysisParser.Parse(token, AnalysisCategory.Secrets));

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void R2055_TheSameHistoryLeak_KeepsItsFingerprint_WhenOnlyTheProvenanceChanges()
    {
        var reported = GitleaksHistory("aaaa1111", "generic-api-key has detected secret for file app.env.", author: "First Name");
        var renamedAuthor = GitleaksHistory("aaaa1111", "generic-api-key has detected secret for file app.env.", author: "Other Name");

        Assert.Equal(
            Assert.Single(SarifAnalysisParser.Parse(reported, AnalysisCategory.Secrets)).Fingerprint,
            Assert.Single(SarifAnalysisParser.Parse(renamedAuthor, AnalysisCategory.Secrets)).Fingerprint);
    }

    [Theory]
    // An explicit severity wins over every other signal.
    [InlineData("\"properties\": { \"severity\": \"info\", \"security-severity\": \"9.5\" }, \"level\": \"error\",", null, AnalysisSeverity.Info)]
    // The result's own security-severity, numeric or textual, mapped by CVSS band.
    [InlineData("\"properties\": { \"security-severity\": 9.0 },", null, AnalysisSeverity.Critical)]
    [InlineData("\"properties\": { \"security-severity\": \"7.0\" },", null, AnalysisSeverity.High)]
    [InlineData("\"properties\": { \"security-severity\": 4 },", null, AnalysisSeverity.Medium)]
    [InlineData("\"properties\": { \"security-severity\": 0.1 },", null, AnalysisSeverity.Low)]
    // The rule's security-severity is the fallback when the result has none.
    [InlineData("", "8.1", AnalysisSeverity.High)]
    // An unparsable explicit severity and a zero score fall through to the SARIF level.
    [InlineData("\"properties\": { \"severity\": \"dreadful\", \"security-severity\": 0 }, \"level\": \"ERROR\",", null, AnalysisSeverity.High)]
    [InlineData("\"level\": \"warning\",", null, AnalysisSeverity.Medium)]
    [InlineData("\"level\": \"note\",", null, AnalysisSeverity.Low)]
    [InlineData("\"level\": \"none\",", null, AnalysisSeverity.Info)]
    [InlineData("\"level\": \"unexpected\",", null, AnalysisSeverity.Medium)]
    [InlineData("", null, AnalysisSeverity.Medium)]
    public void Parse_SeverityFollowsExplicitThenScoreThenLevel(
        string resultSeverity, string? ruleScore, AnalysisSeverity expected)
    {
        var ruleProperties = ruleScore is null ? string.Empty : $$""", "properties": { "security-severity": "{{ruleScore}}" }""";
        var sarif = $$"""
            {
              "version": "2.1.0",
              "runs": [{
                "tool": { "driver": { "name": "Scanner", "rules": [{ "id": "R1"{{ruleProperties}} }] } },
                "results": [{
                  "ruleId": "R1",
                  {{resultSeverity}}
                  "message": { "text": "Finding" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "src/app.ts" }, "region": { "startLine": 3 } } }]
                }]
              }]
            }
            """;

        var finding = Assert.Single(SarifAnalysisParser.Parse(sarif, AnalysisCategory.Sast));

        Assert.Equal(expected, finding.Severity);
    }

    [Fact]
    public void Parse_MalformedSarifFailsClosed()
    {
        var exception = Assert.Throws<BadRequestException>(() =>
            SarifAnalysisParser.Parse("not-json", AnalysisCategory.Sast));

        Assert.Contains("Invalid SARIF", exception.Message, StringComparison.Ordinal);
    }

    private static string GitleaksHistory(string commit, string message, string author = "Same Author") => $$"""
        { "runs": [{ "tool": { "driver": { "name": "gitleaks" } }, "results": [{
          "ruleId": "generic-api-key",
          "message": { "text": "{{message}}" },
          "partialFingerprints": { "commitSha": "{{commit}}", "email": "dev@example.test", "author": "{{author}}", "date": "2026-04-05T09:03:12Z", "commitMessage": "a commit" },
          "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "app.env" }, "region": { "startLine": 3 } } }]
        }] }] }
        """;

    private static string Sarif(int line, string path = "src/app.ts") => $$"""
        { "runs": [{ "tool": { "driver": { "name": "ESLint" } }, "results": [{
          "ruleId": "complexity",
          "message": { "text": "Function is too complex" },
          "locations": [{
            "physicalLocation": { "artifactLocation": { "uri": "{{path}}" }, "region": { "startLine": {{line}} } },
            "logicalLocations": [{ "fullyQualifiedName": "renderPage" }]
          }]
        }] }] }
        """;
}
