// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// A failed artifact collection fails its run (BACK-019), but only for as long as it is the run's last
/// word on that artifact: a retry that collects it again must be able to end Success, and a collection
/// step the definition allows to fail (<c>continue_on_error</c>) must not fail the run on its own.
/// Real PostgreSQL, because both answers are relational queries over tasks and step runs.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PipelineArtifactCollectionRetryIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task ARetryAfterAFailedStageCollection_ReplaysTheProducerAndCanEndSuccess()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedRunAsync(ct);
        int buildStep, deployStep;
        await using (var seed = NewContext())
        {
            var build = Step(seeded.RunId, "Build", TaskExecutionStatus.Success);
            var deploy = Step(seeded.RunId, "Deploy", TaskExecutionStatus.Cancelled);
            seed.PipelineStepRuns.AddRange(build, deploy);
            await seed.SaveChangesAsync(ct);
            seed.Tasks.Add(StageCollection(seeded, "Build", TaskExecutionStatus.Failed));
            await seed.SaveChangesAsync(ct);
            (buildStep, deployStep) = (build.Id, deploy.Id);
        }

        await using (var db = NewContext())
        {
            var repo = NewRepository(db);
            Assert.True(await repo.HasFailedArtifactCollectionAsync(seeded.RunId, ct));
            Assert.True(await repo.ResetFailedStepRunsAsync(seeded.RunId, ct) > 0);
        }

        await using (var db = NewContext())
        {
            var repo = NewRepository(db);
            // The artifact only exists if its stage runs again: the producer is replayed with the retry.
            Assert.Equal(TaskExecutionStatus.Pending, (await db.PipelineStepRuns.FindAsync([buildStep], ct))!.Status);
            Assert.Equal(TaskExecutionStatus.Pending, (await db.PipelineStepRuns.FindAsync([deployStep], ct))!.Status);
            // While it is being replayed, the old failure does not fail the retry.
            Assert.False(await repo.HasFailedArtifactCollectionAsync(seeded.RunId, ct));
        }

        await using (var db = NewContext())
        {
            foreach (var step in db.PipelineStepRuns.Where(step => step.PipelineRunId == seeded.RunId))
                step.Status = TaskExecutionStatus.Success;
            db.Tasks.Add(StageCollection(seeded, "Build", TaskExecutionStatus.Success));
            await db.SaveChangesAsync(ct);
            var repo = NewRepository(db);
            Assert.False(await repo.HasFailedArtifactCollectionAsync(seeded.RunId, ct));
        }

        await using (var db = NewContext())
        {
            // No fake green: the replayed collection failing again fails the run again.
            db.Tasks.Add(StageCollection(seeded, "Build", TaskExecutionStatus.Failed));
            await db.SaveChangesAsync(ct);
            Assert.True(await NewRepository(db).HasFailedArtifactCollectionAsync(seeded.RunId, ct));
        }
    }

    [Fact]
    public async Task ARetriedCollectionStep_IsJudgedOnItsLatestAttempt()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedRunAsync(ct);
        int collectStep;
        await using (var seed = NewContext())
        {
            var collect = Step(seeded.RunId, "Package", TaskExecutionStatus.Failed);
            seed.PipelineStepRuns.Add(collect);
            await seed.SaveChangesAsync(ct);
            seed.Tasks.Add(StepCollection(seeded, collect.Id, TaskExecutionStatus.Failed));
            await seed.SaveChangesAsync(ct);
            collectStep = collect.Id;
        }

        await using (var db = NewContext())
        {
            var repo = NewRepository(db);
            Assert.True(await repo.HasFailedArtifactCollectionAsync(seeded.RunId, ct));
            Assert.Equal(1, await repo.ResetFailedStepRunsAsync(seeded.RunId, ct));
        }

        await using (var db = NewContext())
        {
            Assert.False(await NewRepository(db).HasFailedArtifactCollectionAsync(seeded.RunId, ct));
            var step = (await db.PipelineStepRuns.FindAsync([collectStep], ct))!;
            step.Status = TaskExecutionStatus.Success;
            db.Tasks.Add(StepCollection(seeded, collectStep, TaskExecutionStatus.Success));
            await db.SaveChangesAsync(ct);
            Assert.False(await NewRepository(db).HasFailedArtifactCollectionAsync(seeded.RunId, ct));
        }
    }

    [Fact]
    public async Task AContinueOnErrorCollectionStep_DoesNotFailTheRunByItself()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedRunAsync(ct);
        await using (var seed = NewContext())
        {
            var optional = Step(seeded.RunId, "Package", TaskExecutionStatus.Failed);
            optional.ContinueOnError = true;
            var required = Step(seeded.RunId, "Publish", TaskExecutionStatus.Failed);
            seed.PipelineStepRuns.AddRange(optional, required);
            await seed.SaveChangesAsync(ct);
            seed.Tasks.Add(StepCollection(seeded, optional.Id, TaskExecutionStatus.Failed));
            await seed.SaveChangesAsync(ct);
        }

        await using (var db = NewContext())
            Assert.False(await NewRepository(db).HasFailedArtifactCollectionAsync(seeded.RunId, ct));

        await using (var db = NewContext())
        {
            // The same failure on a step that may not fail still fails the run.
            var required = db.PipelineStepRuns.Single(step => step.PipelineRunId == seeded.RunId && step.StageName == "Publish");
            db.Tasks.Add(StepCollection(seeded, required.Id, TaskExecutionStatus.Timeout));
            await db.SaveChangesAsync(ct);
            Assert.True(await NewRepository(db).HasFailedArtifactCollectionAsync(seeded.RunId, ct));
        }
    }

    private sealed record SeededRun(int RunId, int ServerId);

    private async Task<SeededRun> SeedRunAsync(CancellationToken ct)
    {
        await using var seed = NewContext();
        var organization = new Organization { Name = "Collect", Slug = $"collect-{Guid.NewGuid():N}"[..20] };
        seed.Organizations.Add(organization);
        await seed.SaveChangesAsync(ct);
        var project = new Project { Name = "Collect", OrganizationId = organization.Id };
        seed.Projects.Add(project);
        var server = new Server
        {
            OrganizationId = organization.Id,
            Name = "collector",
            Hostname = "collector.local",
            Status = ServerStatus.Online,
        };
        seed.Servers.Add(server);
        await seed.SaveChangesAsync(ct);
        var pipeline = new Pipeline { Name = "deliver", ProjectId = project.Id, YamlDefinition = "name: deliver\nstages: []" };
        seed.Pipelines.Add(pipeline);
        await seed.SaveChangesAsync(ct);
        var run = new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Failed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        };
        seed.PipelineRuns.Add(run);
        await seed.SaveChangesAsync(ct);
        return new SeededRun(run.Id, server.Id);
    }

    private static PipelineStepRun Step(int runId, string stage, TaskExecutionStatus status) => new()
    {
        PipelineRunId = runId,
        StageName = stage,
        StepName = $"{stage} step",
        Status = status,
    };

    private static ServerTask StageCollection(SeededRun run, string stage, TaskExecutionStatus status) => new()
    {
        ServerId = run.ServerId,
        Name = PipelineRunStatusRepository.StageArtifactCollectionTaskName(stage),
        Command = "[]",
        PipelineRunId = run.RunId,
        Operation = OperationKind.PipelineCollectArtifacts,
        Status = status,
        CreatedAt = DateTime.UtcNow,
    };

    private static ServerTask StepCollection(SeededRun run, int stepRunId, TaskExecutionStatus status) => new()
    {
        ServerId = run.ServerId,
        Name = "Collect Artifacts",
        Command = "[]",
        PipelineRunId = run.RunId,
        PipelineStepRunId = stepRunId,
        Operation = OperationKind.PipelineCollectArtifacts,
        Status = status,
        CreatedAt = DateTime.UtcNow,
    };

    private static PipelineRepository NewRepository(AppDbContext db) => new(
        db, TimeProvider.System, NullLogger<PipelineRepository>.Instance,
        new Aetheus.Back.Components.Tasks.PipelineTaskLifecycleRepository(db, TimeProvider.System),
        new PipelineRunLineageRepository(db));
}
