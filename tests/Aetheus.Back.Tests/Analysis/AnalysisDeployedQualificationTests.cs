// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>R-12, R-20: the overall grade of a project is the sealed verdict of the release deployed in production,
/// never the analysis grade of the latest candidate or nightly; the analysis domains stay as measured.</summary>
public sealed class AnalysisDeployedQualificationTests : IDisposable
{
    private const int ProjectId = 5;
    private readonly AppDbContext _db;
    private readonly DateTime _now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private int _nextId = 1;

    public AnalysisDeployedQualificationTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _db.Projects.Add(new Project { Id = ProjectId, Name = "Aetheus", Description = "d", DefaultBranch = "develop" });
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<AnalysisGradeSummaryDto?> GradeAsync()
    {
        await _db.SaveChangesAsync(Ct);
        return (await new AnalysisProjectSummaryRepository(_db).GetGradesAsync([ProjectId], Ct)).GetValueOrDefault(ProjectId);
    }

    [Fact]
    public async Task ADeployedRelease_SealedNotDeployable_ShowsF_EvenWhenEveryAnalysisDomainIsA()
    {
        // The R-20 case: the analysis domains of the candidate are all A, but the seal refused the release
        // because required tests failed. The letter shown must never be A.
        Run(3092, _now.AddHours(-3), buildNumber: 339);
        Evaluate(3092, AnalysisGrade.A, AnalysisGradeDomain.Security, _now.AddHours(-2));
        Evaluate(3092, AnalysisGrade.A, AnalysisGradeDomain.CodeQuality, _now.AddHours(-2));
        Release("1.5.30", ReleaseStatus.Deployed, runId: 3092, AnalysisGrade.A, deployable: false, blocking: 2, commit: "76660953aaaa");

        var grade = await GradeAsync();

        Assert.NotNull(grade);
        Assert.Equal(AnalysisGrade.F, grade.OverallGrade);
        var qualification = grade.Qualification;
        Assert.NotNull(qualification);
        Assert.Equal(AnalysisQualificationState.DeployedNotDeployable, qualification.State);
        Assert.Equal("1.5.30", qualification.ReleaseVersion);
        Assert.Equal(3092, qualification.PipelineRunId);
        Assert.Equal(339, qualification.PipelineRunNumber);
        Assert.Equal(2, qualification.BlockingTestCount);
        Assert.Equal("76660953aaaa", grade.CommitHash);
        // The domain grades stay visible as measured: they are not rewritten to F.
        Assert.Equal(AnalysisGrade.A, DomainGrade(grade, AnalysisGradeDomain.Security));
        Assert.Equal(AnalysisGrade.A, DomainGrade(grade, AnalysisGradeDomain.CodeQuality));
        Assert.DoesNotContain(grade.Domains, domain => domain.Grade == AnalysisGrade.F);
        Assert.Null(grade.LimitingDomain);
    }

    [Fact]
    public async Task ADeployedRelease_SealedBWithFlakyTests_ShowsB_WhileTheDomainsStayA()
    {
        Run(3100, _now.AddHours(-3), buildNumber: 340);
        Evaluate(3100, AnalysisGrade.A, AnalysisGradeDomain.Security, _now.AddHours(-2));
        Release("1.5.31", ReleaseStatus.Deployed, runId: 3100, AnalysisGrade.B, deployable: true, blocking: 0);

        var grade = await GradeAsync();

        Assert.NotNull(grade);
        Assert.Equal(AnalysisGrade.B, grade.OverallGrade);
        Assert.Equal(AnalysisQualificationState.DeployedSealed, grade.Qualification!.State);
        Assert.Equal(AnalysisGrade.A, DomainGrade(grade, AnalysisGradeDomain.Security));
    }

