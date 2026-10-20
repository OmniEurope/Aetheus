// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Aetheus.E2E;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>Parse E2E-exported accessibility SARIF with the real parser and map the lint category
/// to exactly one gate scope.</summary>
public sealed class AccessibilityAnalysisTests
{
    private static readonly AccessibilityViolation[] Violations =
    [
        new("/servers", "color-contrast", "serious", "Contrast", "Elements must meet contrast", "https://dequeuniversity.com/rules/axe/color-contrast",
            ["#status", ".badge"]),
        new("/projects", "button-name", "critical", "Buttons need text", "Buttons must have discernible text", null, ["#add"]),
        new("/", "region", "moderate", "Landmarks", "All page content should be contained by landmarks", null, ["footer"]),
        new("/", "list", "minor", "Lists", "Lists must only contain li elements", null, [])
    ];

    [Fact]
    public void TheSuitesSarif_ParsesIntoOneAccessibilityFindingPerElement_WithTheImpactAsSeverity()
    {
        var findings = SarifAnalysisParser.Parse(AccessibilitySarif.Build(Violations), AnalysisCategory.Accessibility);

        Assert.Equal(5, findings.Count);
        Assert.All(findings, finding => Assert.Equal(AnalysisCategory.Accessibility, finding.Category));
        Assert.All(findings, finding => Assert.Equal("axe-core", finding.ToolName));
        Assert.Equal(
            [AnalysisSeverity.Low, AnalysisSeverity.Medium, AnalysisSeverity.High, AnalysisSeverity.High, AnalysisSeverity.Critical],
            findings.Select(finding => finding.Severity).Order());
        Assert.Contains(findings, finding => finding.RuleId == "color-contrast" && finding.Message.Contains("#status", StringComparison.Ordinal));
    }

    /// <summary>The same violation keeps its identity from one run to the next; two elements are two findings.</summary>
    [Fact]
    public void Fingerprints_AreStableAcrossRuns_AndDistinctPerElement()
    {
        var first = SarifAnalysisParser.Parse(AccessibilitySarif.Build(Violations), AnalysisCategory.Accessibility);
        var second = SarifAnalysisParser.Parse(AccessibilitySarif.Build(Violations.Reverse()), AnalysisCategory.Accessibility);

        Assert.Equal(first.Select(finding => finding.Fingerprint).Order(), second.Select(finding => finding.Fingerprint).Order());
        Assert.Equal(5, first.Select(finding => finding.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void AnEmptyScan_IsAValidReportWithNoFinding()
    {
        Assert.Empty(SarifAnalysisParser.Parse(AccessibilitySarif.Build([]), AnalysisCategory.Accessibility));
    }

    /// <summary>
    /// A category in neither list was counted as security by one path and dropped by the other, so
    /// every category, present and future, must sit in exactly one scope.
    /// </summary>
    [Fact]
    public void EveryCategory_BelongsToExactlyOneGateScope()
    {
        foreach (var category in Enum.GetValues<AnalysisCategory>())
        {
            var inQuality = AnalysisGateScopes.Categories(AnalysisGateScopes.Quality).Contains(category);
            var inSecurity = AnalysisGateScopes.Categories(AnalysisGateScopes.Security).Contains(category);
            Assert.True(inQuality ^ inSecurity, $"{category} must be in exactly one gate scope.");
            Assert.Equal(inQuality ? AnalysisGateScopes.Quality : AnalysisGateScopes.Security, AnalysisGateScopes.ForCategory(category));
        }
        Assert.Equal(AnalysisGateScopes.Quality, AnalysisGateScopes.ForScannerCategory("Accessibility"));
    }

    [Theory]
    [InlineData(null, AnalysisCategory.CodeQuality)]
    [InlineData("code-quality", AnalysisCategory.CodeQuality)]
    [InlineData("accessibility", AnalysisCategory.Accessibility)]
    [InlineData("Accessibility", AnalysisCategory.Accessibility)]
    public void TheLintCategory_Parses(string? value, AnalysisCategory expected)
    {
        Assert.True(LintAnalysisCategories.TryParse(value, out var category));
        Assert.Equal(expected, category);
    }

    [Fact]
    public void AnalysisCategory_IsALintField_ParsedStrictly_AndLimitedToItsTwoValues()
    {
        const string yaml = """
            name: qa
            stages:
              - name: Accessibility
                agent: linux-01
                steps:
                  - name: Publish accessibility findings
                    type: lint
                    analysis_category: accessibility
                    target_files: [".qa-evidence/**/accessibility.sarif"]
            """;
        var parsed = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(yaml);
        Assert.Equal("accessibility", parsed.Stages[0].Steps[0].AnalysisCategory);
        // The field is honoured, so the editor must not announce it "will be ignored".
        var warnings = new List<string>();
        PipelineYamlDiagnostics.AppendUnknownPropertyWarnings(yaml, warnings, NullLogger.Instance);
        Assert.Empty(warnings);
        Assert.Empty(Errors(new PipelineStepDefinition { Name = "a", Type = "lint", AnalysisCategory = "accessibility" }));

        Assert.Contains(Errors(new PipelineStepDefinition { Name = "a", Type = "lint", AnalysisCategory = "sast" }),
            error => error.Contains("must be code-quality or accessibility", StringComparison.Ordinal));
        Assert.Contains(Errors(new PipelineStepDefinition { Name = "a", Shell = "echo", AnalysisCategory = "accessibility" }),
            error => error.Contains("only valid on a lint step", StringComparison.Ordinal));
    }

    private static List<string> Errors(PipelineStepDefinition step)
    {
        var definition = new PipelineYamlDefinition
        {
            Name = "qa",
            Stages = [new PipelineStageDefinition { Name = "Accessibility", Agent = "linux-01", Steps = [step] }]
        };
        var errors = new List<string>();
        PipelineDefinitionValidator.ValidateStages(definition, errors, []);
        return errors;
    }
}
