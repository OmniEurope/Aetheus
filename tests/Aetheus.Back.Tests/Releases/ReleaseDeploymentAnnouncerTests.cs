// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Releases;

/// <summary>A release step recorded as deployed announces deployment with the executing agent
/// stage.</summary>
public sealed class ReleaseDeploymentAnnouncerTests : IDisposable
{
    private const int RunId = 2477;
    private const int AgentServerId = 5;

    private readonly AppDbContext _db;
    private readonly IDomainEventDispatcher _dispatcher = Substitute.For<IDomainEventDispatcher>();
    private readonly ReleaseDeploymentAnnouncer _announcer;

    public ReleaseDeploymentAnnouncerTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var pipelines = new PipelineRepository(
            _db,
            TimeProvider.System,
            NullLogger<PipelineRepository>.Instance,
            new PipelineTaskLifecycleRepository(_db, TimeProvider.System),
            new PipelineRunLineageRepository(_db));
        _announcer = new ReleaseDeploymentAnnouncer(pipelines, _dispatcher);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Announce_NamesTheStageTheAgentIsRunning()
    {
        _db.PipelineStepRuns.AddRange(
            Step("Confirm", TaskExecutionStatus.Success, AgentServerId),
            Step("Record release", TaskExecutionStatus.Running, AgentServerId),
            Step("Commit", TaskExecutionStatus.Pending, AgentServerId),
            // Another agent's running step in the same run does not blur the answer.
            Step("Publish sites", TaskExecutionStatus.Running, serverId: 6));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _announcer.AnnounceAsync(releaseId: 9, RunId, AgentServerId, TestContext.Current.CancellationToken);

        await _dispatcher.Received(1).DispatchAsync(
            new ReleaseDeployedEvent(9, RunId, "Record release", false), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Announce_LeavesTheStageOut_WhenTheAgentRunsStepsOfTwoStages()
    {
        _db.PipelineStepRuns.AddRange(
            Step("Record release", TaskExecutionStatus.Running, AgentServerId),
            Step("Publish sites", TaskExecutionStatus.Running, AgentServerId));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _announcer.AnnounceAsync(releaseId: 9, RunId, AgentServerId, TestContext.Current.CancellationToken);

        // Without a stage the handler finds no target environment and advances nothing: no guess.
        await _dispatcher.Received(1).DispatchAsync(
            new ReleaseDeployedEvent(9, RunId, null, false), Arg.Any<CancellationToken>());
    }

    private static PipelineStepRun Step(string stage, TaskExecutionStatus status, int serverId) => new()
    {
        PipelineRunId = RunId,
        StageName = stage,
        StepName = stage,
        Status = status,
        ServerId = serverId
    };
}