    [Fact]
    public async Task WithoutADeployedRelease_TheGradeIsAnExplicitState_NotAnA()
    {
        // Analysis A everywhere, a sealed candidate published but never deployed: no letter, an explicit state.
        Run(3100, _now.AddHours(-3), buildNumber: 340);
        Evaluate(3100, AnalysisGrade.A, AnalysisGradeDomain.Security, _now.AddHours(-2));
        Release("1.5.31", ReleaseStatus.Published, runId: 3100, AnalysisGrade.A, deployable: true, blocking: 0);

        var grade = await GradeAsync();

        Assert.NotNull(grade);
        Assert.Null(grade.OverallGrade);
        Assert.Equal(AnalysisQualificationState.NothingDeployed, grade.Qualification!.State);
        // The domain grades remain visible as such.
        Assert.Equal(AnalysisGrade.A, DomainGrade(grade, AnalysisGradeDomain.Security));
    }

    [Fact]
    public async Task ADeployedReleaseWithoutASealedVerdict_ShowsNoLetter_AndSaysSo()
    {
        Run(3100, _now.AddHours(-3), buildNumber: 340);
        Evaluate(3100, AnalysisGrade.A, AnalysisGradeDomain.Security, _now.AddHours(-2));
        Release("1.4.0", ReleaseStatus.Deployed, runId: 3100, assuranceGrade: null, deployable: null, blocking: null);

        var grade = await GradeAsync();

        Assert.NotNull(grade);
        Assert.Null(grade.OverallGrade);
        Assert.Equal(AnalysisQualificationState.DeployedWithoutVerdict, grade.Qualification!.State);
        Assert.Equal("1.4.0", grade.Qualification.ReleaseVersion);
    }

    [Fact]
    public async Task ADeployedReleaseWithoutAVerdict_ShowsTheNewestEarlierVerdict_MarkedOldWithItsSource()
    {
        // Option B: nothing is sealed on the deployed 1.6.0, the newest known verdict (F, not deployable) is shown,
        // marked old with the release, run and date it comes from; an older B is not the one.
        Run(3000, _now.AddHours(-30), buildNumber: 300);
        Run(3092, _now.AddHours(-10), buildNumber: 339, series: "1.5", number: 30);
        Run(3100, _now.AddHours(-3), buildNumber: 340);
        Evaluate(3100, AnalysisGrade.A, AnalysisGradeDomain.Security, _now.AddHours(-2));
        Release("1.5.29", ReleaseStatus.Superseded, runId: 3000, AnalysisGrade.B, deployable: true, blocking: 0);
        Release("0a0a0a", ReleaseStatus.Superseded, runId: 3092, AnalysisGrade.A, deployable: false, blocking: 2, commit: "76660953aaaa");
        Release("1.6.0", ReleaseStatus.Deployed, runId: 3100, assuranceGrade: null, deployable: null, blocking: null);

        var grade = await GradeAsync();

        Assert.NotNull(grade);
        Assert.Equal(AnalysisGrade.F, grade.OverallGrade);
        var qualification = grade.Qualification;
        Assert.NotNull(qualification);
        Assert.Equal(AnalysisQualificationState.DeployedLastKnownGrade, qualification.State);
        Assert.Equal("1.6.0", qualification.ReleaseVersion);
        Assert.Equal("1.5.30", qualification.KnownReleaseVersion);
        Assert.NotNull(qualification.KnownAt);
        Assert.Equal(3092, qualification.PipelineRunId);
        Assert.Equal(339, qualification.PipelineRunNumber);
        Assert.Equal(2, qualification.BlockingTestCount);
        Assert.Equal(3092, grade.PipelineRunId);
        Assert.Equal("76660953aaaa", grade.CommitHash);
        // The domains stay the analysis as measured, never rewritten to the old letter.
        Assert.Equal(AnalysisGrade.A, DomainGrade(grade, AnalysisGradeDomain.Security));
    }

    [Fact]
    public async Task ASealedDeployedRelease_IsShownAsIs_NeverReplacedByAnEarlierVerdict()
    {
        Run(3000, _now.AddHours(-10), buildNumber: 300);
        Run(3100, _now.AddHours(-3), buildNumber: 340);
        Release("1.5.29", ReleaseStatus.Superseded, runId: 3000, AnalysisGrade.F, deployable: false, blocking: 1);
        Release("1.5.31", ReleaseStatus.Deployed, runId: 3100, AnalysisGrade.B, deployable: true, blocking: 0);

        var grade = await GradeAsync();

        Assert.Equal(AnalysisGrade.B, grade!.OverallGrade);
        Assert.Equal(AnalysisQualificationState.DeployedSealed, grade.Qualification!.State);
        Assert.Null(grade.Qualification.KnownReleaseVersion);
    }

