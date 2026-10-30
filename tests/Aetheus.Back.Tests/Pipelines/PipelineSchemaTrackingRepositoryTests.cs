// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-18: the two reads behind the schema tracking. The release a migration was first applied by comes from
/// the earliest successful deployment whose gate let it run; an environment's last verdict comes from the migrate
/// steps that ran on one of its servers, since a run records no environment.
/// </summary>
public sealed class PipelineSchemaTrackingRepositoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static void AddDeployment(
        AppDbContext db, int runId, DateTime startedAt, PipelineStatus status, MigrationGateOutcome outcome, string migrationsJson, string version)
    {
        db.PipelineRuns.Add(new PipelineRun { Id = runId, Status = status, StartedAt = startedAt });
        db.PipelineMigrationGateVerdicts.Add(new PipelineMigrationGateVerdict
        {
            PipelineRunId = runId,
            Outcome = outcome,
            MigrationsJson = migrationsJson,
            CreatedAt = startedAt
        });
        // The release step of the run publishes the release it recorded. Release.PipelineRunId is left unset: it
        // belongs to the latest run that recorded the release, so the first deployment is never found through it.
        db.PipelineStepRuns.Add(new PipelineStepRun
        {
            PipelineRunId = runId,
            StepName = "Record release",
            Status = TaskExecutionStatus.Success,
            OutputVariablesJson = $"{{\"RELEASE_ID\":\"{runId}\"}}"
        });
        db.Releases.Add(new Release { Id = runId, Version = version });
    }

    [Fact]
    public async Task FirstAppliedRelease_IsTheEarliestSuccessfulDeploymentWhoseGateLetTheMigrationRun()
    {
        await using var db = CreateDb();
        const string listing = "[{\"migrationId\":\"20261008_Expand\",\"compatible\":true,\"violations\":[]}]";
        AddDeployment(db, 1, T0, PipelineStatus.Failed, MigrationGateOutcome.Compatible, listing, "1.5.9");
        AddDeployment(db, 2, T0.AddHours(1), PipelineStatus.Success, MigrationGateOutcome.Refused, listing, "1.5.10");
        AddDeployment(db, 3, T0.AddHours(2), PipelineStatus.Success, MigrationGateOutcome.Compatible, listing, "1.5.11");
        AddDeployment(db, 4, T0.AddHours(3), PipelineStatus.Success, MigrationGateOutcome.Compatible, listing, "1.5.12");
        AddDeployment(db, 5, T0.AddHours(4), PipelineStatus.Success, MigrationGateOutcome.Compatible, "[]", "1.5.13");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var version = await new PipelineMigrationGateRepository(db)
            .FindFirstAppliedReleaseVersionAsync("20261008_Expand", TestContext.Current.CancellationToken);

        Assert.Equal("1.5.11", version);
    }

    // A redeploy or a rollback records the release again and overwrites Release.PipelineRunId with the later run.
    [Fact]
    public async Task FirstAppliedRelease_IsStillFoundOnceALaterRunTookOverTheReleasesRun()
    {
        await using var db = CreateDb();
        const string listing = "[{\"migrationId\":\"20261008_Expand\",\"compatible\":true,\"violations\":[]}]";
        AddDeployment(db, 1, T0, PipelineStatus.Success, MigrationGateOutcome.Compatible, listing, "1.5.9");
        AddDeployment(db, 2, T0.AddHours(1), PipelineStatus.Success, MigrationGateOutcome.Compatible, "[]", "1.5.10");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await db.Releases.SingleAsync(release => release.Id == 1, TestContext.Current.CancellationToken)).PipelineRunId = 2;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var version = await new PipelineMigrationGateRepository(db)
            .FindFirstAppliedReleaseVersionAsync("20261008_Expand", TestContext.Current.CancellationToken);

        Assert.Equal("1.5.9", version);
    }

    [Fact]
    public async Task FirstAppliedRelease_IsNullWhenNoDeploymentRecordedTheMigration()
    {
        await using var db = CreateDb();
        AddDeployment(db, 1, T0, PipelineStatus.Success, MigrationGateOutcome.Compatible, "[]", "1.5.9");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await new PipelineMigrationGateRepository(db)
            .FindFirstAppliedReleaseVersionAsync("20261008_Expand", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LatestForEnvironment_ComesFromAMigrateStepThatRanOnOneOfItsServers()
    {
        await using var db = CreateDb();
        // The link to a retired server is hidden by a query filter, so the servers must exist.
        db.Servers.AddRange(new Server { Id = 10, Name = "prod-1" }, new Server { Id = 20, Name = "prod-2" });
        db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = 1, ServerId = 10 });
        db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = 2, ServerId = 20 });
        AddProjectEnvironmentAndPipeline(db, environmentId: 1, projectId: 1, pipelineId: 1);
        AddProjectEnvironmentAndPipeline(db, environmentId: 2, projectId: 2, pipelineId: 2);
        AddMigrateStep(db, runId: 100, serverId: 10, at: T0, pipelineId: 1);
        AddMigrateStep(db, runId: 101, serverId: 10, at: T0.AddHours(2), pipelineId: 1);
        AddMigrateStep(db, runId: 102, serverId: 20, at: T0.AddHours(3), pipelineId: 2);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repo = new PipelineMigrationGateRepository(db);

        var first = await repo.FindLatestForEnvironmentAsync(1, TestContext.Current.CancellationToken);
        var second = await repo.FindLatestForEnvironmentAsync(2, TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(101, first.PipelineRunId);
        Assert.Equal(102, second.PipelineRunId);
        Assert.Null(await repo.FindLatestForEnvironmentAsync(3, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LatestForEnvironment_IgnoresAVerdictWhoseStepRanOnNoServerOfTheEnvironment()
    {
        await using var db = CreateDb();
        db.Servers.Add(new Server { Id = 10, Name = "prod-1" });
        db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = 1, ServerId = 10 });
        AddProjectEnvironmentAndPipeline(db, environmentId: 1, projectId: 1, pipelineId: 1);
        db.PipelineRuns.Add(new PipelineRun { Id = 100, PipelineId = 1 });
        db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = 100, StageName = "Deploy", StepName = "Migrate", ServerId = 10 });
        // The verdict of another step of the run, on no server of the environment.
        db.PipelineMigrationGateVerdicts.Add(new PipelineMigrationGateVerdict
        {
            PipelineRunId = 100,
            StageName = "Deploy",
            StepName = "Elsewhere",
            CreatedAt = T0
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await new PipelineMigrationGateRepository(db)
            .FindLatestForEnvironmentAsync(1, TestContext.Current.CancellationToken));
    }

    // Recette R-81: two projects whose production environments share one server (W6 deploys onto the host that runs
    // Aetheus). The migrate step of an Aetheus deployment ran on that server, but the environment of the other
    // project did not receive that deployment: its page must not show that verdict.
    [Fact]
    public async Task LatestForEnvironment_IgnoresAVerdictOfAnotherProjectsRunOnASharedServer()
    {
        await using var db = CreateDb();
        db.Servers.Add(new Server { Id = 10, Name = "shared-vps" });
        db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = 1, ServerId = 10 });
        db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = 2, ServerId = 10 });
        AddProjectEnvironmentAndPipeline(db, environmentId: 1, projectId: 1, pipelineId: 1);
        AddProjectEnvironmentAndPipeline(db, environmentId: 2, projectId: 2, pipelineId: 2);
        AddMigrateStep(db, runId: 2919, serverId: 10, at: T0, pipelineId: 1);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repo = new PipelineMigrationGateRepository(db);

        Assert.Equal(2919, (await repo.FindLatestForEnvironmentAsync(1, TestContext.Current.CancellationToken))!.PipelineRunId);
        Assert.Null(await repo.FindLatestForEnvironmentAsync(2, TestContext.Current.CancellationToken));
    }

    // A pipeline owned by the environment itself (not by its project) is also the environment's own deployment.
    [Fact]
    public async Task LatestForEnvironment_KeepsAVerdictOfAPipelineOwnedByTheEnvironment()
    {
        await using var db = CreateDb();
        db.Servers.Add(new Server { Id = 10, Name = "prod-1" });
        db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = 1, ServerId = 10 });
        db.Environments.Add(new Aetheus.Back.Data.Entities.Environment { Id = 1, Name = "prod" });
        db.Pipelines.Add(new Pipeline { Id = 1, Name = "deploy", EnvironmentId = 1 });
        AddMigrateStep(db, runId: 100, serverId: 10, at: T0, pipelineId: 1);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(100, (await new PipelineMigrationGateRepository(db)
            .FindLatestForEnvironmentAsync(1, TestContext.Current.CancellationToken))!.PipelineRunId);
    }

    [Fact]
    public async Task EnvironmentExists_AnswersForTheEnvironmentsThatAreStored()
    {
        await using var db = CreateDb();
        db.Environments.Add(new Aetheus.Back.Data.Entities.Environment { Id = 1, Name = "prod" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repo = new PipelineMigrationGateRepository(db);

        Assert.True(await repo.EnvironmentExistsAsync(1, TestContext.Current.CancellationToken));
        Assert.False(await repo.EnvironmentExistsAsync(2, TestContext.Current.CancellationToken));
    }

    private static void AddProjectEnvironmentAndPipeline(AppDbContext db, int environmentId, int projectId, int pipelineId)
    {
        db.Environments.Add(new Aetheus.Back.Data.Entities.Environment { Id = environmentId, Name = $"prod-{environmentId}", ProjectId = projectId });
        db.Pipelines.Add(new Pipeline { Id = pipelineId, Name = $"deploy-{pipelineId}", ProjectId = projectId });
    }

    private static void AddMigrateStep(AppDbContext db, int runId, int serverId, DateTime at, int pipelineId)
    {
        db.PipelineRuns.Add(new PipelineRun { Id = runId, PipelineId = pipelineId, StartedAt = at });
        db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = runId, StageName = "Deploy", StepName = "Migrate", ServerId = serverId });
        db.PipelineMigrationGateVerdicts.Add(new PipelineMigrationGateVerdict
        {
            PipelineRunId = runId,
            StageName = "Deploy",
            StepName = "Migrate",
            CreatedAt = at
        });
    }
}
