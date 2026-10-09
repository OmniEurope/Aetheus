// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R2-117: aetheus-release-fast asked for the prod approval only when "Deploy blue-green"
/// became ready, after "Build images" and "Application payload", long after the launch. Every
/// approval-requiring environment a run declares is now asked when the run starts, before its first
/// stage (System:Prepare included) is dispatched; once given it is not asked again, and the Confirm
/// stage's own confirmation is still asked when Confirm is ready. The stage shapes mirror
/// .pipeline/aetheus-release-fast.yaml (not read here: pipeline files are not asserted on by tests).
/// </summary>
public sealed class PipelineLaunchEnvironmentApprovalTests
{
    private const int RunId = 2555;
    private const int ProdEnvironmentId = 4;

    private static readonly PipelineStageDefinition BuildImages = new()
    {
        Name = "Build images",
        Os = "linux",
        Steps = [new PipelineStepDefinition { Name = "Build production Docker images", Shell = "docker build ." }]
    };
    private static readonly PipelineStageDefinition DeployBlueGreen = new()
    {
        Name = "Deploy blue-green",
        Os = "linux",
        ExecutionRole = "deploy",
        DependsOn = ["Build images"],
        Steps = [new PipelineStepDefinition { Name = "Deploy production blue-green", Shell = "sh deploy.sh" }]
    };
    private static readonly PipelineStageDefinition Confirm = new()
    {
        Name = "Confirm",
        Os = "linux",
        Environment = "prod",
        ExecutionRole = "deploy",
        ApprovalTimeoutMinutes = 10,
        DependsOn = ["Deploy blue-green"],
        Steps = [new PipelineStepDefinition { Name = "Record the confirmation", Shell = "echo confirmed" }]
    };

    private static PipelineApproval ProdApproval(ApprovalStatus status) => new()
    {
        Id = 1,
        PipelineRunId = RunId,
        StageName = "Confirm",
        Scope = ApprovalScope.Environment,
        EnvironmentId = ProdEnvironmentId,
        Status = status
    };

    [Fact]
    public async Task AtLaunch_TheProdApprovalIsAsked_BeforeAnyStageIsDispatched()
    {
        var h = new Harness(approvals: []);

        await h.DispatchAsync(completed: [], PipelineRunService.SystemPrepareStage, "Build images", "Deploy blue-green", "Confirm");

        await h.Repo.Received(1).AddApprovalAsync(
            Arg.Is<PipelineApproval>(a => a.Scope == ApprovalScope.Environment
                && a.EnvironmentId == ProdEnvironmentId
                && a.TimeoutMinutes == null
                && a.StageName == "Confirm"),
            Arg.Any<CancellationToken>());
        await h.Repo.Received(1).TryTransitionPipelineRunStatusAsync(
            RunId, PipelineStatus.Running, PipelineStatus.WaitingForApproval, Arg.Any<CancellationToken>());
        await h.SystemTasks.DidNotReceive().CreateSystemTasksAsync(
            Arg.Any<int>(), Arg.Any<List<PipelineStepRun>>(), Arg.Any<Dictionary<string, string>>(),
            Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<List<string>>(), Arg.Any<CancellationToken>());
        await h.AssertNoStageDispatchedAsync();
    }