    [Fact]
    public async Task ADeployedReleaseWithoutAVerdict_AndNoEarlierVerdict_ShowsNoGrade_NotAnInventedOne()
    {
        // The only other release was never sealed either: nothing is known, so no letter at all.
        Run(3000, _now.AddHours(-10), buildNumber: 300);
        Run(3100, _now.AddHours(-3), buildNumber: 340);
        Evaluate(3100, AnalysisGrade.A, AnalysisGradeDomain.Security, _now.AddHours(-2));
        Release("1.3.0", ReleaseStatus.Superseded, runId: 3000, assuranceGrade: null, deployable: null, blocking: null);
        Release("1.4.0", ReleaseStatus.Deployed, runId: 3100, assuranceGrade: null, deployable: null, blocking: null);

        var grade = await GradeAsync();

        Assert.NotNull(grade);
        Assert.Null(grade.OverallGrade);
        Assert.Equal(AnalysisQualificationState.DeployedWithoutVerdict, grade.Qualification!.State);
        Assert.Null(grade.Qualification.KnownReleaseVersion);
    }

    [Fact]
    public async Task ANewerRejectedCandidate_NeverReplacesTheDeployedReleaseVerdict()
    {
        // The deployed release (B, sealed deployable) is older than a candidate sealed F that was never deployed.
        Run(3000, _now.AddHours(-10), buildNumber: 300, sealedCandidate: true);
        Evaluate(3000, AnalysisGrade.B, AnalysisGradeDomain.Security, _now.AddHours(-9));
        Release("1.5.29", ReleaseStatus.Deployed, runId: 3000, AnalysisGrade.B, deployable: true, blocking: 0);
        Run(3092, _now.AddHours(-3), buildNumber: 339, sealedCandidate: true);
        Evaluate(3092, AnalysisGrade.A, AnalysisGradeDomain.Security, _now.AddHours(-2));
        Release("1.5.30", ReleaseStatus.Published, runId: 3092, AnalysisGrade.F, deployable: false, blocking: 3);

        var grade = await GradeAsync();

        Assert.NotNull(grade);
        Assert.Equal(AnalysisGrade.B, grade.OverallGrade);
        Assert.Equal("1.5.29", grade.Qualification!.ReleaseVersion);
        // The domains and the run to open are the deployed release's, not the newer candidate's.
        Assert.Equal(3000, grade.PipelineRunId);
        Assert.Equal(AnalysisGrade.B, DomainGrade(grade, AnalysisGradeDomain.Security));
    }

    [Fact]
    public async Task ASupersededRelease_IsNotTheDeployedOne()
    {
        Run(3000, _now.AddHours(-10), buildNumber: 300);
        Run(3100, _now.AddHours(-3), buildNumber: 340);
        Evaluate(3100, AnalysisGrade.A, AnalysisGradeDomain.Security, _now.AddHours(-2));
        Release("1.5.29", ReleaseStatus.Superseded, runId: 3000, AnalysisGrade.F, deployable: false, blocking: 1);
        Release("1.5.31", ReleaseStatus.Deployed, runId: 3100, AnalysisGrade.A, deployable: true, blocking: 0);

        var grade = await GradeAsync();

        Assert.Equal(AnalysisGrade.A, grade!.OverallGrade);
        Assert.Equal("1.5.31", grade.Qualification!.ReleaseVersion);
    }

    [Fact]
    public async Task ADeployedReleaseSealedF_WithoutAnyAnalysisGrade_StillShowsF()
    {
        Run(3092, _now.AddHours(-3), buildNumber: 339);
        Release("1.5.30", ReleaseStatus.Deployed, runId: 3092, AnalysisGrade.F, deployable: false, blocking: 2);

        var grade = await GradeAsync();

        Assert.NotNull(grade);
        Assert.Equal(AnalysisGrade.F, grade.OverallGrade);
        Assert.DoesNotContain(grade.Domains, domain => domain.Grade.HasValue);
    }

