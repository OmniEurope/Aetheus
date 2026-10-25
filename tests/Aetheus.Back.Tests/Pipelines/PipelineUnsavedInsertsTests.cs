// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests;

/// <summary>R-38: a scheduling pass refused half-way drops the inserts it queued, and only those, on a real
/// change tracker (EF in-memory), not on a mock that could not show what the next save would write.</summary>
public sealed class PipelineUnsavedInsertsTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly PipelineRepository _repo;

    public PipelineUnsavedInsertsTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new PipelineRepository(
            _db,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineRepository>.Instance,
            new PipelineTaskLifecycleRepository(_db, TimeProvider.System),
            new PipelineRunLineageRepository(_db));
    }

    public void Dispose() => _db.Dispose();

    private static ServerTask QueuedTask(string name) => new()
    {
        ServerId = 10,
        Name = name,
        Command = "echo",
        PipelineRunId = 1
    };

    private async Task<PipelineStepRun> SavedStepRunAsync()
    {
        var step = new PipelineStepRun { PipelineRunId = 1, StageName = "expose", StepName = "notify" };
        _db.PipelineStepRuns.Add(step);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return step;
    }

    [Fact]
    public async Task DiscardUnsavedInsertsSince_DropsTheTasksQueuedAfterTheMarkAndTheLinkFromTheirStep()
    {
        var step = await SavedStepRunAsync();
        var mark = _repo.MarkUnsavedInserts();
        var queued = QueuedTask("notify");
        _repo.TrackTask(queued);
        step.Task = queued;
        step.Status = TaskExecutionStatus.Assigned;

        _repo.DiscardUnsavedInsertsSince(mark);
        await _repo.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await _db.Tasks.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Null(step.Task);
        Assert.Null(step.TaskId);
        Assert.Equal(EntityState.Detached, _db.Entry(queued).State);
    }

    [Fact]
    public async Task DiscardUnsavedInsertsSince_KeepsTheInsertsQueuedBeforeTheMark()
    {
        var earlier = QueuedTask("earlier");
        _repo.TrackTask(earlier);
        var mark = _repo.MarkUnsavedInserts();
        _repo.TrackTask(QueuedTask("refused"));

        _repo.DiscardUnsavedInsertsSince(mark);
        await _repo.SaveChangesAsync(TestContext.Current.CancellationToken);

        var saved = await _db.Tasks.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal("earlier", Assert.Single(saved).Name);
    }

    [Fact]
    public async Task DiscardUnsavedInsertsSince_RemovesTheDroppedTaskFromTheCollectionOfATrackedOwner()
    {
        var server = new Server { Name = "linux-01", Hostname = "linux-01" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var mark = _repo.MarkUnsavedInserts();
        var queued = QueuedTask("refused");
        queued.Server = server;
        server.Tasks.Add(queued);
        _repo.TrackTask(queued);

        _repo.DiscardUnsavedInsertsSince(mark);
        await _repo.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(server.Tasks);
        Assert.Empty(await _db.Tasks.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DiscardUnsavedInsertsSince_WithAMarkNobodyTook_DropsNothing()
    {
        _repo.TrackTask(QueuedTask("queued"));

        _repo.DiscardUnsavedInsertsSince(default);
        await _repo.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Single(await _db.Tasks.ToListAsync(TestContext.Current.CancellationToken));
    }
}
