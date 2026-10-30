// SPDX-License-Identifier: EUPL-1.2
using System.IO.Compression;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

public sealed class AnalysisGateOperationExecutorTests
{
    [Theory]
    [InlineData(AnalysisGateStatus.Passed, 0)]
    [InlineData(AnalysisGateStatus.Warning, 0)]
    [InlineData(AnalysisGateStatus.Blocked, 0)]
    [InlineData(AnalysisGateStatus.Error, 1)]
    public async Task ExecuteAsync_PublishesBothSummariesAndEnforcesAggregateVerdict(
        AnalysisGateStatus status,
        int expectedExitCode)
    {
        var api = Substitute.For<IServerApiClient>();
        api.GetAnalysisRunGateAsync(42, "security", Arg.Any<CancellationToken>()).Returns(new AnalysisRunGateDto
        {
            PipelineRunId = 42,
            Status = status,
            ReportCount = 1,
            Reports = [new AnalysisRunGateReportDto { ReportId = 7, ScannerName = "Scanner", ScannerVersion = "1" }],
            Findings = [new AnalysisRunGateFindingDto { FindingId = 9, RuleId = "R1", Title = "Finding", Severity = AnalysisSeverity.High, IsNew = true }]
        });
        api.UploadArtifactAsync(42, Arg.Any<string>(), "Gate", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifactDto { Id = 1 });
        var executor = new AnalysisGateOperationExecutor(api);

        var result = await executor.ExecuteAsync(OperationKind.PipelineEvaluateAnalysisGate, "security",
            new Dictionary<string, string> { ["AETHEUS_RUN_ID"] = "42", ["AETHEUS_STAGE_NAME"] = "Gate" },
            60, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(expectedExitCode, result.ExitCode);
        await api.Received(1).GetAnalysisRunGateAsync(
            42, "security", Arg.Any<CancellationToken>());
        await api.Received(2).UploadArtifactAsync(42, Arg.Any<string>(), "Gate", Arg.Any<Stream>(),
            Arg.Any<CancellationToken>());
    }

    // Run #2958: the gate printed "note incomplète" with no reason while three grading rules were rejected. The log
    // now names the grade, each rejected rule with the validator's message, or that nothing was measured.
    [Fact]
    public async Task ExecuteAsync_SaysWhyTheGradeIsFOrMissing()
    {
        var rejected = new AnalysisGradeMeasureDto
        {
            Key = "grade.duplication",
            Domain = AnalysisGradeDomain.CodeQuality,
            Required = true,
            Observed = true,
            Grade = AnalysisGrade.F,
            Rejection = "analysis grading rule 'grade.duplication' must define finite A, B, C, D and E thresholds."
        };
        var withRejection = await GateLinesAsync(new AnalysisGradeSummaryDto
        {
            OverallGrade = AnalysisGrade.F,
            Domains = [new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.CodeQuality, Grade = AnalysisGrade.F, Required = true, Measures = [rejected] }]
        });
        var nothingMeasured = await GateLinesAsync(new AnalysisGradeSummaryDto());

        Assert.Contains(withRejection, line => line.EndsWith("note F.", StringComparison.Ordinal));
        Assert.Contains(withRejection, line => line == "Règle de notation rejetée (note F) grade.duplication : "
            + "analysis grading rule 'grade.duplication' must define finite A, B, C, D and E thresholds.");
        Assert.Contains(nothingMeasured, line => line.EndsWith("note : aucune (aucune mesure).", StringComparison.Ordinal));
        Assert.DoesNotContain(nothingMeasured, line => line.Contains("incomplète", StringComparison.Ordinal));
    }

