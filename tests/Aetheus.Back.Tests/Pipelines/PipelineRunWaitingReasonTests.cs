// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// A run that dispatches nothing used to look exactly like a run making progress: Running, no task
/// moving, and the reason living only inside the planner's local state. These tests pin the reason
/// onto the run, and pin that it disappears the moment something moves - a stale "waiting for a
/// runner" on an advancing run would be worse than none.
/// </summary>
public class PipelineRunWaitingReasonTests
{
    private const int RunId = 4242;

    private static PipelineYamlDefinition Definition(PipelineStageDefinition stage) =>
        new() { Name = "candidate", Stages = [stage] };

    private sealed record Harness(
        PipelineStageDispatchPlanner Planner,
        IPipelineRepository Repo,
        IPipelineDispatchServerResolver Servers,
        IPipelineStepTaskDispatcher Dispatcher);

    private static Harness Build(bool anythingRunning = false, bool artifactCollection = false)
    {
        var repo = Substitute.For<IPipelineRepository>();
        var servers = Substitute.For<IPipelineDispatchServerResolver>();
        var dispatcher = Substitute.For<IPipelineStepTaskDispatcher>();
        var finalizer = Substitute.For<IPipelineRunFinalizer>();
        var environmentChecks = Substitute.For<IPipelineEnvironmentCheckGuard>();

        repo.HasActiveArtifactCollectionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(artifactCollection);
        repo.GetCompletedStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetTerminalStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.HasAnyFailedStepInRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        repo.HasAnyRunningStepInRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(anythingRunning);
        repo.GetActiveStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetRunAffinityServerIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((int?)null);
        repo.GetSuccessfulStepOutputsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        environmentChecks.CheckEnvironmentChecksAsync(Arg.Any<PipelineStageDefinition>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        finalizer.IsRunActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        // NSubstitute hands back "" for an unstubbed string, and the planner reads any non-null as a
        // policy violation. Say "no violation" explicitly, or every dispatch here fails the run.
        servers.CheckDeploymentPolicy(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Server>()).Returns((string?)null);

        var planner = new PipelineStageDispatchPlanner(
            repo, servers, environmentChecks, finalizer,
            Substitute.For<IPipelineSystemTaskFactory>(), dispatcher,
            Substitute.For<IHubContext<PipelineHub>>(),
            Substitute.For<IDomainEventDispatcher>(),
            TimeProvider.System,
            Substitute.For<ILogger<PipelineStageDispatchPlanner>>());
        return new Harness(planner, repo, servers, dispatcher);
    }

    private static Task Dispatch(Harness harness, PipelineStageDefinition stage, List<PipelineStepRun> pending) =>
        harness.Planner.CreateTasksForNextStageAsync(
            RunId, Definition(stage), [], pending, null, [],
            Substitute.For<IPipelineChildRunLauncher>(), CancellationToken.None);

    private static List<PipelineStepRun> Pending(string stageName, TaskExecutionStatus status = TaskExecutionStatus.Pending) =>
        [new() { StageName = stageName, Status = status }];

    [Fact]
    public async Task AStageWaitingForAnOfflineRunner_RecordsThatOnTheRun()
    {
        var harness = Build();
        var stage = new PipelineStageDefinition { Name = "Build images" };
        // No online server, but a target IS configured: the run waits rather than failing.
        harness.Servers.ResolveEffectiveStageTarget(Arg.Any<PipelineStageDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(callInfo => callInfo.Arg<PipelineStageDefinition>());
        harness.Servers.ResolveServerForTargetAsync(
                Arg.Any<PipelineStageDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((Server?)null);
        harness.Repo.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([7]);

        await Dispatch(harness, stage, Pending("Build images"));

        await harness.Repo.Received(1).SetRunWaitingReasonAsync(
            RunId,
            Arg.Is<string>(reason =>
                reason.Contains("Build images") && reason.Contains("come back online")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AStageHeldByItsGroupLimit_NamesTheGroup()
    {
        var harness = Build();
        var stage = new PipelineStageDefinition
        {
            Name = "Test backend",
            Group = "Quality",
            Strategy = new PipelineDeploymentStrategy { MaxParallel = 1 }
        };
        // One stage of the group is already active, and the limit is one.
        harness.Repo.GetActiveStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(["Test backend"]);

        await Dispatch(harness, stage, Pending("Test backend"));

        await harness.Repo.Received(1).SetRunWaitingReasonAsync(
            RunId,
            Arg.Is<string>(reason => reason.Contains("group 'Quality'")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ArtifactCollectionInFlight_IsReportedAsTheWait()
    {
        var harness = Build(artifactCollection: true);

        await Dispatch(harness, new PipelineStageDefinition { Name = "Publish evidence" }, Pending("Publish evidence"));

        await harness.Repo.Received(1).SetRunWaitingReasonAsync(
            RunId,
            Arg.Is<string>(reason => reason.Contains("Artifact collection")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AStageThatDispatches_ClearsTheReason()
    {
        var harness = Build();
        var server = new Server { Id = 7, Name = "runner-1" };
        harness.Servers.ResolveEffectiveStageTarget(Arg.Any<PipelineStageDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(callInfo => callInfo.Arg<PipelineStageDefinition>());
        harness.Servers.ResolveServerForTargetAsync(
                Arg.Any<PipelineStageDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(server);
        harness.Dispatcher.CreateTasksForStageAsync(
                Arg.Any<int>(), Arg.Any<Server>(), Arg.Any<List<PipelineStepRun>>(),
                Arg.Any<PipelineStageDefinition>(), Arg.Any<Dictionary<string, string>>(),
                Arg.Any<List<string>>(), Arg.Any<bool>(), Arg.Any<HashSet<string>>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IPipelineChildRunLauncher>(), Arg.Any<CancellationToken>())
            .Returns(false);

        // The step is Assigned after dispatch, which is what tells the planner a task was created.
        await Dispatch(harness, new PipelineStageDefinition { Name = "Build images" },
            Pending("Build images", TaskExecutionStatus.Assigned));

        await harness.Repo.Received(1).SetRunWaitingReasonAsync(RunId, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NothingReadyBecauseAStageIsRunning_ClearsTheReason()
    {
        // Progress, not a wait: a throttle recorded a moment ago must not survive it.
        var harness = Build(anythingRunning: true);
        var stage = new PipelineStageDefinition { Name = "Gate unit tests", DependsOn = ["Test backend"] };

        await Dispatch(harness, stage, Pending("Gate unit tests"));

        await harness.Repo.Received(1).SetRunWaitingReasonAsync(RunId, null, Arg.Any<CancellationToken>());
    }
}
