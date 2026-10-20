// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Components.Tasks.Events;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Tasks;

public sealed class UnexecutableTaskRefuserTests : IDisposable
{
    private const string Reason = "L'agent du serveur « vps » (1.3.15) ne sait pas exécuter cette tâche.";

    private readonly AppDbContext _db;
    private readonly IDomainEventDispatcher _events = Substitute.For<IDomainEventDispatcher>();
    private readonly UnexecutableTaskRefuser _sut;

    public UnexecutableTaskRefuserTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _sut = new UnexecutableTaskRefuser(
            new TaskRepository(_db, TimeProvider.System),
            Substitute.For<IHubContext<PipelineHub>>(),
            Substitute.For<IHubContext<ServerHub>>(),
            _events,
            TimeProvider.System,
            NullLogger<UnexecutableTaskRefuser>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private async Task<(ServerTask Task, PipelineStepRun? Step)> SeedAsync(
        TaskExecutionStatus status, bool withStep, OperationKind operation = OperationKind.PipelineFunction)
    {
        var server = new Server { Name = "vps", Hostname = "vps", Status = ServerStatus.Online };
        var pipeline = new Pipeline { Name = "release" };
        var run = new PipelineRun { Pipeline = pipeline, Status = PipelineStatus.Running };
        var step = withStep
            ? new PipelineStepRun { PipelineRun = run, StageName = "work", StepName = "bump", Status = TaskExecutionStatus.Assigned }
            : null;
        var task = new ServerTask
        {
            Server = server,
            PipelineRun = run,
            PipelineStepRun = step,
            Name = "bump",
            Command = "bump-version",
            Operation = operation,
            Status = status,
            EnvironmentVariables = "{\"SECRET\":\"x\"}"
        };
        _db.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (task, step);
    }

    [Fact]
    public async Task PendingPipelineTask_IsFailedWithItsReason_AndTheStepAndRunAreSettled()
    {
        var (task, step) = await SeedAsync(TaskExecutionStatus.Pending, withStep: true);

        var refused = await _sut.RefuseAsync(
            task.Id, TaskFailureCodes.AgentIncompatible, Reason, TestContext.Current.CancellationToken);

        Assert.True(refused);
        var stored = await _db.Tasks.Include(item => item.Logs).SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskExecutionStatus.Failed, stored.Status);
        Assert.Equal(TaskFailureCodes.AgentIncompatible, stored.FailureCode);
        Assert.Equal(Reason, stored.FailureReason);
        Assert.Equal("{}", stored.EnvironmentVariables);
        Assert.NotNull(stored.CompletedAt);
        Assert.Contains(stored.Logs, log => log.Level == TaskLogLevel.Error
            && log.Message == $"[incident:AgentIncompatible] {Reason}");
        Assert.Equal(TaskExecutionStatus.Failed, step!.Status);
        Assert.Equal(TaskFailureCodes.AgentIncompatible, step.FailureCode);
        Assert.Equal(Reason, step.FailureReason);
        await _events.Received(1).DispatchStrictAsync(
            Arg.Is<PipelineStepTaskCompletedEvent>(e => e.PipelineRunId == task.PipelineRunId
                && e.StageName == "work" && e.TaskStatus == TaskExecutionStatus.Failed && !e.IsArtifactCollection),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Assigned)]
    [InlineData(TaskExecutionStatus.Running)]
    [InlineData(TaskExecutionStatus.Success)]
    [InlineData(TaskExecutionStatus.Cancelled)]
    public async Task TaskNoLongerPending_IsLeftUntouched(TaskExecutionStatus status)
    {
        var (task, step) = await SeedAsync(status, withStep: true);

        var refused = await _sut.RefuseAsync(
            task.Id, TaskFailureCodes.AgentIncompatible, Reason, TestContext.Current.CancellationToken);

        Assert.False(refused);
        Assert.Equal(status, (await _db.Tasks.SingleAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(TaskExecutionStatus.Assigned, step!.Status);
        Assert.Null(step.FailureReason);
        await _events.DidNotReceiveWithAnyArgs().DispatchStrictAsync(
            default(PipelineStepTaskCompletedEvent)!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StepAlreadySettled_IsNotReopenedNorTheStageAdvancedAgain()
    {
        var (task, step) = await SeedAsync(TaskExecutionStatus.Pending, withStep: true);
        step!.Status = TaskExecutionStatus.Cancelled;
        step.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _sut.RefuseAsync(
            task.Id, TaskFailureCodes.AgentIncompatible, Reason, TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Cancelled, step.Status);
        await _events.DidNotReceiveWithAnyArgs().DispatchStrictAsync(
            default(PipelineStepTaskCompletedEvent)!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StepLessArtifactCollection_ResumesTheRunInsteadOfHangingIt()
    {
        var (task, _) = await SeedAsync(
            TaskExecutionStatus.Pending, withStep: false, OperationKind.PipelineCollectArtifacts);

        await _sut.RefuseAsync(
            task.Id, TaskFailureCodes.AgentIncompatible, Reason, TestContext.Current.CancellationToken);

        await _events.Received(1).DispatchStrictAsync(
            Arg.Is<PipelineStepTaskCompletedEvent>(e => e.StageName == null && e.IsArtifactCollection
                && e.TaskStatus == TaskExecutionStatus.Failed),
            Arg.Any<CancellationToken>());
    }
}