    [Fact]
    public async Task TheProjectList_TheProjectDetail_AndTheQualityPage_ShowTheSameGrade_ForEveryProjectOfTheList()
    {
        // R-12, R-20 (recette R-4, R-6): the list shows a batch of projects at once, the Quality page one project.
        // A rejected deployed release (F) next to another project deployed A must give F in all three reads, and A
        // for the other project, whatever the batch holds. Real grade reader, real data: no double.
        const int otherProjectId = 6;
        _db.Projects.Add(new Project { Id = otherProjectId, Name = "Other", Description = "d", DefaultBranch = "develop" });
        Run(3092, _now.AddHours(-3), buildNumber: 339);
        Evaluate(3092, AnalysisGrade.A, AnalysisGradeDomain.Security, _now.AddHours(-2));
        Release("1.5.30", ReleaseStatus.Deployed, runId: 3092, AnalysisGrade.A, deployable: false, blocking: 2);
        _db.Releases.Add(new Release
        {
            Id = _nextId++,
            ProjectId = otherProjectId,
            Version = "2.0.0",
            Status = ReleaseStatus.Deployed,
            AssuranceGrade = AnalysisGrade.A,
            Deployable = true,
            PublishedAt = _now
        });
        await _db.SaveChangesAsync(Ct);
        var repository = new Aetheus.Back.Components.Projects.ProjectRepository(_db, new ProjectAnalysisGradeRepository(_db));

        var list = await repository.GetProjectListInsightsAsync([ProjectId, otherProjectId], _now.AddMinutes(-5), Ct);
        foreach (var projectId in new[] { ProjectId, otherProjectId })
        {
            var detail = await repository.GetLatestGateGradeAsync(projectId, Ct);
            var quality = (await new AnalysisProjectSummaryRepository(_db).GetAsync(projectId, Ct)).Grade;

            Assert.Equal(quality?.OverallGrade, list[projectId].LatestGateGrade);
            Assert.Equal(quality?.OverallGrade, detail?.OverallGrade);
            Assert.Equal(quality?.Qualification?.State, list[projectId].GradeQualification?.State);
        }
        Assert.Equal(AnalysisGrade.F, list[ProjectId].LatestGateGrade);
        Assert.Equal(AnalysisGrade.A, list[otherProjectId].LatestGateGrade);
    }

    [Fact]
    public async Task AProjectNeverGradedAndNeverDeployed_HasNoGradeAtAll()
    {
        Assert.Null(await GradeAsync());
    }

    [Fact]
    public async Task TheApplicationVersion_IsTheOneTheDeployedReleaseRunAllocated()
    {
        Run(3092, _now.AddHours(-3), buildNumber: 339, series: "1.5", number: 30);
        Release("0f0f0f", ReleaseStatus.Deployed, runId: 3092, AnalysisGrade.B, deployable: true, blocking: 0);

        var grade = await GradeAsync();

        Assert.Equal("1.5.30", grade!.Qualification!.ReleaseVersion);
    }

    // R-7: the project grade carries a general Findings line from the open findings, counted in the overall letter.
    [Theory]
    [InlineData(AnalysisSeverity.Critical, AnalysisGrade.F)]
    [InlineData(AnalysisSeverity.High, AnalysisGrade.D)]
    [InlineData(AnalysisSeverity.Medium, AnalysisGrade.B)]
    public async Task AnOpenFinding_LowersTheOverallGrade_AndShowsTheFindingsLine(AnalysisSeverity severity, AnalysisGrade expected)
    {
        Run(3100, _now.AddHours(-3), buildNumber: 340);
        Evaluate(3100, AnalysisGrade.A, AnalysisGradeDomain.Security, _now.AddHours(-2));
        Release("1.5.31", ReleaseStatus.Deployed, runId: 3100, AnalysisGrade.A, deployable: true, blocking: 0);
        Finding(1, severity, AnalysisFindingStatus.Open);

        var grade = await GradeAsync();

        Assert.Equal(expected, grade!.OverallGrade);
        Assert.Equal(expected, grade.Findings!.Grade);
        Assert.Equal(1, AnalysisFindingScore.Total(grade.Findings.Counts));
        // The Qualification still names the sealed release; only the letter follows the findings.
        Assert.Equal(AnalysisQualificationState.DeployedSealed, grade.Qualification!.State);
        Assert.Null(grade.LimitingDomain);
    }