    [Fact]
    public async Task AtLaunch_APendingProdApproval_KeepsEveryStageWaiting_WithoutAskingTwice()
    {
        var h = new Harness(approvals: [ProdApproval(ApprovalStatus.Pending)]);

        await h.DispatchAsync(completed: [], PipelineRunService.SystemPrepareStage, "Build images", "Deploy blue-green", "Confirm");

        await h.Repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
        await h.SystemTasks.DidNotReceive().CreateSystemTasksAsync(
            Arg.Any<int>(), Arg.Any<List<PipelineStepRun>>(), Arg.Any<Dictionary<string, string>>(),
            Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<List<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnceProdIsApproved_TheRunStarts_AndTheBuildIsDispatched()
    {
        var h = new Harness(approvals: [ProdApproval(ApprovalStatus.Approved)]);

        // The resume pass: the workspace is prepared first.
        await h.DispatchAsync(completed: [], PipelineRunService.SystemPrepareStage, "Build images", "Deploy blue-green", "Confirm");
        await h.SystemTasks.Received(1).CreateSystemTasksAsync(
            RunId, Arg.Any<List<PipelineStepRun>>(), Arg.Any<Dictionary<string, string>>(),
            Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<List<string>>(), Arg.Any<CancellationToken>());

        // Prepare done: the build goes, and nothing is asked again.
        await h.DispatchAsync(completed: [PipelineRunService.SystemPrepareStage], "Build images", "Deploy blue-green", "Confirm");
        await h.StepDispatcher.Received(1).CreateTasksForStageAsync(
            RunId, Arg.Any<Server>(), Arg.Any<List<PipelineStepRun>>(),
            Arg.Is<PipelineStageDefinition>(stage => stage.Name == "Build images"),
            Arg.Any<Dictionary<string, string>>(), Arg.Any<List<string>>(), Arg.Any<bool>(), Arg.Any<HashSet<string>>(),
            Arg.Any<int?>(), Arg.Any<IPipelineChildRunLauncher>(), Arg.Any<CancellationToken>());

        // Build and payload done: the deployment is not held for an approval already given.
        await h.DispatchAsync(completed: [PipelineRunService.SystemPrepareStage, "Build images"], "Deploy blue-green", "Confirm");
        await h.StepDispatcher.Received(1).CreateTasksForStageAsync(
            RunId, Arg.Any<Server>(), Arg.Any<List<PipelineStepRun>>(),
            Arg.Is<PipelineStageDefinition>(stage => stage.Name == "Deploy blue-green"),
            Arg.Any<Dictionary<string, string>>(), Arg.Any<List<string>>(), Arg.Any<bool>(), Arg.Any<HashSet<string>>(),
            Arg.Any<int?>(), Arg.Any<IPipelineChildRunLauncher>(), Arg.Any<CancellationToken>());
        await h.Repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheConfirmStage_StillAsksItsOwnConfirmation_AfterTheLaunchApproval()
    {
        var h = new Harness(approvals: [ProdApproval(ApprovalStatus.Approved)]);

        await h.DispatchAsync(
            completed: [PipelineRunService.SystemPrepareStage, "Build images", "Deploy blue-green"], "Confirm");

        await h.Repo.Received(1).AddApprovalAsync(
            Arg.Is<PipelineApproval>(a => a.Scope == ApprovalScope.Pipeline && a.StageName == "Confirm" && a.TimeoutMinutes == 10),
            Arg.Any<CancellationToken>());
        await h.Repo.DidNotReceive().AddApprovalAsync(
            Arg.Is<PipelineApproval>(a => a.Scope == ApprovalScope.Environment), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARunWithoutAnApprovalRequiringEnvironment_StartsAtOnce()
    {
        var h = new Harness(approvals: [], prodRequiresApproval: false);

        await h.DispatchAsync(completed: [], PipelineRunService.SystemPrepareStage, "Build images", "Deploy blue-green", "Confirm");

        await h.Repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
        await h.SystemTasks.Received(1).CreateSystemTasksAsync(
            RunId, Arg.Any<List<PipelineStepRun>>(), Arg.Any<Dictionary<string, string>>(),
            Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<List<string>>(), Arg.Any<CancellationToken>());
    }

    private sealed class Harness
    {
        public IPipelineRepository Repo { get; } = Substitute.For<IPipelineRepository>();
        public IPipelineStepTaskDispatcher StepDispatcher { get; } = Substitute.For<IPipelineStepTaskDispatcher>();
        public IPipelineSystemTaskFactory SystemTasks { get; } = Substitute.For<IPipelineSystemTaskFactory>();
        private readonly PipelineStageDispatchPlanner _planner;

        public Harness(List<PipelineApproval> approvals, bool prodRequiresApproval = true)
        {
            var finalizer = Substitute.For<IPipelineRunFinalizer>();
            var environmentChecks = Substitute.For<IPipelineEnvironmentCheckGuard>();
            var servers = Substitute.For<IPipelineDispatchServerResolver>();

            Repo.HasActiveArtifactCollectionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
            Repo.HasAnyFailedStepInRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
            Repo.HasAnyRunningStepInRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
            Repo.GetActiveStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
            Repo.GetRunAffinityServerIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((int?)null);
            Repo.GetSuccessfulStepOutputsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
            Repo.GetApprovalsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(approvals);
            Repo.GetRunProjectIdAsync(RunId, Arg.Any<CancellationToken>()).Returns(7);
            Repo.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns([]);
            Repo.FindEnvironmentByNameAsync("prod", 7, Arg.Any<CancellationToken>())
                .Returns(new Environment
                {
                    Id = ProdEnvironmentId,
                    Name = "prod",
                    ProjectId = 7,
                    RequireApproval = prodRequiresApproval,
                    ApprovalTimeoutMinutes = 1440
                });
            Repo.TryTransitionPipelineRunStatusAsync(
                Arg.Any<int>(), PipelineStatus.Running, PipelineStatus.WaitingForApproval, Arg.Any<CancellationToken>())
                .Returns(true);
            environmentChecks.CheckEnvironmentChecksAsync(
                    Arg.Any<PipelineStageDefinition>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(true);
            finalizer.IsRunActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
            SystemTasks.CreateSystemTasksAsync(
                    Arg.Any<int>(), Arg.Any<List<PipelineStepRun>>(), Arg.Any<Dictionary<string, string>>(),
                    Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
                .Returns(true);
            servers.ResolveEffectiveStageTarget(Arg.Any<PipelineStageDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>())
                .Returns(callInfo => callInfo.Arg<PipelineStageDefinition>());
            // A runner and a deployment target are online: without a gate every ready stage WOULD go.
            servers.ResolveServerForTargetAsync(
                    Arg.Any<PipelineStageDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                    Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new Server { Id = 5, Name = "prod-host", DeploymentTargetAvailable = true });
            servers.CheckDeploymentPolicy(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Server>()).Returns((string?)null);

            _planner = new PipelineStageDispatchPlanner(
                Repo, servers, environmentChecks, finalizer, SystemTasks, StepDispatcher,
                Substitute.For<IHubContext<PipelineHub>>(),
                Substitute.For<IDomainEventDispatcher>(),
                new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 13, 0, 0, TimeSpan.Zero)),
                Substitute.For<ILogger<PipelineStageDispatchPlanner>>());
        }

        /// <summary>One dispatch pass over a run where <paramref name="completed"/> stages are done and
        /// <paramref name="pending"/> stages each still hold one pending step.</summary>
        public async Task DispatchAsync(List<string> completed, params string[] pending)
        {
            Repo.GetCompletedStageNamesAsync(RunId, Arg.Any<CancellationToken>()).Returns(completed);
            Repo.GetTerminalStageNamesAsync(RunId, Arg.Any<CancellationToken>()).Returns(completed);
            await _planner.CreateTasksForNextStageAsync(
                RunId,
                new PipelineYamlDefinition { Name = "aetheus-release-fast", Stages = [BuildImages, DeployBlueGreen, Confirm] },
                [],
                [.. pending.Select(stage => new PipelineStepRun { StageName = stage, Status = TaskExecutionStatus.Pending })],
                null,
                [],
                Substitute.For<IPipelineChildRunLauncher>(),
                CancellationToken.None);
        }

        public Task AssertNoStageDispatchedAsync() =>
            StepDispatcher.DidNotReceive().CreateTasksForStageAsync(
                Arg.Any<int>(), Arg.Any<Server>(), Arg.Any<List<PipelineStepRun>>(), Arg.Any<PipelineStageDefinition>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<List<string>>(), Arg.Any<bool>(), Arg.Any<HashSet<string>>(),
                Arg.Any<int?>(), Arg.Any<IPipelineChildRunLauncher>(), Arg.Any<CancellationToken>());
    }
}
