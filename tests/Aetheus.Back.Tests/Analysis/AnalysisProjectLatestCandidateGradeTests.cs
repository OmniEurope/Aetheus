// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>Use the latest candidate root and triggered descendants, merging each domain
/// independently.</summary>
public sealed class AnalysisProjectLatestCandidateGradeTests : IDisposable
{
    private const int ProjectId = 5;
    private readonly AppDbContext _db;
    private readonly DateTime _now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private int _nextId = 1;

    public AnalysisProjectLatestCandidateGradeTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _db.Projects.Add(new Project { Id = ProjectId, Name = "Aetheus", Description = "d", DefaultBranch = "develop" });
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheLatestCandidate_GivesTheGrade_TheWorstOfItsTree_EvenWhenANewerNightlyGradedBetter()
    {
        // A newer nightly grade must not replace a measured domain from the sealed candidate tree.
        Run(2478, _now.AddHours(-3), sealedCandidate: true);
        Run(2479, _now.AddHours(-3).AddMinutes(1), parentRunId: 2478);
        Run(2490, _now.AddHours(-1));
        Evaluate(2478, AnalysisGrade.B, AnalysisGradeDomain.CodeQuality, _now.AddHours(-2));
        Evaluate(2479, AnalysisGrade.D, AnalysisGradeDomain.Security, _now.AddHours(-2).AddMinutes(5));
        Evaluate(2490, AnalysisGrade.A, AnalysisGradeDomain.Security, _now.AddMinutes(-30));
        await _db.SaveChangesAsync(Ct);

        var grade = (await new AnalysisProjectSummaryRepository(_db).GetGradesAsync([ProjectId], Ct))[ProjectId];

        Assert.Equal(AnalysisGrade.D, grade.OverallGrade);
        // The run to open is the candidate's root, which shows its children merged.
        Assert.Equal(2478, grade.PipelineRunId);
    }

    [Fact]
    public async Task WithoutAnyCandidate_TheNewestGradedRunTree_GivesTheGrade()
    {
        Run(300, _now.AddHours(-3));
        Run(301, _now.AddHours(-1));
        Run(302, _now.AddHours(-1).AddMinutes(1), parentRunId: 301);
        Evaluate(300, AnalysisGrade.A, AnalysisGradeDomain.Security, _now.AddHours(-2));
        Evaluate(301, AnalysisGrade.A, AnalysisGradeDomain.CodeQuality, _now.AddMinutes(-50));
        Evaluate(302, AnalysisGrade.C, AnalysisGradeDomain.Security, _now.AddMinutes(-40));
        await _db.SaveChangesAsync(Ct);

        var grade = (await new AnalysisProjectSummaryRepository(_db).GetGradesAsync([ProjectId], Ct))[ProjectId];

        Assert.Equal(AnalysisGrade.C, grade.OverallGrade);
        Assert.Equal(301, grade.PipelineRunId);
    }

    [Fact]
    public async Task AProjectNeverGraded_HasNoGrade()
    {
        Run(400, _now);
        await _db.SaveChangesAsync(Ct);

        Assert.Empty(await new AnalysisProjectSummaryRepository(_db).GetGradesAsync([ProjectId], Ct));
    }

    [Fact]
    public async Task TheCandidateTree_ShowsTheSecurityDomainOfItsSecurityRun_WhenItsQualityRunTiesOnTheOverallGrade()
    {
        // Keep security grades from the security child even when a tied quality child has an
        // incomplete security domain.
        Run(600, _now.AddHours(-2), sealedCandidate: true);
        Run(601, _now.AddHours(-2).AddMinutes(1), parentRunId: 600);
        Run(602, _now.AddHours(-2).AddMinutes(2), parentRunId: 600);
        EvaluateDomains(601, _now.AddHours(-1),
            Measured(AnalysisGradeDomain.CodeQuality, AnalysisGrade.B), Unmeasured(AnalysisGradeDomain.Security));
        EvaluateDomains(602, _now.AddHours(-1).AddMinutes(1),
            Measured(AnalysisGradeDomain.Security, AnalysisGrade.B), Unmeasured(AnalysisGradeDomain.CodeQuality));
        await _db.SaveChangesAsync(Ct);

        var grade = (await new AnalysisProjectSummaryRepository(_db).GetGradesAsync([ProjectId], Ct))[ProjectId];

        var security = grade.Domains.Single(domain => domain.Domain == AnalysisGradeDomain.Security);
        Assert.Equal(AnalysisGrade.B, security.Grade);
        Assert.True(security.Required);
        Assert.Equal(AnalysisGradeCompleteness.Complete, security.Completeness);
        Assert.NotEmpty(security.Measures);
        var codeQuality = grade.Domains.Single(domain => domain.Domain == AnalysisGradeDomain.CodeQuality);
        Assert.Equal(AnalysisGrade.B, codeQuality.Grade);
        Assert.Equal(AnalysisGradeCompleteness.Complete, grade.Completeness);
        Assert.Equal(600, grade.PipelineRunId);
    }