    [Fact]
    public async Task OnlyOpenFindings_CountInTheFindingsLine_AndNoFindingIsAnA()
    {
        Run(3100, _now.AddHours(-3), buildNumber: 340);
        Evaluate(3100, AnalysisGrade.A, AnalysisGradeDomain.Security, _now.AddHours(-2));
        Release("1.5.31", ReleaseStatus.Deployed, runId: 3100, AnalysisGrade.A, deployable: true, blocking: 0);
        Finding(1, AnalysisSeverity.Critical, AnalysisFindingStatus.Accepted);
        Finding(2, AnalysisSeverity.High, AnalysisFindingStatus.Fixed);

        var grade = await GradeAsync();

        Assert.Equal(AnalysisGrade.A, grade!.OverallGrade);
        Assert.Equal(AnalysisGrade.A, grade.Findings!.Grade);
        Assert.Equal(0, AnalysisFindingScore.Total(grade.Findings.Counts));
    }

    [Fact]
    public async Task TheFindingsLine_NeverRaisesTheOverallGrade()
    {
        Run(3092, _now.AddHours(-3), buildNumber: 339);
        Release("1.5.30", ReleaseStatus.Deployed, runId: 3092, AnalysisGrade.A, deployable: false, blocking: 2);
        Evaluate(3092, AnalysisGrade.A, AnalysisGradeDomain.Security, _now.AddHours(-2));
        Finding(1, AnalysisSeverity.Low, AnalysisFindingStatus.Open);

        var grade = await GradeAsync();

        Assert.Equal(AnalysisGrade.F, grade!.OverallGrade);
        Assert.Equal(AnalysisGrade.B, grade.Findings!.Grade);
    }

    // User decision 2026-11-10: a grade below A names what lowers it; an A names nothing.
    [Fact]
    public async Task ASealedBelowA_NamesThePipelineRun_AsTheCause()
    {
        Run(3100, _now.AddHours(-3), buildNumber: 340);
        Release("1.5.31", ReleaseStatus.Deployed, runId: 3100, AnalysisGrade.B, deployable: true, blocking: 0);

        var grade = await GradeAsync();

        Assert.Equal(AnalysisGrade.B, grade!.OverallGrade);
        Assert.Equal(AnalysisGradeCause.Pipeline, grade.Cause);
        Assert.Equal(3100, grade.Qualification!.PipelineRunId);
    }

    [Fact]
    public async Task ASealRefusal_NamesThePipelineRun_AsTheCause_EvenWithAFindingLessSevere()
    {
        Run(3092, _now.AddHours(-3), buildNumber: 339);
        Release("1.5.30", ReleaseStatus.Deployed, runId: 3092, AnalysisGrade.A, deployable: false, blocking: 2);
        Finding(1, AnalysisSeverity.Low, AnalysisFindingStatus.Open);

        var grade = await GradeAsync();

        Assert.Equal(AnalysisGrade.F, grade!.OverallGrade);
        Assert.Equal(AnalysisGradeCause.Pipeline, grade.Cause);
    }

    [Fact]
    public async Task OpenFindingsWorseThanTheSeal_NameTheProject_AsTheCause()
    {
        Run(3100, _now.AddHours(-3), buildNumber: 340);
        Release("1.5.31", ReleaseStatus.Deployed, runId: 3100, AnalysisGrade.A, deployable: true, blocking: 0);
        Finding(1, AnalysisSeverity.High, AnalysisFindingStatus.Open);

        var grade = await GradeAsync();

        Assert.Equal(AnalysisGrade.D, grade!.OverallGrade);
        Assert.Equal(AnalysisGradeCause.Project, grade.Cause);
    }

