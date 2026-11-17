// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>Recette R-21: the nightly's security child (run 3100) ended "Error - 9 rapports, 0 findings, 0 blocages,
/// note A" and nothing said which producer or report was refused, or why. A verdict in Error now carries one cause
/// per failing report or missing producer (the tool, the step, the report and its file, the precise reason).</summary>
public sealed class AnalysisRunGateErrorCausesTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);
    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private int _nextReportId;

    public AnalysisRunGateErrorCausesTests()
    {
        _db.Organizations.Add(new Organization { Id = 1, Name = "Aetheus", Slug = "aetheus" });
        _db.Projects.Add(new Project { Id = 1, Name = "Project", OrganizationId = 1, DefaultBranch = "main" });
        _db.Pipelines.Add(new Pipeline { Id = 1, Name = "aetheus-security", ProjectId = 1 });
        _db.PipelineRuns.Add(new PipelineRun { Id = 3100, PipelineId = 1, Status = PipelineStatus.Failed, BranchName = "main", CommitHash = new string('a', 40) });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private void SeedScannerTask(
        int id,
        string scanner,
        string stepName,
        TaskExecutionStatus status = TaskExecutionStatus.Pending,
        int? exitCode = null,
        string? failureCode = null,
        string? failureReason = null,
        params (TaskLogLevel Level, string Message)[] logs)
    {
        var task = new ServerTask
        {
            Id = id,
            ServerId = 1,
            PipelineRunId = 3100,
            Operation = OperationKind.PipelineRunScanner,
            Command = scanner,
            Name = stepName,
            Status = status,
            ExitCode = exitCode,
            FailureCode = failureCode,
            FailureReason = failureReason
        };
        _db.Tasks.Add(task);
        foreach (var (level, message) in logs)
            _db.TaskLogs.Add(new TaskLog { TaskId = id, Level = level, Message = message, Timestamp = Now });
        _db.SaveChanges();
    }

    /// <summary>A report of the run with its stored evaluation (null: stored with none).</summary>
    private int SeedReport(
        string scanner,
        string stepName,
        AnalysisReportStatus status = AnalysisReportStatus.Passed,
        AnalysisGateStatus? evaluation = AnalysisGateStatus.Passed,
        int? artifactId = null,
        string? reportPath = null,
        string? errorMessage = null)
    {
        var id = ++_nextReportId;
        _db.AnalysisReports.Add(new AnalysisReport
        {
            Id = id,
            OrganizationId = 1,
            ProjectId = 1,
            PipelineRunId = 3100,
            PipelineArtifactId = artifactId,
            ScannerKey = scanner,
            ScannerName = scanner.ToUpperInvariant(),
            ScannerVersion = "1.0.0",
            Category = AnalysisCategory.Dependencies,
            Status = status,
            Format = AnalysisReportFormat.Sarif,
            ReportPath = reportPath,
            ErrorMessage = errorMessage,
            StageName = "Scan source",
            StepName = stepName,
            ContentHash = id.ToString("D64", System.Globalization.CultureInfo.InvariantCulture),
            StartedAt = Now.AddMinutes(-1),
            CompletedAt = Now,
            CreatedAt = Now
        });
        if (evaluation is { } gate)
        {
            _db.AnalysisEvaluations.Add(new AnalysisEvaluation
            {
                Id = id,
                OrganizationId = 1,
                ProjectId = 1,
                AnalysisReportId = id,
                PipelineRunId = 3100,
                Status = gate,
                BlockerCount = gate == AnalysisGateStatus.Error ? 1 : 0,
                EvaluatedAt = Now,
                CreatedAt = Now
            });
        }
        _db.SaveChanges();
        return id;
    }

    private Task<AnalysisRunGateDto> GateAsync() =>
        new AnalysisRepository(_db).GetRunGateAsync(3100, AnalysisGateScopes.Security, TestContext.Current.CancellationToken);

    [Fact]
    public async Task AReportTheToolEndedInError_IsAnErrorWithNoFinding_ThatNamesTheToolTheReportAndTheReason()
    {
        SeedScannerTask(900, "gitleaks", "Scan secrets");
        SeedScannerTask(901, "trivy-dependencies", "Scan dependencies");
        SeedReport("gitleaks", "Scan secrets");
        var failed = SeedReport(
            "trivy-dependencies", "Scan dependencies", AnalysisReportStatus.TimedOut, AnalysisGateStatus.Error,
            artifactId: 7, reportPath: ".analysis/trivy-dependencies.json", errorMessage: "Scanner timed out after 1200 s.");

        var gate = await GateAsync();

        // The shape of run 3100: an Error with reports and no finding...
        Assert.Equal(AnalysisGateStatus.Error, gate.Status);
        Assert.Equal(0, gate.FindingCount);
        // ...and now the cause, which names the producer, the evidence and the reason.
        var cause = Assert.Single(gate.ErrorCauses);
        Assert.Equal(AnalysisGateErrorCauseKind.ReportTimedOut, cause.Kind);
        Assert.Equal("trivy-dependencies", cause.ScannerKey);
        Assert.Equal("Scan dependencies", cause.StepName);
        Assert.Equal(failed, cause.ReportId);
        Assert.Equal(7, cause.PipelineArtifactId);
        Assert.Equal(".analysis/trivy-dependencies.json", cause.ReportPath);
        Assert.Equal("Scanner timed out after 1200 s.", cause.Detail);
        Assert.Equal(3100, cause.PipelineRunId);
    }

    [Fact]
    public async Task ATaskThatPublishedNoReport_IsAnErrorThatNamesItsToolAndItsStep()
    {
        SeedScannerTask(900, "gitleaks", "Scan secrets");
        SeedScannerTask(901, "trivy-dependencies", "Scan dependencies");
        SeedReport("gitleaks", "Scan secrets");

        var gate = await GateAsync();

        Assert.Equal(AnalysisGateStatus.Error, gate.Status);
        var cause = Assert.Single(gate.ErrorCauses);
        Assert.Equal(AnalysisGateErrorCauseKind.MissingProducer, cause.Kind);
        Assert.Equal("trivy-dependencies", cause.ScannerKey);
        Assert.Equal("Scan dependencies", cause.StepName);
        Assert.Contains(gate.MissingProducers, item => item.StartsWith("trivy-dependencies:task:901", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReportStoredWithoutEvaluation_IsAnErrorThatSaysItsVerdictIsUnknown()
    {
        SeedScannerTask(900, "gitleaks", "Scan secrets");
        var report = SeedReport("gitleaks", "Scan secrets", evaluation: null);

        var gate = await GateAsync();

        Assert.Equal(AnalysisGateStatus.Error, gate.Status);
        var cause = Assert.Single(gate.ErrorCauses);
        Assert.Equal(AnalysisGateErrorCauseKind.NoEvaluation, cause.Kind);
        Assert.Equal(report, cause.ReportId);
        Assert.Equal("gitleaks", cause.ScannerKey);
    }

    [Fact]
    public async Task ARunWithNoReportAtAll_IsAnErrorThatSaysNothingWasPublished()
    {
        var gate = await GateAsync();

        Assert.Equal(AnalysisGateStatus.Error, gate.Status);
        Assert.Equal(AnalysisGateErrorCauseKind.NoReport, Assert.Single(gate.ErrorCauses).Kind);
    }

    [Fact]
    public async Task AGateThatIsNotInError_HasNoCause()
    {
        SeedScannerTask(900, "gitleaks", "Scan secrets");
        SeedReport("gitleaks", "Scan secrets");

        var gate = await GateAsync();

        Assert.NotEqual(AnalysisGateStatus.Error, gate.Status);
        Assert.Empty(gate.ErrorCauses);
    }

    // The gate keeps its rule: any failing report makes it an Error, even when a later report of the same step (the retry
    // of retry_count) succeeded. Recette R-21 only names the cause; it relaxes nothing.
    [Fact]
    public async Task AFailedReportFollowedByAGoodRetry_StillMakesTheGateAnErrorAndNamesTheFailedReport()
    {
        SeedScannerTask(900, "trivy-dependencies", "Scan dependencies");
        var failed = SeedReport("trivy-dependencies", "Scan dependencies", AnalysisReportStatus.Unavailable, AnalysisGateStatus.Error,
            errorMessage: "Scanner runtime download failed because of a transient network error.");
        SeedReport("trivy-dependencies", "Scan dependencies");

        var gate = await GateAsync();

        Assert.Equal(AnalysisGateStatus.Error, gate.Status);
        var cause = Assert.Single(gate.ErrorCauses);
        Assert.Equal(AnalysisGateErrorCauseKind.ReportUnavailable, cause.Kind);
        Assert.Equal(failed, cause.ReportId);
        Assert.Equal("Scan dependencies", cause.StepName);
        Assert.Equal("Scanner runtime download failed because of a transient network error.", cause.Detail);
    }

    [Fact]
    public async Task ARetryThatFailedToo_NamesBothFailedReports()
    {
        SeedScannerTask(900, "trivy-dependencies", "Scan dependencies");
        SeedReport("trivy-dependencies", "Scan dependencies", AnalysisReportStatus.Unavailable, AnalysisGateStatus.Error,
            errorMessage: "first attempt");
        var last = SeedReport("trivy-dependencies", "Scan dependencies", AnalysisReportStatus.Error, AnalysisGateStatus.Error,
            errorMessage: "second attempt");

        var gate = await GateAsync();

        Assert.Equal(AnalysisGateStatus.Error, gate.Status);
        Assert.Equal(2, gate.ErrorCauses.Count);
        Assert.Contains(gate.ErrorCauses, cause => cause.ReportId == last && cause.Detail == "second attempt");
    }

    // The other way a gate reads "Error" with grade A and no finding: the SBOM's Dependency-Track upload is required and
    // not done when the gate is read. Only its token in MissingProducers said so, and no screen or log showed it.
    [Theory]
    [InlineData("Pending", "pending")]
    [InlineData("Failed", "failed")]
    public async Task ARequiredDependencyTrackUploadNotDone_IsAnErrorThatSaysItsState(string outbox, string expected)
    {
        var repository = Substitute.For<IAnalysisRepository>();
        repository.GetDependencyTrackGateStateAsync(3100, Arg.Any<CancellationToken>())
            .Returns(new DependencyTrackGateState(true, [outbox]));
        var gate = new AnalysisRunGateDto
        {
            PipelineRunId = 3100,
            Status = AnalysisGateStatus.Passed,
            ReportCount = 9,
            Grade = new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.A }
        };

        var result = await DependencyTrackGateVerdict.ApplyAsync(
            gate, 3100, AnalysisGateScopes.Security, new DependencyTrackOptions { Enabled = true, Required = true }, repository,
            TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Error, result.Status);
        var cause = Assert.Single(result.ErrorCauses);
        Assert.Equal(AnalysisGateErrorCauseKind.DependencyTrack, cause.Kind);
        Assert.Equal(expected, cause.Detail);
    }

    [Fact]
    public async Task AGoodReportOfAnotherStep_DoesNotCoverAFailedOne()
    {
        SeedScannerTask(900, "trivy-dependencies", "Scan dependencies");
        SeedScannerTask(901, "trivy-dependencies", "Scan images");
        SeedReport("trivy-dependencies", "Scan dependencies", AnalysisReportStatus.Error, AnalysisGateStatus.Error);
        SeedReport("trivy-dependencies", "Scan images");

        var gate = await GateAsync();

        Assert.Equal(AnalysisGateStatus.Error, gate.Status);
        Assert.Equal("Scan dependencies", Assert.Single(gate.ErrorCauses).StepName);
    }

    // Recette R-21, the missing-producer branch: "the task published no report" named no reason. The cause now says what
    // became of the task that was to publish it: its end, its failure and its last error lines (the coverage that is empty,
    // the commit that does not match the workspace), and not the generic incident line that only repeats the exit code.
    [Fact]
    public async Task AProducerWhoseTaskFailed_IsNamedWithTheTaskEndAndItsLastErrorLines()
    {
        SeedScannerTask(
            900, "gitleaks", "Scan secrets", TaskExecutionStatus.Failed, exitCode: 1,
            failureCode: "ToolError", failureReason: "Coverage source SHA does not match the checked-out workspace.",
            logs:
            [
                (TaskLogLevel.Info, "Publishing coverage"),
                (TaskLogLevel.Error, "Coverage source SHA does not match the checked-out workspace."),
                (TaskLogLevel.Error, "[incident:ToolError] Pipeline step 'Scan/Scan secrets' finished with status Failed and exit code 1.")
            ]);

        var gate = await GateAsync();

        var cause = Assert.Single(gate.ErrorCauses, item => item.Kind == AnalysisGateErrorCauseKind.MissingProducer);
        Assert.Equal(
            "tâche #900 en échec, code de sortie 1, échec ToolError : Coverage source SHA does not match the checked-out workspace"
            + ". Dernières erreurs de son journal : Coverage source SHA does not match the checked-out workspace.",
            cause.Detail);
        Assert.DoesNotContain("incident", cause.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Success, "terminée avec succès, mais sans publier de rapport")]
    [InlineData(TaskExecutionStatus.Cancelled, "annulée")]
    [InlineData(TaskExecutionStatus.Pending, "jamais démarrée (en attente)")]
    [InlineData(TaskExecutionStatus.Timeout, "arrêtée par son délai")]
    public async Task AProducerWhoseTaskPublishedNothing_SaysHowTheTaskEnded(TaskExecutionStatus status, string expected)
    {
        SeedScannerTask(900, "gitleaks", "Scan secrets", status);

        var gate = await GateAsync();

        var cause = Assert.Single(gate.ErrorCauses, item => item.Kind == AnalysisGateErrorCauseKind.MissingProducer);
        Assert.Equal($"tâche #900 {expected}", cause.Detail);
    }

    // Grade branch: "no measure could be graded" now lists the required rules nobody observed with the step that should
    // have produced them.
    [Fact]
    public void AGradeThatMeasuredNothing_NamesTheRulesNobodyObservedAndTheirStep()
    {
        var grade = new AnalysisGradeSummaryDto
        {
            Completeness = AnalysisGradeCompleteness.Incomplete,
            Domains =
            [
                new AnalysisGradeDomainDto
                {
                    Domain = AnalysisGradeDomain.CodeQuality,
                    Required = true,
                    Measures =
                    [
                        new AnalysisGradeMeasureDto { Key = "grade.coverage", Required = true, Observed = false, ProducerStep = "Publish coverage" },
                        new AnalysisGradeMeasureDto { Key = "grade.untied", Required = true, Observed = false },
                        new AnalysisGradeMeasureDto { Key = "grade.optional", Required = false, Observed = false }
                    ]
                }
            ]
        };

        var causes = AnalysisGateVerdict.ErrorCauses(
            3100, [new AnalysisRunGateReportDto { ReportId = 1, GateStatus = AnalysisGateStatus.Passed }], [], grade);

        var cause = Assert.Single(causes);
        Assert.Equal(AnalysisGateErrorCauseKind.GradeNotComputed, cause.Kind);
        Assert.Equal("mesures non observées : règle grade.coverage (étape « Publish coverage »), règle grade.untied", cause.Detail);
    }

    // Evidence branch: a snapshot that is not valid JSON was dropped from the verdict and the grade in silence. It is named
    // with its producer and its report; the verdict is the one it was.
    [Fact]
    public async Task ASnapshotThatIsNotValidJson_IsNamedWithItsProducer_AndChangesNoVerdict()
    {
        SeedScannerTask(900, "gitleaks", "Scan secrets");
        SeedReport("gitleaks", "Scan secrets");
        var evaluation = _db.AnalysisEvaluations.Single();
        evaluation.PolicySnapshotJson = "{not json";
        evaluation.GradeSnapshotJson = "[1,";
        _db.SaveChanges();

        var gate = await GateAsync();

        Assert.Equal(AnalysisGateStatus.Passed, gate.Status);
        Assert.Empty(gate.ErrorCauses);
        Assert.Equal(2, gate.UnreadableEvidence.Count);
        Assert.All(gate.UnreadableEvidence, item =>
        {
            Assert.Equal(1, item.ReportId);
            Assert.Equal("gitleaks", item.ScannerKey);
            Assert.Equal("Scan secrets", item.StepName);
            Assert.False(string.IsNullOrWhiteSpace(item.Detail));
        });
        Assert.Contains(gate.UnreadableEvidence, item => item.Evidence == "policy snapshot");
        Assert.Contains(gate.UnreadableEvidence, item => item.Evidence == "grade snapshot");
    }

    [Fact]
    public async Task ReadableSnapshots_AreNotReportedAsUnreadable()
    {
        SeedScannerTask(900, "gitleaks", "Scan secrets");
        SeedReport("gitleaks", "Scan secrets");

        var gate = await GateAsync();

        Assert.Empty(gate.UnreadableEvidence);
    }
}
