// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>A scheduling pass refused after a mid-pass save has to cancel, in the database, the tasks it
/// had queued for the steps it touched, and only those: through the IPipelineRepository facade, down to the
/// task lifecycle.</summary>
public sealed class PipelineStepRunTaskCancellationTests : IDisposable
{
    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IPipelineRepository Facade() => new PipelineRepository(
        _db, TimeProvider.System, NullLogger<PipelineRepository>.Instance,
        new PipelineTaskLifecycleRepository(_db, TimeProvider.System), new PipelineRunLineageRepository(_db));

    private static ServerTask NewTask(int id, int runId, int stepRunId, TaskExecutionStatus status) => new()
    {
        Id = id,
        ServerId = 1,
        Name = $"task-{id}",
        PipelineRunId = runId,
        PipelineStepRunId = stepRunId,
        Status = status,
        EnvironmentVariables = "{\"SECRET\":\"value\"}"
    };

    private async Task SeedAsync()
    {
        _db.Tasks.AddRange(
            NewTask(1, 10, 100, TaskExecutionStatus.Pending),
            NewTask(2, 10, 100, TaskExecutionStatus.Assigned),
            NewTask(3, 10, 100, TaskExecutionStatus.Running),
            NewTask(4, 10, 100, TaskExecutionStatus.Success),
            NewTask(5, 10, 101, TaskExecutionStatus.Pending),
            NewTask(6, 11, 100, TaskExecutionStatus.Pending));
        await _db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task CancelActiveTasksOfStepRunsAsync_CancelsTheActiveTasksOfThoseStepsOfThatRunOnly()
    {
        await SeedAsync();

        await Facade().CancelActiveTasksOfStepRunsAsync(10, [100], Ct);
        await _db.SaveChangesAsync(Ct);

        var tasks = await _db.Tasks.AsNoTracking().OrderBy(task => task.Id).ToListAsync(Ct);
        Assert.Equal(
            [
                TaskExecutionStatus.Cancelled, TaskExecutionStatus.Cancelled, TaskExecutionStatus.Cancelled,
                TaskExecutionStatus.Success, TaskExecutionStatus.Pending, TaskExecutionStatus.Pending
            ],
            tasks.Select(task => task.Status));
        Assert.All(tasks.Take(3), task =>
        {
            Assert.NotNull(task.CompletedAt);
            Assert.Equal(TaskEnvProtection.EmptyEnv, task.EnvironmentVariables);
        });
        Assert.All(tasks.Skip(3), task => Assert.Contains("SECRET", task.EnvironmentVariables, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelActiveTasksOfStepRunsAsync_WithNoStepRun_CancelsNothing()
    {
        await SeedAsync();

        await Facade().CancelActiveTasksOfStepRunsAsync(10, [], Ct);
        await _db.SaveChangesAsync(Ct);

        Assert.DoesNotContain(
            await _db.Tasks.AsNoTracking().ToListAsync(Ct), task => task.Status == TaskExecutionStatus.Cancelled);
    }

    public void Dispose() => _db.Dispose();
}
