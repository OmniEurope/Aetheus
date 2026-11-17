// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// R-10: the fenced completion stamps the step with the instant it stamps the task; the finalizer must not stamp it
/// a second time (one more UPDATE per completion for a few microseconds), and still stamps a step nobody stamped.
/// </summary>
public sealed class TaskCompletionFinalizerStepStampTests
{
    private static readonly DateTime Stamped = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

    private static async Task<PipelineStepRun> CompleteAsync(PipelineStepRun stepRun, DateTime clockNow)
    {
        var repo = Substitute.For<ITaskRepository>();
        repo.FindPipelineStepRunAsync(stepRun.Id, Arg.Any<CancellationToken>()).Returns(stepRun);
        var clock = new FakeTimeProvider(new DateTimeOffset(clockNow, TimeSpan.Zero));
        var finalizer = new TaskCompletionFinalizer(
            repo, Substitute.For<ILogService>(), Substitute.For<IAuditService>(), clock,
            Substitute.For<IArtifactService>(), NullLogger<TaskService>.Instance, Substitute.For<IDomainEventDispatcher>());

        var updated = await finalizer.UpdateStepRunAsync(
            new ServerTask { Id = 21, PipelineRunId = 7, PipelineStepRunId = stepRun.Id },
            new TaskResultDto { TaskId = 21, Status = TaskExecutionStatus.Success, ExitCode = 0, Output = "done" },
            "done", TestContext.Current.CancellationToken);

        return updated!;
    }

    [Fact]
    public async Task AStepTheLeaseCompletionStamped_KeepsThatInstant()
    {
        var step = new PipelineStepRun { Id = 11, PipelineRunId = 7, StageName = "Build", StepName = "Compile", CompletedAt = Stamped };

        var updated = await CompleteAsync(step, Stamped.AddMilliseconds(3));

        Assert.Equal(Stamped, updated.CompletedAt);
        Assert.Equal(TaskExecutionStatus.Success, updated.Status);
        Assert.Equal(0, updated.ExitCode);
    }

    [Fact]
    public async Task AStepNobodyStamped_GetsTheTimeOfTheCompletion()
    {
        var step = new PipelineStepRun { Id = 12, PipelineRunId = 7, StageName = "Build", StepName = "Compile" };

        var updated = await CompleteAsync(step, Stamped.AddMilliseconds(3));

        Assert.Equal(Stamped.AddMilliseconds(3), updated.CompletedAt);
    }
}
