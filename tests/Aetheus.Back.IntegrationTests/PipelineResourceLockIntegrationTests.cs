// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The environment lock (decision of 2026-10-02) on a real PostgreSQL: a run that holds an environment
/// keeps another out, the lock counts only while its holder is active, a run started by the holder's
/// trigger step shares it, and the end of the run frees it.
/// </summary>
public sealed class PipelineResourceLockIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private const string Qa = "environment:1";
    private const string Prod = "environment:2";
    private int _nextBuildNumber = 6;

    private PipelineResourceLockRepository Repository(AppDbContext db) => new(db, TimeProvider.System);

    private async Task<int> SeedPipelineAsync(string name, CancellationToken ct)
    {
        await using var db = NewContext();
        var organization = new Organization { Name = "Locks", Slug = $"locks-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = "Project", OrganizationId = organization.Id, DefaultBranch = "main" };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        var pipeline = new Pipeline { Name = name, ProjectId = project.Id, YamlDefinition = $"name: {name}\ntrigger: manual\nstages: []" };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);
        return pipeline.Id;
    }

    private async Task<int> SeedRunAsync(int pipelineId, PipelineStatus status, CancellationToken ct, int? parentRunId = null)
    {
        await using var db = NewContext();
        var run = new PipelineRun { PipelineId = pipelineId, Status = status, StartedAt = DateTime.UtcNow, BuildNumber = ++_nextBuildNumber };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(ct);
        if (parentRunId is { } parent)
        {
            // The parent's trigger step records the child it started: that is the lineage the lock follows.
            db.PipelineStepRuns.Add(new PipelineStepRun
            {
                PipelineRunId = parent,
                StepName = "Run QA",
                StageName = "QA",
                Status = TaskExecutionStatus.Running,
                TriggeredRunId = run.Id
            });
            await db.SaveChangesAsync(ct);
        }
        return run.Id;
    }

    private async Task SetStatusAsync(int runId, PipelineStatus status, CancellationToken ct)
    {
        await using var db = NewContext();
        await db.PipelineRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status), ct);
    }

    [Fact]
    public async Task AHeldEnvironment_KeepsAnotherRunOut_NamingTheHolder()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        var pipelineId = await SeedPipelineAsync("aetheus-nightly", ct);
        var first = await SeedRunAsync(pipelineId, PipelineStatus.Running, ct);
        var second = await SeedRunAsync(pipelineId, PipelineStatus.Running, ct);

        await using var db = NewContext();
        Assert.Null(await Repository(db).TryAcquireAsync(first, [Qa, Prod], ct));
        var holder = await Repository(db).TryAcquireAsync(second, [Prod], ct);

        Assert.NotNull(holder);
        Assert.Equal(first, holder.RunId);
        Assert.Equal("aetheus-nightly", holder.PipelineName);
        Assert.Equal(Prod, holder.Key);
    }

    [Theory]
    [InlineData(PipelineStatus.Failed)]
    public async Task AnEndedHolder_NoLongerBlocks_EvenWhenItsRowStayed(PipelineStatus ended)
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        var pipelineId = await SeedPipelineAsync("aetheus-candidate", ct);
        var first = await SeedRunAsync(pipelineId, PipelineStatus.Running, ct);
        var second = await SeedRunAsync(pipelineId, PipelineStatus.Running, ct);
        await using (var db = NewContext())
            Assert.Null(await Repository(db).TryAcquireAsync(first, [Qa], ct));

        await SetStatusAsync(first, ended, ct);

        await using (var db = NewContext())
            Assert.Null(await Repository(db).TryAcquireAsync(second, [Qa], ct));
        await using (var db = NewContext())
            Assert.Equal(second, (await db.PipelineResourceLocks.SingleAsync(l => l.Key == Qa, ct)).PipelineRunId);
    }

    [Fact]
    public async Task ARunStartedByTheHolder_SharesItsEnvironment()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        var pipelineId = await SeedPipelineAsync("aetheus-nightly", ct);
        var parent = await SeedRunAsync(pipelineId, PipelineStatus.Running, ct);
        var child = await SeedRunAsync(pipelineId, PipelineStatus.Running, ct, parentRunId: parent);
        var stranger = await SeedRunAsync(pipelineId, PipelineStatus.Running, ct);

        await using var db = NewContext();
        Assert.Null(await Repository(db).TryAcquireAsync(parent, [Qa], ct));
        Assert.Null(await Repository(db).TryAcquireAsync(child, [Qa], ct));
        Assert.NotNull(await Repository(db).TryAcquireAsync(stranger, [Qa], ct));
        Assert.Equal(parent, (await db.PipelineResourceLocks.AsNoTracking().SingleAsync(l => l.Key == Qa, ct)).PipelineRunId);
    }

    [Fact]
    public async Task Release_FreesEverythingTheRunHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        var pipelineId = await SeedPipelineAsync("aetheus-deploy-prod", ct);
        var first = await SeedRunAsync(pipelineId, PipelineStatus.Running, ct);
        var second = await SeedRunAsync(pipelineId, PipelineStatus.Running, ct);

        await using var db = NewContext();
        Assert.Null(await Repository(db).TryAcquireAsync(first, [Qa, Prod], ct));
        Assert.Equal(2, await Repository(db).ReleaseAsync(first, ct));
        Assert.Null(await Repository(db).TryAcquireAsync(second, [Qa, Prod], ct));
    }

    [Fact]
    public async Task AllOrNothing_ARunBlockedOnOneEnvironment_TakesNoneOfThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        var pipelineId = await SeedPipelineAsync("aetheus-release-fast", ct);
        var holder = await SeedRunAsync(pipelineId, PipelineStatus.WaitingForApproval, ct);
        var waiter = await SeedRunAsync(pipelineId, PipelineStatus.Running, ct);

        await using var db = NewContext();
        Assert.Null(await Repository(db).TryAcquireAsync(holder, [Prod], ct));
        Assert.NotNull(await Repository(db).TryAcquireAsync(waiter, [Qa, Prod], ct));
        Assert.False(await db.PipelineResourceLocks.AnyAsync(l => l.PipelineRunId == waiter, ct));
    }
}
