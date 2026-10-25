// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// End-to-end proof that a run with no <c>AnalysisEvaluation</c> of its own still surfaces a
/// <c>GateGrade</c> on the run LIST (<see cref="PipelineRepository.GetRunsPagedAsync"/>), the same
/// path the pipeline run grid and the dashboard use. Covers the three roll-up sources
/// <see cref="PipelineRunGradeAggregation"/> resolves: a candidate's own sealed assurance letter, a
/// trigger orchestrator's children, and a deploy run's restored candidate release.
/// </summary>
public class PipelineRunGradeAggregationTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly PipelineRepository _repo;

    public PipelineRunGradeAggregationTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new PipelineRepository(
            _db,
            TimeProvider.System,
            NullLogger<PipelineRepository>.Instance,
            new PipelineTaskLifecycleRepository(_db, TimeProvider.System),
            new PipelineRunLineageRepository(_db));
    }

    public void Dispose() => _db.Dispose();

    private Pipeline AddPipeline(string name)
    {
        var pipeline = new Pipeline { Name = name, YamlDefinition = "y" };
        _db.Pipelines.Add(pipeline);
        _db.SaveChanges();
        return pipeline;
    }

    private PipelineRun AddRun(int pipelineId, string? parametersJson = null)
    {
        var run = new PipelineRun
        {
            PipelineId = pipelineId,
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow,
            ParametersJson = parametersJson ?? "{}"
        };
        _db.PipelineRuns.Add(run);
        _db.SaveChanges();
        return run;
    }

    [Fact]
    public async Task GetRunsPagedAsync_CandidateWithNoEvaluation_SurfacesSealedAssuranceGrade()
    {
        // The candidate pipeline has no analysis_grading block of its own: it delegates every
        // analysis to a child pipeline and publishes its verdict via CANDIDATE_ASSURANCE_GRADE
        // (deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/generate-candidate-assurance-contract.mjs).
        var pipeline = AddPipeline("candidate");
        var run = AddRun(pipeline.Id);
        _db.PipelineStepRuns.Add(new PipelineStepRun
        {
            PipelineRunId = run.Id,
            StageName = "Seal",
            StepName = "seal-contract",
            Status = TaskExecutionStatus.Success,
            OutputVariablesJson = """{"CANDIDATE_ASSURANCE_GRADE":"F"}"""
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetRunsPagedAsync(
            pipeline.Id, 1, 20, ct: TestContext.Current.CancellationToken);

        var dto = Assert.Single(items);
        Assert.Equal(AnalysisGrade.F, dto.GateGrade);
    }

    [Fact]
    public async Task GetRunsPagedAsync_TriggerOrchestrator_RollsUpWorstChildGrade()
    {
        // The orchestrator (e.g. nightly) has no gate of its own; its own trigger step launched two
        // child runs, one graded B and one graded D. F=5..A=0, so the worse (higher) grade D wins.
        var orchestratorPipeline = AddPipeline("nightly");
        var childPipeline = AddPipeline("nightly-child");
        var orchestratorRun = AddRun(orchestratorPipeline.Id);
        var childRunGoodGrade = AddRun(childPipeline.Id);
        var childRunWorseGrade = AddRun(childPipeline.Id);

        _db.AnalysisEvaluations.AddRange(
            new AnalysisEvaluation
            {
                PipelineRunId = childRunGoodGrade.Id,
                Grade = AnalysisGrade.B,
                EvaluatedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            },
            new AnalysisEvaluation
            {
                PipelineRunId = childRunWorseGrade.Id,
                Grade = AnalysisGrade.D,
                EvaluatedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun
            {
                PipelineRunId = orchestratorRun.Id,
                StageName = "Fan-out",
                StepName = "trigger-good",
                Status = TaskExecutionStatus.Success,
                TriggeredRunId = childRunGoodGrade.Id
            },
            new PipelineStepRun
            {
                PipelineRunId = orchestratorRun.Id,
                StageName = "Fan-out",
                StepName = "trigger-worse",
                Status = TaskExecutionStatus.Success,
                TriggeredRunId = childRunWorseGrade.Id
            });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetRunsPagedAsync(
            orchestratorPipeline.Id, 1, 20, ct: TestContext.Current.CancellationToken);

        var dto = Assert.Single(items);
        Assert.Equal(AnalysisGrade.D, dto.GateGrade);
    }

    [Fact]
    public async Task GetRunsPagedAsync_DeployRun_ResolvesGradeFromRestoredCandidateRelease()
    {
        // The deploy pipeline itself has no analysis_grading block; it restores a published,
        // immutable candidate release identified by the candidateVersion run parameter.
        var candidatePipeline = AddPipeline("candidate");
        var candidateRun = AddRun(candidatePipeline.Id);
        _db.AnalysisEvaluations.Add(new AnalysisEvaluation
        {
            PipelineRunId = candidateRun.Id,
            Grade = AnalysisGrade.C,
            EvaluatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        var project = new Project { Name = "proj" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.Releases.Add(new Release
        {
            ProjectId = project.Id,
            Version = "1.2.3",
            PipelineRunId = candidateRun.Id,
            DetectedAt = DateTime.UtcNow
        });
        var deployPipeline = AddPipeline("deploy-prod");
        deployPipeline.ProjectId = project.Id;
        var deployRun = AddRun(deployPipeline.Id, parametersJson: """{"candidateVersion":"1.2.3"}""");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetRunsPagedAsync(
            deployPipeline.Id, 1, 20, ct: TestContext.Current.CancellationToken);

        var dto = Assert.Single(items);
        Assert.Equal(AnalysisGrade.C, dto.GateGrade);
        _ = deployRun; // seeded for context; not asserted on directly
    }

    [Fact]
    public async Task R1_30_HydrateLinkedGradeAsync_DeployRunDetail_ReadsTheDeployedCandidateGrade()
    {
        // The run page (detail path, not the list) shows the grade in its status tile: the API must give
        // a deploy run the grade of the candidate release it restored (run 2819 case).
        var project = new Project { Name = "proj" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var deployPipeline = AddPipeline("deploy-prod");
        deployPipeline.ProjectId = project.Id;
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.Releases.Add(new Release
        {
            ProjectId = project.Id,
            Version = "c-2819",
            AssuranceGrade = AnalysisGrade.A,
            DetectedAt = DateTime.UtcNow
        });
        var deployRun = AddRun(deployPipeline.Id, parametersJson: """{"candidateVersion":"c-2819"}""");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var detail = new PipelineRunDto { Id = deployRun.Id, PipelineId = deployPipeline.Id, ProjectId = project.Id };
        var hydrated = await _repo.HydrateLinkedGradeAsync(detail, TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGrade.A, hydrated.GateGrade);
    }

    [Fact]
    public async Task GetRunsPagedAsync_DeployRunThatRewroteItsRelease_ReadsTheReleaseSealedGrade()
    {
        // Runs 2325, 2327 and 2334: the deploy's `type: release` step with `deployed: true` rewrote
        // Release.PipelineRunId to the deploy run itself, so following it found no grade. The release
        // still carries the grade its candidate sealed, and that is what the deploy run must show.
        var project = new Project { Name = "proj" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var deployPipeline = AddPipeline("deploy-prod");
        deployPipeline.ProjectId = project.Id;
        var deployRun = AddRun(deployPipeline.Id, parametersJson: """{"candidateVersion":"c-96"}""");
        _db.Releases.Add(new Release
        {
            ProjectId = project.Id,
            Version = "c-96",
            PipelineRunId = deployRun.Id,
            AssuranceGrade = AnalysisGrade.C,
            DetectedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetRunsPagedAsync(
            deployPipeline.Id, 1, 20, ct: TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGrade.C, Assert.Single(items).GateGrade);
    }

    [Fact]
    public async Task GetRunsPagedAsync_DeployRunWhoseReleaseHasNoGradeAndPointsToItself_StaysUngraded()
    {
        // A release-fast release carries no sealed grade: the deploy must not borrow one, and must not
        // resolve its own run as if it were the candidate.
        var project = new Project { Name = "proj" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var deployPipeline = AddPipeline("deploy-prod");
        deployPipeline.ProjectId = project.Id;
        var deployRun = AddRun(deployPipeline.Id, parametersJson: """{"candidateVersion":"f-1"}""");
        _db.Releases.Add(new Release
        {
            ProjectId = project.Id,
            Version = "f-1",
            PipelineRunId = deployRun.Id,
            DetectedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetRunsPagedAsync(
            deployPipeline.Id, 1, 20, ct: TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(items).GateGrade);
    }

    [Fact]
    public async Task GetRunsPagedAsync_RunWithOwnEvaluation_IsNotOverriddenByAggregation()
    {
        var pipeline = AddPipeline("direct");
        var run = AddRun(pipeline.Id);
        _db.AnalysisEvaluations.Add(new AnalysisEvaluation
        {
            PipelineRunId = run.Id,
            Grade = AnalysisGrade.A,
            EvaluatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetRunsPagedAsync(
            pipeline.Id, 1, 20, ct: TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGrade.A, Assert.Single(items).GateGrade);
    }
}
