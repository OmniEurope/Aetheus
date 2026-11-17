// SPDX-License-Identifier: EUPL-1.2
using System.IO.Compression;
using Aetheus.Agent.Core.Executors;
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

    // Recette R-21, run 3100 of the nightly: "Verdict security: Error - 9 rapports, 0 findings, 0 blocages, 0 avertissements,
    // note A." and then only the incident. The log now names each refused producer and report, and shows no letter.
    [Fact]
    public async Task ExecuteAsync_AnErrorVerdict_NamesEachCause_AndShowsNoLetter()
    {
        var gate = new AnalysisRunGateDto
        {
            PipelineRunId = 42,
            Status = AnalysisGateStatus.Error,
            ReportCount = 9,
            Grade = new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.A },
            ErrorCauses =
            [
                new AnalysisGateErrorCauseDto
                {
                    Kind = AnalysisGateErrorCauseKind.ReportTimedOut,
                    PipelineRunId = 42,
                    ScannerKey = "trivy-dependencies",
                    ScannerName = "Trivy",
                    StepName = "Scan dependencies",
                    ReportId = 4,
                    PipelineArtifactId = 7,
                    ReportPath = ".analysis/trivy.json",
                    Detail = "Scanner timed out after 1200 s."
                },
                new AnalysisGateErrorCauseDto
                {
                    Kind = AnalysisGateErrorCauseKind.MissingProducer,
                    PipelineRunId = 42,
                    ScannerKey = "gitleaks",
                    StepName = "Scan secrets"
                },
                new AnalysisGateErrorCauseDto { Kind = AnalysisGateErrorCauseKind.DependencyTrack, PipelineRunId = 42, Detail = "pending" }
            ]
        };

        var (result, lines) = await RunGateAsync(gate);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(lines, line => line == "Cause de l'erreur : producteur Trivy (trivy-dependencies), étape « Scan dependencies », "
            + "rapport #4, artefact #7, fichier .analysis/trivy.json, run 42 : l'outil a dépassé son délai. Détail : Scanner timed out after 1200 s.");
        Assert.Contains(lines, line => line == "Cause de l'erreur : producteur gitleaks, étape « Scan secrets », run 42 : "
            + "la tâche n'a publié aucun rapport.");
        Assert.Contains(lines, line => line.StartsWith("Cause de l'erreur : ", StringComparison.Ordinal)
            && line.EndsWith("(état : pending).", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("Verdict security: Error", StringComparison.Ordinal)
            && line.EndsWith("note : aucune (verdict en erreur).", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("note A", StringComparison.Ordinal));
        Assert.Contains("##aetheus[setvariable name=ANALYSIS_SECURITY_GRADE]none", lines);
    }

    [Fact]
    public async Task ExecuteAsync_AnErrorVerdictWithNoCause_SaysTheServerGaveNone()
    {
        var (_, lines) = await RunGateAsync(new AnalysisRunGateDto { PipelineRunId = 42, Status = AnalysisGateStatus.Error });

        Assert.Contains(lines, line => line == "Cause de l'erreur : " + AnalysisGateErrorCauseText.NoneProvided);
    }

    // Recette R-21: the step's own error state (the task's failure code and reason) names the causes. Run 3100 ended with
    // the generic incident "Pipeline step 'Gate/Grade security' finished with status Failed and exit code 1."
    [Fact]
    public async Task ExecuteAsync_AnErrorVerdict_EndsTheStepWithAReasonThatNamesTheProducerAndTheReason()
    {
        var gate = new AnalysisRunGateDto
        {
            PipelineRunId = 42,
            Status = AnalysisGateStatus.Error,
            ErrorCauses =
            [
                new AnalysisGateErrorCauseDto
                {
                    Kind = AnalysisGateErrorCauseKind.MissingProducer,
                    PipelineRunId = 42,
                    ScannerKey = "gitleaks",
                    StepName = "Scan secrets",
                    Detail = "tâche #900 en échec, code de sortie 1. Dernières erreurs de son journal : Coverage report is empty."
                }
            ]
        };

        var (result, _) = await RunGateAsync(gate);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("ToolError", result.FailureCode);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("gitleaks", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains("Scan secrets", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains("la tâche n'a publié aucun rapport", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains("Coverage report is empty", result.FailureReason, StringComparison.Ordinal);
    }

    // The server cuts a failure reason longer than 512 characters without a mark: the reason is cut cleanly before.
    [Fact]
    public async Task ExecuteAsync_AnErrorVerdictWithManyCauses_KeepsTheReasonUnderTheServerCap()
    {
        var causes = Enumerable.Range(1, 30).Select(index => new AnalysisGateErrorCauseDto
        {
            Kind = AnalysisGateErrorCauseKind.ReportError,
            PipelineRunId = 42,
            ScannerKey = $"scanner-{index}",
            StepName = $"Step {index}",
            Detail = "Scanner exited with code 2."
        }).ToList();

        var (result, lines) = await RunGateAsync(new AnalysisRunGateDto
        {
            PipelineRunId = 42,
            Status = AnalysisGateStatus.Error,
            ErrorCauses = causes
        });

        Assert.True(result.FailureReason!.Length <= 480);
        Assert.StartsWith("Verdict security en erreur (30 causes) : producteur scanner-1", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(30, lines.Count(line => line.StartsWith("Cause de l'erreur : ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ExecuteAsync_AnErrorVerdictWithNoCause_StillEndsTheStepWithAReason()
    {
        var (result, _) = await RunGateAsync(new AnalysisRunGateDto { PipelineRunId = 42, Status = AnalysisGateStatus.Error });

        Assert.Equal("ToolError", result.FailureCode);
        Assert.Contains(AnalysisGateErrorCauseText.NoneProvided, result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_APassedVerdict_ReportsNoFailure()
    {
        var (result, _) = await RunGateAsync(new AnalysisRunGateDto { PipelineRunId = 42, Status = AnalysisGateStatus.Passed });

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.FailureCode);
        Assert.Null(result.FailureReason);
    }

    // A gate that cannot be read: each refusal names the input at fault, in the log and in the step's failure reason.
    [Theory]
    [InlineData("coverage", "42", "Cible de jugement d'analyse invalide « coverage »")]
    [InlineData("security", null, "La variable AETHEUS_RUN_ID est absente")]
    [InlineData("security", "abc", "La variable AETHEUS_RUN_ID vaut « abc »")]
    [InlineData("security", "0", "La variable AETHEUS_RUN_ID vaut « 0 »")]
    public async Task ExecuteAsync_InvalidParameters_NameTheInputAtFault(string target, string? runId, string expected)
    {
        var env = new Dictionary<string, string>();
        if (runId is not null) env["AETHEUS_RUN_ID"] = runId;
        var lines = new List<string>();

        var result = await new AnalysisGateOperationExecutor(Substitute.For<IServerApiClient>()).ExecuteAsync(
            OperationKind.PipelineEvaluateAnalysisGate, target, env,
            60, (line, _) => { lines.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("ToolError", result.FailureCode);
        Assert.Contains(expected, result.FailureReason, StringComparison.Ordinal);
        Assert.Contains(lines, line => line.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_AGateTheServerDoesNotAnswer_NamesTheRunAndTheScope()
    {
        var api = Substitute.For<IServerApiClient>();
        api.GetAnalysisRunGateAsync(42, "security", Arg.Any<CancellationToken>()).Returns((AnalysisRunGateDto?)null);
        var lines = new List<string>();

        var result = await new AnalysisGateOperationExecutor(api).ExecuteAsync(
            OperationKind.PipelineEvaluateAnalysisGate, "security",
            new Dictionary<string, string> { ["AETHEUS_RUN_ID"] = "42" },
            60, (line, _) => { lines.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("verdict d'analyse security du run 42 est indisponible", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains(lines, line => line.Contains("du run 42", StringComparison.Ordinal));
    }

    // Recette R-21, threshold branch: a Blocked verdict logged only counts. Each crossed rule is now a line with its
    // observed value and its limit, and a grade under its minimum says so.
    [Fact]
    public async Task ExecuteAsync_ABlockedVerdict_NamesEachCrossedRuleWithItsObservedValueAndItsLimit()
    {
        var (result, lines) = await RunGateAsync(new AnalysisRunGateDto
        {
            PipelineRunId = 42,
            Status = AnalysisGateStatus.Blocked,
            Grade = new AnalysisGradeSummaryDto
            {
                OverallGrade = AnalysisGrade.D,
                MinimumGrade = AnalysisGrade.B,
                LimitingDomain = AnalysisGradeDomain.CodeQuality
            },
            Violations =
            [
                new AnalysisRunGateViolationDto
                {
                    ReportId = 7,
                    PolicyKey = "max-critical",
                    PolicyName = "Aucun finding critique",
                    Outcome = "Block",
                    TargetKind = "finding",
                    TargetKey = "CVE-2026-1",
                    ObservedValue = "3",
                    Operator = ">",
                    Threshold = "0",
                    Count = 3
                },
                new AnalysisRunGateViolationDto { ReportId = 8, PolicyKey = "warn-medium", Outcome = "Warn", ObservedValue = "12", Operator = ">", Threshold = "10" }
            ]
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(lines, line => line == "Règle violée Aucun finding critique (blocage) : valeur observée 3, condition > 0, "
            + "cible finding CVE-2026-1, rapport #7, 3 occurrences.");
        Assert.Contains(lines, line => line == "Règle violée warn-medium (avertissement) : valeur observée 12, condition > 10, rapport #8.");
        Assert.Contains(lines, line => line == "Note D sous le minimum exigé B, domaine limitant CodeQuality.");
    }

    [Fact]
    public async Task ExecuteAsync_AGradeAtItsMinimum_SaysNothingAboutTheMinimum()
    {
        var (_, lines) = await RunGateAsync(new AnalysisRunGateDto
        {
            PipelineRunId = 42,
            Status = AnalysisGateStatus.Passed,
            Grade = new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.B, MinimumGrade = AnalysisGrade.B }
        });

        Assert.DoesNotContain(lines, line => line.Contains("sous le minimum", StringComparison.Ordinal));
    }

    // The gate drops, without a word, a stored snapshot that is not valid JSON: it is now named, and it changes no verdict.
    [Fact]
    public async Task ExecuteAsync_UnreadableEvidence_IsNamedWithItsProducerAndItsReport_AndChangesNoVerdict()
    {
        var (result, lines) = await RunGateAsync(new AnalysisRunGateDto
        {
            PipelineRunId = 42,
            Status = AnalysisGateStatus.Passed,
            UnreadableEvidence =
            [
                new AnalysisGateUnreadableEvidenceDto
                {
                    ReportId = 5, ScannerKey = "trivy-dependencies", StepName = "Scan dependencies",
                    Evidence = "grade snapshot", Detail = "'{' is invalid after a property name."
                }
            ]
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(lines, line => line == "Preuve illisible, ignorée du verdict : producteur trivy-dependencies, "
            + "étape « Scan dependencies », rapport #5, grade snapshot : '{' is invalid after a property name.");
    }

    private static async Task<(ExecutorResult Result, List<string> Lines)> RunGateAsync(AnalysisRunGateDto gate)
    {
        var api = Substitute.For<IServerApiClient>();
        api.GetAnalysisRunGateAsync(42, "security", Arg.Any<CancellationToken>()).Returns(gate);
        api.UploadArtifactAsync(42, Arg.Any<string>(), "Gate", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifactDto { Id = 1 });
        var lines = new List<string>();
        var result = await new AnalysisGateOperationExecutor(api).ExecuteAsync(
            OperationKind.PipelineEvaluateAnalysisGate, "security",
            new Dictionary<string, string> { ["AETHEUS_RUN_ID"] = "42", ["AETHEUS_STAGE_NAME"] = "Gate" },
            60, (line, _) => { lines.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        return (result, lines);
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
