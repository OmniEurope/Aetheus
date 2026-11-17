// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-8: what a person reads for a run comes out of the SQL projections, which InMemory does not translate.
/// The technical key and the number are deliberately far apart, so a projection that returned the key
/// instead of the number would fail here.
/// </summary>
public sealed class PipelineRunNumberProjectionIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private const int Number = 12;

    private async Task<(int PipelineId, int RunId)> SeedAsync(CancellationToken ct)
    {
        await using var db = NewContext();
        var organization = new Organization { Name = "Numbers", Slug = $"numbers-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = "Project", OrganizationId = organization.Id, DefaultBranch = "main" };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        var pipeline = new Pipeline { Name = "numbered", ProjectId = project.Id, YamlDefinition = "name: numbered" };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);
        var run = new PipelineRun
        {
            Id = 400_000,
            PipelineId = pipeline.Id,
            BuildNumber = Number,
            Status = PipelineStatus.WaitingForApproval,
            StartedAt = DateTime.UtcNow
        };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return (pipeline.Id, run.Id);
    }

    [Fact]
    public async Task TheRunList_CarriesTheNumber()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var (_, runId) = await SeedAsync(ct);

        await using var db = NewContext();
        var row = await db.PipelineRuns.AsNoTracking()
            .Where(run => run.Id == runId)
            .Select(PipelineRunListRows.Projection)
            .SingleAsync(ct);

        Assert.Equal(runId, row.Id);
        Assert.Equal(Number, row.BuildNumber);
    }

    [Fact]
    public async Task TheRunList_CarriesThePublicIdentifier()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var (_, runId) = await SeedAsync(ct);

        await using var db = NewContext();
        var stored = await db.PipelineRuns.AsNoTracking().Where(run => run.Id == runId).Select(run => run.Uid).SingleAsync(ct);
        var row = await db.PipelineRuns.AsNoTracking()
            .Where(run => run.Id == runId)
            .Select(PipelineRunListRows.Projection)
            .SingleAsync(ct);

        Assert.NotNull(stored);
        Assert.Equal(stored, row.Uid);
    }

    [Fact]
    public async Task ThePendingApprovals_CarryTheNumber()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var (_, runId) = await SeedAsync(ct);
        await using var db = NewContext();
        db.PipelineApprovals.Add(new PipelineApproval
        {
            PipelineRunId = runId,
            StageName = "Deploy",
            Status = ApprovalStatus.Pending,
            RequestedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);

        var approval = Assert.Single(await new PipelineApprovalRepository(db).GetPendingApprovalsAsync(null, ct));

        Assert.Equal(runId, approval.PipelineRunId);
        Assert.Equal(Number, approval.PipelineRunNumber);
    }

    [Fact]
    public async Task TheCoverageTrend_CarriesTheNumber()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var (_, runId) = await SeedAsync(ct);
        await using var db = NewContext();
        db.CoverageResults.Add(new CoverageResult { PipelineRunId = runId, LineRate = 0.8, BranchRate = 0.7, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);

        var point = Assert.Single(await new PipelineCoverageRepository(db).GetCoverageTrendAsync(runId, 5, ct));

        Assert.Equal(runId, point.RunId);
        Assert.Equal(Number, point.RunNumber);
    }

    [Fact]
    public async Task AFollowUpRun_ReadsThePipelineNumberOfTheRunThatStartedIt()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var (pipelineId, parentRunId) = await SeedAsync(ct);
        await using var db = NewContext();
        var child = new PipelineRun
        {
            PipelineId = pipelineId,
            BuildNumber = Number + 1,
            StartedAt = DateTime.UtcNow,
            AdditionalVariablesJson = $$"""{"UPSTREAM_RUN_ID":"{{parentRunId}}"}"""
        };
        db.PipelineRuns.Add(child);
        await db.SaveChangesAsync(ct);

        var upstream = await new PipelineRunLineageRepository(db).GetUpstreamBuildNumberAsync(child.Id, ct);

        Assert.Equal(Number, upstream);
    }

    [Fact]
    public async Task TheRunApprovals_AreReadWithoutTheirRun_AndTheNumberComesFromTheNumberQuery()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var (_, runId) = await SeedAsync(ct);
        await using var db = NewContext();
        db.PipelineApprovals.Add(new PipelineApproval
        {
            PipelineRunId = runId,
            StageName = "Deploy",
            Status = ApprovalStatus.Pending,
            RequestedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        var repository = new PipelineApprovalRepository(db);

        var approvals = await repository.GetApprovalsAsync(runId, ct);
        // Only the read path runs: GetApprovalsAsync reads the approvals and, for a pending one, the release the run
        // restores (R-34) through the real lookup reader; the other collaborators stay unused.
        var service = new PipelineApprovalService(
            repository, new PipelineRunLookupRepository(db), null!, null!, null!, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineApprovalService>.Instance, TimeProvider.System);
        var dto = Assert.Single(await service.GetApprovalsAsync(runId, ct));

        Assert.Null(Assert.Single(approvals).PipelineRun);
        Assert.Equal(Number, await repository.GetRunNumberAsync(runId, ct));
        Assert.Equal(runId, dto.PipelineRunId);
        Assert.Equal(Number, dto.PipelineRunNumber);
    }

    [Fact]
    public async Task TheMigrationGateVerdicts_AreReadWithoutTheirRun_AndTheDtoStillCarriesTheNumber()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var (_, runId) = await SeedAsync(ct);
        await using var db = NewContext();
        db.PipelineMigrationGateVerdicts.Add(new PipelineMigrationGateVerdict
        {
            PipelineRunId = runId,
            StageName = "Deploy",
            StepName = "Migrate",
            Outcome = MigrationGateOutcome.Compatible,
            MigrationsJson = "[]",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        var repository = new PipelineMigrationGateRepository(db);

        var verdicts = await repository.ListForRunAsync(runId, ct);
        var dto = Assert.Single(await new PipelineMigrationGateService(repository).GetForRunAsync(runId, ct));

        Assert.Null(Assert.Single(verdicts).PipelineRun);
        Assert.Equal(runId, dto.RunId);
        Assert.Equal(Number, dto.RunNumber);
    }

    [Fact]
    public async Task TheProjectTrends_CarryThePipelineName_BesideTheNumber()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var (_, runId) = await SeedAsync(ct);
        await using var db = NewContext();
        var projectId = await db.Pipelines.Where(p => p.Name == "numbered").Select(p => p.ProjectId!.Value).SingleAsync(ct);
        db.CoverageResults.Add(new CoverageResult { PipelineRunId = runId, LineRate = 0.8, BranchRate = 0.7, CreatedAt = DateTime.UtcNow });
        db.TestResults.Add(new Aetheus.Back.Data.Entities.TestResult { PipelineRunId = runId, Outcome = TestOutcome.Passed, TestName = "t", CreatedAt = DateTime.UtcNow });
        db.RunMetrics.AddRange(
            new RunMetric { PipelineRunId = runId, Key = "complexity.cyclomatic.avg", Value = 3, CreatedAt = DateTime.UtcNow },
            new RunMetric { PipelineRunId = runId, Key = "complexity.cyclomatic.max", Value = 9, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
        var repository = new PipelineCoverageRepository(db);

        var coverage = Assert.Single(await repository.GetProjectCoverageTrendAsync(projectId, 5, ct));
        var tests = Assert.Single(await repository.GetProjectTestTrendAsync(projectId, 5, ct));
        var complexity = Assert.Single(await repository.GetProjectComplexityTrendAsync(projectId, 5, ct));

        Assert.All(new[] { (coverage.RunNumber, coverage.PipelineName), (tests.RunNumber, tests.PipelineName), (complexity.RunNumber, complexity.PipelineName) },
            point => Assert.Equal((Number, "numbered"), point));
    }

    [Fact]
    public async Task TheNumberLookup_AnswersForEveryRunOfAPage()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var (_, runId) = await SeedAsync(ct);
        await using var db = NewContext();

        var numbers = await PipelineRunNumbers.ReadAsync(db, [runId, runId, 999_999_999], ct);

        Assert.Equal(new Dictionary<int, int> { [runId] = Number }, numbers);
    }
}