    [Fact]
    public async Task OpenFindingsAsBadAsTheSeal_DoNotTakeTheCauseAway_FromThePipelineRun()
    {
        Run(3100, _now.AddHours(-3), buildNumber: 340);
        Release("1.5.31", ReleaseStatus.Deployed, runId: 3100, AnalysisGrade.B, deployable: true, blocking: 0);
        Finding(1, AnalysisSeverity.Medium, AnalysisFindingStatus.Open);

        var grade = await GradeAsync();

        Assert.Equal(AnalysisGrade.B, grade!.OverallGrade);
        Assert.Equal(AnalysisGradeCause.Pipeline, grade.Cause);
    }

    [Fact]
    public async Task AnAGrade_AndAGradeWithoutLetter_NameNoCause()
    {
        Run(3100, _now.AddHours(-3), buildNumber: 340);
        Release("1.5.31", ReleaseStatus.Deployed, runId: 3100, AnalysisGrade.A, deployable: true, blocking: 0);
        Finding(1, AnalysisSeverity.Critical, AnalysisFindingStatus.Accepted);

        var grade = await GradeAsync();

        Assert.Equal(AnalysisGrade.A, grade!.OverallGrade);
        Assert.Null(grade.Cause);
    }

    [Fact]
    public async Task WithoutADeployedRelease_NoCauseIsNamed()
    {
        Run(3100, _now.AddHours(-3), buildNumber: 340);
        Evaluate(3100, AnalysisGrade.A, AnalysisGradeDomain.Security, _now.AddHours(-2));
        Release("1.5.31", ReleaseStatus.Published, runId: 3100, AnalysisGrade.A, deployable: true, blocking: 0);
        Finding(1, AnalysisSeverity.Critical, AnalysisFindingStatus.Open);

        var grade = await GradeAsync();

        Assert.Null(grade!.OverallGrade);
        Assert.Null(grade.Cause);
    }

    // The reported case (production, project 1): findings closed as Mitigated must give the same, current grade to the
    // menu (the portfolio summary), the project overview (the project detail) and the Quality page (the summary).
    [Fact]
    public async Task AfterTheOpenFindingsAreMitigated_TheMenuTheOverviewAndTheQualityPageAgree_OnA()
    {
        Run(3100, _now.AddHours(-3), buildNumber: 345);
        Release("1.6.5", ReleaseStatus.Deployed, runId: 3100, AnalysisGrade.A, deployable: true, blocking: 0);
        Finding(1, AnalysisSeverity.Medium, AnalysisFindingStatus.Open);
        // The portfolio (menu) query joins the project's organization.
        var organization = new Organization { Id = 1, Name = "Org", Slug = "org" };
        _db.Organizations.Add(organization);
        _db.Projects.Local.Single(project => project.Id == ProjectId).Organization = organization;
        await _db.SaveChangesAsync(Ct);
        var repository = new Aetheus.Back.Components.Projects.ProjectRepository(_db, new ProjectAnalysisGradeRepository(_db));

        async Task<(AnalysisGrade? Menu, AnalysisGrade? Overview, AnalysisGrade? Quality)> ReadAllAsync() => (
            (await new AnalysisPortfolioProjectRepository(_db).GetAsync([ProjectId], Ct)).Single().Grade?.OverallGrade,
            (await repository.GetLatestGateGradeAsync(ProjectId, Ct))?.OverallGrade,
            (await new AnalysisProjectSummaryRepository(_db).GetAsync(ProjectId, Ct)).Grade?.OverallGrade);

        var before = await ReadAllAsync();
        Assert.Equal((AnalysisGrade.B, AnalysisGrade.B, AnalysisGrade.B), before);

        _db.AnalysisFindings.Single(finding => finding.Id == 1).Status = AnalysisFindingStatus.Mitigated;
        await _db.SaveChangesAsync(Ct);

        Assert.Equal((AnalysisGrade.A, AnalysisGrade.A, AnalysisGrade.A), await ReadAllAsync());
    }

