// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Decision of 2026-10-02: a run that needs an environment another run holds waits; it does not fail,
/// it dispatches nothing on that environment, and it says which run it waits for.
/// </summary>
public sealed class PipelineEnvironmentLockDispatchTests
{
    private const int RunId = 4243;

    private sealed record Harness(
        PipelineStageDispatchPlanner Planner, IPipelineRepository Repo, IPipelineRunFinalizer Finalizer,
        IPipelineStepTaskDispatcher Dispatcher, IPipelineResourceLockRepository Locks);

    private static Harness Build(PipelineResourceLockHolder? holder)
    {
        var repo = Substitute.For<IPipelineRepository>();
        var finalizer = Substitute.For<IPipelineRunFinalizer>();
        var dispatcher = Substitute.For<IPipelineStepTaskDispatcher>();
        var environmentChecks = Substitute.For<IPipelineEnvironmentCheckGuard>();
        var locks = Substitute.For<IPipelineResourceLockRepository>();
        repo.GetCompletedStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetTerminalStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetActiveStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetSuccessfulStepOutputsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetRunAffinityServerIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((int?)null);
        repo.FindEnvironmentByNameAsync("qa", Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(new Environment { Id = 3, Name = "qa" });
        environmentChecks.CheckEnvironmentChecksAsync(Arg.Any<PipelineStageDefinition>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        finalizer.IsRunActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        locks.TryAcquireAsync(RunId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(holder);

        // No online runner, but one is configured: a stage that gets past the lock gate waits for it.
        var servers = Substitute.For<IPipelineDispatchServerResolver>();
        servers.ResolveEffectiveStageTarget(Arg.Any<PipelineStageDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(callInfo => callInfo.Arg<PipelineStageDefinition>());
        servers.ResolveServerForTargetAsync(
                Arg.Any<PipelineStageDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((Server?)null);
        repo.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([7]);

        var planner = new PipelineStageDispatchPlanner(
            repo, servers, environmentChecks, finalizer,
            Substitute.For<IPipelineSystemTaskFactory>(), dispatcher,
            Substitute.For<IHubContext<PipelineHub>>(), Substitute.For<IDomainEventDispatcher>(),
            TimeProvider.System, Substitute.For<ILogger<PipelineStageDispatchPlanner>>(), locks);
        return new Harness(planner, repo, finalizer, dispatcher, locks);
    }

    private static Task Dispatch(Harness harness, PipelineStageDefinition stage) =>
        harness.Planner.CreateTasksForNextStageAsync(
            RunId, new PipelineYamlDefinition { Name = "aetheus-qa", Stages = [stage] }, [],
            [new PipelineStepRun { StageName = stage.Name, Status = TaskExecutionStatus.Pending }],
            null, [], Substitute.For<IPipelineChildRunLauncher>(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task AStageOnAHeldEnvironment_Waits_NamingTheHolder_AndDoesNotFail()
    {
        var harness = Build(new PipelineResourceLockHolder("environment:3", 2503, "aetheus-nightly", 12));

        await Dispatch(harness, new PipelineStageDefinition { Name = "Deploy V", Environment = "qa" });

        await harness.Repo.Received(1).SetRunWaitingReasonAsync(
            RunId,
            Arg.Is<string>(reason => reason.Contains("Deploy V") && reason.Contains("'qa'")
                && reason.Contains("aetheus-nightly #12") && reason.Contains(PipelineEnvironmentLockGate.WaitMarker)),
            Arg.Any<CancellationToken>());
        await harness.Finalizer.DidNotReceiveWithAnyArgs().FailRunWithUnmatchedStagesAsync(default, default!, default!, TestContext.Current.CancellationToken);
        await harness.Dispatcher.DidNotReceiveWithAnyArgs().CreateTasksForStageAsync(
            default, default!, default!, default!, default!, default!, default, default!, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AStageWithoutEnvironment_NeverAsksForALock()
    {
        var harness = Build(new PipelineResourceLockHolder("environment:3", 2503, "aetheus-nightly", 12));

        await Dispatch(harness, new PipelineStageDefinition { Name = "Build images" });

        await harness.Locks.DidNotReceiveWithAnyArgs().TryAcquireAsync(default, default!, TestContext.Current.CancellationToken);
    }
}