    private static async Task<List<string>> GateLinesAsync(AnalysisGradeSummaryDto grade)
    {
        var api = Substitute.For<IServerApiClient>();
        api.GetAnalysisRunGateAsync(42, "quality", Arg.Any<CancellationToken>())
            .Returns(new AnalysisRunGateDto { PipelineRunId = 42, Status = AnalysisGateStatus.Passed, Grade = grade });
        api.UploadArtifactAsync(42, Arg.Any<string>(), "Gate", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifactDto { Id = 1 });
        var lines = new List<string>();
        await new AnalysisGateOperationExecutor(api).ExecuteAsync(
            OperationKind.PipelineEvaluateAnalysisGate, "quality",
            new Dictionary<string, string> { ["AETHEUS_RUN_ID"] = "42", ["AETHEUS_STAGE_NAME"] = "Gate" },
            60, (line, _) => { lines.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        return lines;
    }

    // Run 2281: aetheus-candidate was refused on all three grade summaries at once, "the restored
    // artifact carries no .pipeline-artifacts/source-commit". The summaries are built in memory
    // rather than collected from a workspace path, so they carried none of the provenance every
    // other artifact travels with, and no consumer could ever restore one.
    [Fact]
    public async Task ExecuteAsync_SummaryArtifactsCarryTheWorkspaceProvenance()
    {
        var workspace = Directory.CreateTempSubdirectory("aetheus-gate-provenance").FullName;
        try
        {
            var metadata = Directory.CreateDirectory(Path.Combine(workspace, ".pipeline-artifacts")).FullName;
            File.WriteAllText(Path.Combine(metadata, "source-commit"), "58265026");
            File.WriteAllText(Path.Combine(metadata, "delivery-contract.json"), "{}");
            File.WriteAllText(Path.Combine(metadata, "artifact-provenance.json"), "{}");

            var entries = new List<List<string>>();
            var api = Substitute.For<IServerApiClient>();
            api.GetAnalysisRunGateAsync(42, "quality", Arg.Any<CancellationToken>())
                .Returns(new AnalysisRunGateDto { PipelineRunId = 42, Status = AnalysisGateStatus.Passed });
            api.UploadArtifactAsync(42, Arg.Any<string>(), "Gate", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    using var archive = new ZipArchive(call.ArgAt<Stream>(3), ZipArchiveMode.Read, leaveOpen: true);
                    entries.Add(archive.Entries.Select(e => e.FullName).ToList());
                    return new PipelineArtifactDto { Id = 1 };
                });

            var result = await new AnalysisGateOperationExecutor(api).ExecuteAsync(
                OperationKind.PipelineEvaluateAnalysisGate, "quality",
                new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_STAGE_NAME"] = "Gate",
                    ["WORKSPACE"] = workspace
                },
                60, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(2, entries.Count);
            foreach (var names in entries)
            {
                Assert.Contains(".pipeline-artifacts/source-commit", names);
                Assert.Contains(".pipeline-artifacts/delivery-contract.json", names);
                Assert.Contains(".pipeline-artifacts/artifact-provenance.json", names);
            }
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    // A workspace with nothing to prove must not gain invented provenance: refusing the restore is
    // the correct outcome there, and a fabricated source-commit is the defect the check exists for.
    [Fact]
    public async Task ExecuteAsync_WithoutWorkspaceProvenance_AddsNone()
    {
        var names = new List<string>();
        var api = Substitute.For<IServerApiClient>();
        api.GetAnalysisRunGateAsync(42, "quality", Arg.Any<CancellationToken>())
            .Returns(new AnalysisRunGateDto { PipelineRunId = 42, Status = AnalysisGateStatus.Passed });
        api.UploadArtifactAsync(42, Arg.Any<string>(), "Gate", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                using var archive = new ZipArchive(call.ArgAt<Stream>(3), ZipArchiveMode.Read, leaveOpen: true);
                names.AddRange(archive.Entries.Select(e => e.FullName));
                return new PipelineArtifactDto { Id = 1 };
            });

        await new AnalysisGateOperationExecutor(api).ExecuteAsync(
            OperationKind.PipelineEvaluateAnalysisGate, "quality",
            new Dictionary<string, string> { ["AETHEUS_RUN_ID"] = "42", ["AETHEUS_STAGE_NAME"] = "Gate" },
            60, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(names, n => n.StartsWith(".pipeline-artifacts/", StringComparison.Ordinal));
    }

    /// <summary>Recette R-23 (W8): the grade of the gate is published as an output of the step, so the candidate that
    /// triggered the analysis receives it (a trigger step hands up its child's outputs) and seals it into its release.
    /// A gate with no grade says <c>none</c>, never a letter it did not compute.</summary>
    [Theory]
    [InlineData("quality", AnalysisGrade.B, "##aetheus[setvariable name=ANALYSIS_QUALITY_GRADE]B")]
    [InlineData("security", AnalysisGrade.F, "##aetheus[setvariable name=ANALYSIS_SECURITY_GRADE]F")]
    [InlineData("quality", null, "##aetheus[setvariable name=ANALYSIS_QUALITY_GRADE]none")]
    public async Task ExecuteAsync_PublishesTheGateGradeAsAnOutputVariable(string scope, AnalysisGrade? grade, string expected)
    {
        var lines = new List<string>();
        var api = Substitute.For<IServerApiClient>();
        api.GetAnalysisRunGateAsync(42, scope, Arg.Any<CancellationToken>()).Returns(new AnalysisRunGateDto
        {
            PipelineRunId = 42,
            Status = AnalysisGateStatus.Passed,
            Grade = grade is null ? null : new AnalysisGradeSummaryDto { OverallGrade = grade }
        });
        api.UploadArtifactAsync(42, Arg.Any<string>(), "Gate", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifactDto { Id = 1 });

        var result = await new AnalysisGateOperationExecutor(api).ExecuteAsync(
            OperationKind.PipelineEvaluateAnalysisGate, scope,
            new Dictionary<string, string> { ["AETHEUS_RUN_ID"] = "42", ["AETHEUS_STAGE_NAME"] = "Gate" },
            60, (line, _) => { lines.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(expected, lines);
    }
}
