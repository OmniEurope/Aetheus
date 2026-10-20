// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The run-retention port (AUD-07, suite) deletes what the retention service asks it to, and
/// the IPipelineRepository facade, which delegates to it, deletes the same runs from the same data.</summary>
public sealed class PipelineRunRetentionRepositoryTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IPipelineRepository Facade(AppDbContext db) => new PipelineRepository(
        db, TimeProvider.System, NullLogger<PipelineRepository>.Instance,
        new PipelineTaskLifecycleRepository(db, TimeProvider.System), new PipelineRunLineageRepository(db));

    /// <summary>Run 1 is old, run 2 recent, run 3 old but awaits a deferred cleanup, run 4 old but
    /// created a release that is not archived.</summary>
    private static async Task SeedAsync(AppDbContext db)
    {
        var server = new Server { Name = "offline", Hostname = "offline", Status = ServerStatus.Offline };
        db.Pipelines.Add(new Pipeline { Id = 1, Name = "P" });
        db.PipelineRuns.AddRange(
            new PipelineRun { Id = 1, PipelineId = 1, StartedAt = Now.AddDays(-120) },
            new PipelineRun { Id = 2, PipelineId = 1, StartedAt = Now },
            new PipelineRun { Id = 4, PipelineId = 1, StartedAt = Now.AddDays(-120) });
        var awaitingCleanup = new PipelineRun { Id = 3, PipelineId = 1, StartedAt = Now.AddDays(-120) };
        awaitingCleanup.Tasks.Add(new ServerTask
        {
            Server = server,
            Name = "deferred cleanup",
            Command = "cleanup",
            Status = TaskExecutionStatus.Pending,
            IsDeferredCleanup = true,
            CreatedAt = Now.AddDays(-120),
        });
        db.PipelineRuns.Add(awaitingCleanup);
        db.Releases.Add(new Release { ProjectId = 1, Version = "1.0.0", Status = ReleaseStatus.Deployed, CreatedByPipelineRunId = 4 });
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task ThePort_DeletesOnlyTheOldRunsNothingStillNeeds()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        IPipelineRunRetentionRepository port = new PipelineRunRetentionRepository(db);

        Assert.Equal(1, await port.DeleteRunsOlderThanAsync(Now.AddDays(-90), Ct));
        Assert.Equal([2, 3, 4], await db.PipelineRuns.Select(run => run.Id).OrderBy(id => id).ToListAsync(Ct));
        Assert.Equal(0, await port.DeleteRunsOlderThanAsync(Now.AddDays(-90), Ct));
    }

    [Fact]
    public async Task TheFacade_DeletesExactlyWhatThePortDeletes()
    {
        await using var portDb = NewDb();
        await using var facadeDb = NewDb();
        await SeedAsync(portDb);
        await SeedAsync(facadeDb);

        Assert.Equal(
            await new PipelineRunRetentionRepository(portDb).DeleteRunsOlderThanAsync(Now.AddDays(-90), Ct),
            await Facade(facadeDb).DeleteRunsOlderThanAsync(Now.AddDays(-90), Ct));
        Assert.Equal(
            await portDb.PipelineRuns.Select(run => run.Id).OrderBy(id => id).ToListAsync(Ct),
            await facadeDb.PipelineRuns.Select(run => run.Id).OrderBy(id => id).ToListAsync(Ct));
    }
}