    [Fact]
    public async Task TheRunPipeline_IsReadByTheProjectRepository_ForTheCause()
    {
        _db.Pipelines.Add(new Pipeline { Id = 1, Name = "Candidate", ProjectId = ProjectId, YamlDefinition = "yaml" });
        Run(3100, _now.AddHours(-3), buildNumber: 345);
        await _db.SaveChangesAsync(Ct);
        var repository = new Aetheus.Back.Components.Projects.ProjectRepository(_db, new ProjectAnalysisGradeRepository(_db));

        var pipeline = await repository.GetRunPipelineAsync(3100, Ct);

        Assert.Equal(new Aetheus.Back.Components.Projects.RunPipelineRef(1, "Candidate"), pipeline);
        Assert.Null(await repository.GetRunPipelineAsync(999, Ct));
    }

    private void Finding(int id, AnalysisSeverity severity, AnalysisFindingStatus status) =>
        _db.AnalysisFindings.Add(new AnalysisFinding
        {
            Id = id,
            OrganizationId = 1,
            ProjectId = ProjectId,
            Fingerprint = $"fp-{id}",
            RuleId = "rule",
            Category = AnalysisCategory.Dependencies,
            Severity = severity,
            Status = status,
            Title = "finding",
            Message = "message"
        });

    // The engine lists every domain; the tests read the ones they graded.
    private static AnalysisGrade? DomainGrade(AnalysisGradeSummaryDto grade, AnalysisGradeDomain domain) =>
        grade.Domains.Single(candidate => candidate.Domain == domain).Grade;

    private void Run(int runId, DateTime startedAt, int buildNumber, bool sealedCandidate = false, string? series = null, int? number = null)
    {
        _db.PipelineRuns.Add(new PipelineRun
        {
            Id = runId,
            PipelineId = 1,
            Status = PipelineStatus.Success,
            StartedAt = startedAt,
            BuildNumber = buildNumber,
            ReleaseSeries = series,
            ReleaseNumber = number
        });
        if (sealedCandidate)
            _db.PipelineStepRuns.Add(new PipelineStepRun
            {
                Id = _nextId++,
                PipelineRunId = runId,
                StageName = "Seal",
                StepName = "assurance",
                Status = TaskExecutionStatus.Success,
                OutputVariablesJson = "{\"CANDIDATE_DEPLOYABLE\":\"true\"}"
            });
    }

    private void Release(
        string version, ReleaseStatus status, int runId, AnalysisGrade? assuranceGrade, bool? deployable, int? blocking,
        string? commit = null) =>
        _db.Releases.Add(new Release
        {
            Id = _nextId++,
            ProjectId = ProjectId,
            Version = version,
            Status = status,
            PipelineRunId = runId,
            CreatedByPipelineRunId = runId,
            CommitHash = commit,
            PublishedAt = _now.AddMinutes(_nextId),
            AssuranceGrade = assuranceGrade,
            Deployable = deployable,
            BlockingTestCount = blocking
        });

    private void Evaluate(int runId, AnalysisGrade grade, AnalysisGradeDomain domain, DateTime evaluatedAt)
    {
        var snapshot = new AnalysisGradeSummaryDto
        {
            EvaluatedAt = evaluatedAt,
            OverallGrade = grade,
            Completeness = AnalysisGradeCompleteness.Complete,
            Domains =
            [
                new AnalysisGradeDomainDto
                {
                    Domain = domain,
                    Grade = grade,
                    Required = true,
                    Completeness = AnalysisGradeCompleteness.Complete,
                    EvaluatedAt = evaluatedAt,
                    Measures =
                    [
                        new AnalysisGradeMeasureDto
                        {
                            Key = domain.ToString(),
                            Domain = domain,
                            Grade = grade,
                            Required = true,
                            Observed = true,
                            ObservedValue = grade == AnalysisGrade.A ? 100 : 85,
                            Direction = AnalysisMetricDirection.HigherIsBetter,
                            AThreshold = 90,
                            BThreshold = 80,
                            CThreshold = 70,
                            DThreshold = 60,
                            EThreshold = 50
                        }
                    ]
                }
            ]
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
            GradeCompleteness = AnalysisGradeCompleteness.Complete,
            GradeSnapshotJson = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            EvaluatedAt = evaluatedAt,
            CreatedAt = evaluatedAt
        });
    }
}
