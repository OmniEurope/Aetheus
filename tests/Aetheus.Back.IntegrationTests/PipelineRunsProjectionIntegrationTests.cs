// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Proves the paged-runs LIST projection (<see cref="PipelineRunHelpers.RunListProjection"/>, used by
/// <c>GetRunsPagedAsync</c>) actually TRANSLATES to SQL on real PostgreSQL and returns correct data.
/// The unit suite only mocks the repository, so a projection that fails to translate (the nested
/// <c>StepRuns.OrderBy().Select().ToList()</c> collection projection over a Skip/Take page, or the
/// navigation ternaries) would 500 the runs endpoint in production while every unit test stays green.
/// This is the InMemory-blind layer: EF InMemory client-evaluates anything, so it never surfaces a
/// non-translatable Expression - only a real Npgsql query does.
/// </summary>
public sealed class PipelineRunsProjectionIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    [Fact]
    public async Task GetRunsPagedAsync_TranslatesToSql_AndProjectsRunPlusStepSummary()
    {
        await ResetAndMigrateAsync();

        int pipelineId;
        int newerRunId;
        await using (var db = NewContext())
        {
            var org = new Organization { Name = $"org-{Guid.NewGuid():N}", Slug = $"s{Guid.NewGuid():N}"[..12], Description = "proj-test" };
            db.Set<Organization>().Add(org);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var project = new Project { Name = $"proj-{Guid.NewGuid():N}", RepositoryUrl = "https://git/x.git", OrganizationId = org.Id };
            db.Projects.Add(project);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

            var pipeline = new Pipeline { Name = $"pl-{Guid.NewGuid():N}", YamlDefinition = "name: x", ProjectId = project.Id };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            pipelineId = pipeline.Id;

            var older = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success, StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            var newer = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running, StartedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), BranchName = "main", CommitHash = "abc123" };
            db.PipelineRuns.AddRange(older, newer);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            newerRunId = newer.Id;

            // Steps deliberately inserted out of Order to prove the nested OrderBy(s => s.Order) translates.
            db.PipelineStepRuns.AddRange(
                new PipelineStepRun { PipelineRunId = newer.Id, StageName = "build", StepName = "second", Order = 2, Status = TaskExecutionStatus.Pending },
                new PipelineStepRun { PipelineRunId = newer.Id, StageName = "build", StepName = "first", Order = 1, Status = TaskExecutionStatus.Success });
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        await using (var db = NewContext())
        {
            var repo = new PipelineRepository(
                db,
                TimeProvider.System,
                NullLogger<PipelineRepository>.Instance,
                new PipelineTaskLifecycleRepository(db, TimeProvider.System),
                new PipelineRunLineageRepository(db));

            // If RunListProjection is not SQL-translatable, this throws (InvalidOperationException) here.
            var (items, total) = await repo.GetRunsPagedAsync(pipelineId, page: 1, pageSize: 10, ct: TestContext.Current.CancellationToken);

            Assert.Equal(2, total);
            // Ordered by StartedAt DESC: the newer run leads.
            Assert.Equal(newerRunId, items[0].Id);
            Assert.Equal(PipelineStatus.Running, items[0].Status);
            Assert.Equal("main", items[0].BranchName);
            Assert.Equal("abc123", items[0].CommitHash);

            // Step summary projected + ordered by Order (heavy fields intentionally absent from the list DTO).
            Assert.Equal(2, items[0].Steps.Count);
            Assert.Equal("first", items[0].Steps[0].StepName);
            Assert.Equal("second", items[0].Steps[1].StepName);
        }
    }

    [Fact]
    public async Task ConditionEvidence_CompactedPayload_PersistsWithinPostgresColumnLimits()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var organization = new Organization
        {
            Name = $"org-{Guid.NewGuid():N}",
            Slug = $"s{Guid.NewGuid():N}"[..12]
        };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var project = new Project
        {
            Name = $"project-{Guid.NewGuid():N}",
            OrganizationId = organization.Id
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var pipeline = new Pipeline
        {
            Name = $"pipeline-{Guid.NewGuid():N}",
            ProjectId = project.Id,
            YamlDefinition = "name: evidence"
        };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var run = new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Running
        };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var variables = Enumerable.Range(0, 40).ToDictionary(
            index => $"VAR_{index:D2}",
            index => new string((char)('a' + index % 26), 600));
        var step = new PipelineStepRun
        {
            PipelineRunId = run.Id,
            StageName = "quality",
            StepName = "condition",
            Status = TaskExecutionStatus.Cancelled,
            SkippedCondition = PipelineConditionEvidence.CompactCondition(new string('x', 1_100)),
            SkippedConditionVariablesJson = PipelineConditionEvidence.SerializeVariables(variables)
        };
        db.PipelineStepRuns.Add(step);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var persisted = await db.PipelineStepRuns.FindAsync(
            [step.Id], TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal(1_000, persisted.SkippedCondition!.Length);
        Assert.True(persisted.SkippedConditionVariablesJson!.Length <= 4_000);
    }
}
