// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Verifies that separately read stage and task states cannot falsely declare a deadlock
/// when a step completes between the queries.</summary>
public class PipelineDeadlockRecheckTests
{
    private static PipelineYamlDefinition Definition() => new()
    {
        Name = "qa",
        Stages =
        [
            new PipelineStageDefinition { Name = "RestoreCurrentRuntime" },
            new PipelineStageDefinition { Name = "RestorePreviousAgentContract" },
            new PipelineStageDefinition
            {
                Name = "PrepareCompatibilityGate",
                DependsOn = ["RestoreCurrentRuntime", "RestorePreviousAgentContract"]
            }
        ]
    };

    private static (PipelineStageDispatchPlanner Planner, IPipelineRepository Repo, IPipelineRunFinalizer Finalizer)
        Build(Func<List<string>> completedStages, Func<bool>? running = null)
    {
        var repo = Substitute.For<IPipelineRepository>();
        var finalizer = Substitute.For<IPipelineRunFinalizer>();
        repo.HasActiveArtifactCollectionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        repo.GetCompletedStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(completedStages()));
        repo.GetTerminalStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(completedStages()));
        repo.HasAnyFailedStepInRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        // The decisive part of the window: the last step is already gone from the running set.
        repo.HasAnyRunningStepInRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(running?.Invoke() ?? false));
        finalizer.IsRunActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        var planner = new PipelineStageDispatchPlanner(
            repo,
            Substitute.For<IPipelineDispatchServerResolver>(),
            Substitute.For<IPipelineEnvironmentCheckGuard>(),
            finalizer,
            Substitute.For<IPipelineSystemTaskFactory>(),
            Substitute.For<IPipelineStepTaskDispatcher>(),
            Substitute.For<IHubContext<PipelineHub>>(),
            Substitute.For<IDomainEventDispatcher>(),
            TimeProvider.System,
            Substitute.For<ILogger<PipelineStageDispatchPlanner>>());
        return (planner, repo, finalizer);
    }

    private static List<PipelineStepRun> Pending() =>
    [
        new() { StageName = "PrepareCompatibilityGate", Status = TaskExecutionStatus.Pending }
    ];

    [Fact]
    public async Task StepCompletingBetweenTheTwoReads_DoesNotFailTheRun()
    {
        var call = 0;
        // First read misses the stage that just finished; every later read sees it.
        var (planner, _, finalizer) = Build(() => ++call == 1
            ? ["RestoreCurrentRuntime"]
            : ["RestoreCurrentRuntime", "RestorePreviousAgentContract"]);

        await planner.CreateTasksForNextStageAsync(
            1989, Definition(), [], Pending(), null, [],
            Substitute.For<IPipelineChildRunLauncher>(), CancellationToken.None);

        await finalizer.DidNotReceive().FailRunWithUnmatchedStagesAsync(
            Arg.Any<int>(), Arg.Any<List<string>>(), Arg.Any<List<PipelineStepRun>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StepStillRunningOnTheFirstRecheckRead_DoesNotFailTheRun()
    {
        // The window the re-read has to survive: the step is still running when the running flag is
        // read, and only completes before the stages are read. Reading the stages first would miss the
        // completion and see nothing running, which is exactly how run 1989 died.
        var runningCall = 0;
        var completedCall = 0;
        var (planner, _, finalizer) = Build(
            () => ++completedCall == 1
                ? ["RestoreCurrentRuntime"]
                : ["RestoreCurrentRuntime", "RestorePreviousAgentContract"],
            () => ++runningCall == 1);

        await planner.CreateTasksForNextStageAsync(
            1989, Definition(), [], Pending(), null, [],
            Substitute.For<IPipelineChildRunLauncher>(), CancellationToken.None);

        await finalizer.DidNotReceive().FailRunWithUnmatchedStagesAsync(
            Arg.Any<int>(), Arg.Any<List<string>>(), Arg.Any<List<PipelineStepRun>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenuineDeadlock_StillFailsTheRun()
    {
        // The dependency never completes, so the second read looks like the first.
        var (planner, _, finalizer) = Build(() => ["RestoreCurrentRuntime"]);

        await planner.CreateTasksForNextStageAsync(
            1989, Definition(), [], Pending(), null, [],
            Substitute.For<IPipelineChildRunLauncher>(), CancellationToken.None);

        await finalizer.Received(1).FailRunWithUnmatchedStagesAsync(
            Arg.Any<int>(), Arg.Any<List<string>>(), Arg.Any<List<PipelineStepRun>>(),
            Arg.Any<CancellationToken>());
    }
}