    [Fact]
    public async Task TheNightlyTree_MergesItsRunsDomainByDomain_KeepingTheWorstMeasuredSecurityGrade()
    {
        // A nightly (no candidate seal, so the newest tree) triggers quality, security, security history,
        // QA and extended QA. Security is the worst of the four runs that measured it; quality comes from
        // the quality run, and the extended QA run that could not measure security does not blank it.
        Run(700, _now.AddHours(-4));
        Run(701, _now.AddHours(-4).AddMinutes(1), parentRunId: 700);
        Run(702, _now.AddHours(-4).AddMinutes(2), parentRunId: 700);
        Run(703, _now.AddHours(-4).AddMinutes(3), parentRunId: 700);
        Run(704, _now.AddHours(-4).AddMinutes(4), parentRunId: 700);
        Run(705, _now.AddHours(-4).AddMinutes(5), parentRunId: 700);
        EvaluateDomains(701, _now.AddHours(-3),
            Measured(AnalysisGradeDomain.CodeQuality, AnalysisGrade.A), Unmeasured(AnalysisGradeDomain.Security));
        EvaluateDomains(702, _now.AddHours(-3), Measured(AnalysisGradeDomain.Security, AnalysisGrade.C));
        EvaluateDomains(703, _now.AddHours(-3), Measured(AnalysisGradeDomain.Security, AnalysisGrade.D));
        EvaluateDomains(704, _now.AddHours(-3), Measured(AnalysisGradeDomain.Security, AnalysisGrade.B));
        EvaluateDomains(705, _now.AddHours(-3), Unmeasured(AnalysisGradeDomain.Security, required: true));
        await _db.SaveChangesAsync(Ct);

        var grade = (await new AnalysisProjectSummaryRepository(_db).GetGradesAsync([ProjectId], Ct))[ProjectId];

        Assert.Equal(700, grade.PipelineRunId);
        Assert.Equal(AnalysisGrade.D, grade.Domains.Single(domain => domain.Domain == AnalysisGradeDomain.Security).Grade);
        Assert.Equal(AnalysisGrade.A, grade.Domains.Single(domain => domain.Domain == AnalysisGradeDomain.CodeQuality).Grade);
        Assert.Equal(AnalysisGrade.D, grade.OverallGrade);
        Assert.Equal(AnalysisGradeDomain.Security, grade.LimitingDomain);
    }

    private void Run(int runId, DateTime startedAt, bool sealedCandidate = false, int? parentRunId = null)
    {
        _db.PipelineRuns.Add(new PipelineRun { Id = runId, PipelineId = 1, Status = PipelineStatus.Success, StartedAt = startedAt });
        if (sealedCandidate)
            _db.PipelineStepRuns.Add(new PipelineStepRun
            {
                Id = _nextId++,
                PipelineRunId = runId,
                StageName = "Seal",
                StepName = "assurance",
                Status = TaskExecutionStatus.Success,
                OutputVariablesJson = "{\"CANDIDATE_DEPLOYABLE\":\"false\",\"CANDIDATE_ASSURANCE_GRADE\":\"D\"}"
            });
        if (parentRunId is { } parent)
            _db.PipelineStepRuns.Add(new PipelineStepRun
            {
                Id = _nextId++,
                PipelineRunId = parent,
                StageName = "CI",
                StepName = "trigger",
                Status = TaskExecutionStatus.Success,
                TriggeredRunId = runId
            });
    }

    private void Evaluate(int runId, AnalysisGrade grade, AnalysisGradeDomain domain, DateTime evaluatedAt) =>
        EvaluateDomains(runId, evaluatedAt, Measured(domain, grade));

    /// <summary>One evaluation of the run, its snapshot holding the given domains as the engine writes them.</summary>
    private void EvaluateDomains(int runId, DateTime evaluatedAt, params AnalysisGradeDomainDto[] domains)
    {
        var required = domains.Where(domain => domain.Required).ToList();
        var complete = required.Count > 0 && required.All(domain => domain.Grade.HasValue);
        var grade = complete ? required.Max(domain => domain.Grade) : null;
        var snapshot = new AnalysisGradeSummaryDto
        {
            EvaluatedAt = evaluatedAt,
            OverallGrade = grade,
            Completeness = complete ? AnalysisGradeCompleteness.Complete : AnalysisGradeCompleteness.Incomplete,
            Domains = [.. domains.Select(domain => domain with { EvaluatedAt = evaluatedAt })]
        };
        var id = _nextId++;
        _db.AnalysisEvaluations.Add(new AnalysisEvaluation
        {
            Id = id,
            ProjectId = ProjectId,
            AnalysisReportId = id,
            PipelineRunId = runId,
            Status = AnalysisGateStatus.Passed,
            Grade = grade,
            GradeCompleteness = snapshot.Completeness,
            GradeSnapshotJson = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            EvaluatedAt = evaluatedAt,
            CreatedAt = evaluatedAt
        });
    }

    private static AnalysisGradeDomainDto Measured(AnalysisGradeDomain domain, AnalysisGrade grade)
    {
        var observed = grade switch
        {
            AnalysisGrade.A => 100,
            AnalysisGrade.B => 85,
            AnalysisGrade.C => 75,
            AnalysisGrade.D => 65,
            AnalysisGrade.E => 55,
            _ => 45
        };
        return new AnalysisGradeDomainDto
        {
            Domain = domain,
            Grade = grade,
            Required = true,
            Completeness = AnalysisGradeCompleteness.Complete,
            Measures =
            [
                new AnalysisGradeMeasureDto
                {
                    Key = domain.ToString(),
                    Domain = domain,
                    Grade = grade,
                    Required = true,
                    Observed = true,
                    ObservedValue = observed,
                    Direction = AnalysisMetricDirection.HigherIsBetter,
                    AThreshold = 90,
                    BThreshold = 80,
                    CThreshold = 70,
                    DThreshold = 60,
                    EThreshold = 50
                }
            ]
        };
    }

    // A domain the run's grading does not measure: advisory (or required but without evidence), no grade.
    private static AnalysisGradeDomainDto Unmeasured(AnalysisGradeDomain domain, bool required = false) => new()
    {
        Domain = domain,
        Required = required,
        Completeness = AnalysisGradeCompleteness.Incomplete
    };
}
