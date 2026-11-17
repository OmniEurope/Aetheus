// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>Each expected producer claims its own reports once; what is not claimed is reported missing,
/// and a DAST run refuses a DAST report no task asked for.</summary>
public sealed class AnalysisProducerMatcherTests
{
    [Fact]
    public void Coverage_IsClaimedOncePerTask()
    {
        var missing = Tokens(
            [Producer(1, OperationKind.PipelinePublishCoverage), Producer(2, OperationKind.PipelinePublishCoverage)],
            [Report(10, "coverlet", AnalysisCategory.Coverage)]);

        Assert.Equal(["coverage-task:2"], missing);
    }

    [Fact]
    public void LintComplexityAndMutation_ExpectTheirOwnKeys()
    {
        var missing = Tokens(
            [
                Producer(3, OperationKind.PipelinePublishLint),
                Producer(4, OperationKind.PipelinePublishComplexity),
                Producer(5, OperationKind.PipelinePublishMutation),
            ],
            [
                Report(11, "PIPELINE-SARIF-LINT", AnalysisCategory.CodeQuality),
                Report(12, "roslyn-metrics", AnalysisCategory.CodeQuality),
                Report(13, "dotnet-architecture", AnalysisCategory.CodeQuality),
            ]);

        Assert.Equal(["dotnet-architecture-metrics:task:4", "stryker-mutation:task:5"], missing);
    }

    [Fact]
    public void NonDastScanner_AcceptsItsReportFromAnyStep()
    {
        var missing = Tokens(
            [Producer(6, OperationKind.PipelineRunScanner, "gitleaks", "scan")],
            [Report(14, "gitleaks", AnalysisCategory.Secrets, step: "other step")]);

        Assert.Empty(missing);
    }

    [Fact]
    public void DastScanner_NeedsItsOwnStepAndRefusesUnclaimedDastReports()
    {
        var missing = Tokens(
            [Producer(7, OperationKind.PipelineRunScanner, "zap-api", "zap staging")],
            [
                Report(15, "zap-api", AnalysisCategory.Dast, step: "zap production"),
                Report(16, "gitleaks", AnalysisCategory.Secrets),
            ]);

        Assert.Equal(["zap-api:task:7", "unexpected-report:15"], missing);
    }

    [Fact]
    public void DastScanner_ClaimingItsReport_LeavesNothingMissing()
    {
        var missing = Tokens(
            [Producer(8, OperationKind.PipelineRunScanner, "zap-api", "zap staging")],
            [Report(17, "zap-api", AnalysisCategory.Dast, step: "zap staging")]);

        Assert.Empty(missing);
    }

    // Recette R-2: the OSV-Scanner step is a producer of the gate like any other scanner, so a run whose
    // JavaScript dependency scan published nothing is reported missing, and its report satisfies it.
    [Fact]
    public void JavaScriptDependencyScanner_IsMissingWithoutItsReport_AndClaimedWithIt()
    {
        var producers = new[]
        {
            Producer(20, OperationKind.PipelineRunScanner, "trivy-dependencies", "Scan dependencies"),
            Producer(21, OperationKind.PipelineRunScanner, "osv-javascript", "Scan JavaScript dependencies"),
        };

        Assert.Equal(
            ["osv-javascript:task:21"],
            Tokens(producers, [Report(30, "trivy-dependencies", AnalysisCategory.Dependencies)]));
        Assert.Empty(Tokens(producers,
        [
            Report(30, "trivy-dependencies", AnalysisCategory.Dependencies),
            Report(31, "osv-javascript", AnalysisCategory.Dependencies),
        ]));
    }

    [Fact]
    public void OtherOperation_ExpectsNothing()
    {
        var missing = Tokens(
            [Producer(9, OperationKind.None)],
            []);

        Assert.Empty(missing);
    }

    private static List<string> Tokens(
        IReadOnlyCollection<ExpectedAnalysisProducer> producers, IReadOnlyCollection<AnalysisRunGateReportDto> reports) =>
        [.. AnalysisProducerMatcher.FindMissing(producers, reports).Select(item => item.Token)];

    private static ExpectedAnalysisProducer Producer(int id, OperationKind operation, string command = "", string step = "step") =>
        new(id, operation, command, step);

    private static AnalysisRunGateReportDto Report(int id, string key, AnalysisCategory category, string? step = null) =>
        new() { ReportId = id, ScannerKey = key, Category = category, StepName = step };
}
