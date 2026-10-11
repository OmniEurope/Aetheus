// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// R-369: aetheus-release-fast deploys to production from a stage that declares no environment
/// ("Deploy blue-green", execution_role: deploy). Its only environment is on the Confirm stage that
/// FOLLOWS the go-live, and the approval gate only looked at a stage's own environment: a prod that
/// requires approval was therefore deployed first and asked afterwards. A deployment stage with no
/// environment of its own now needs every approval-requiring environment the run targets to be
/// approved for the run before it is dispatched. The stage shapes below mirror
/// .pipeline/aetheus-release-fast.yaml (not read here: pipeline files are not asserted on by tests).
/// </summary>
public class PipelineDeploymentStageApprovalTests
{
    private const int RunId = 9369;
    private const int ProdEnvironmentId = 1;
    private const int StagingEnvironmentId = 2;

    private static readonly PipelineStageDefinition BuildImages = new() { Name = "Build images", Os = "linux" };
    private static readonly PipelineStageDefinition DeployBlueGreen = new()
    {
        Name = "Deploy blue-green",
        Os = "linux",
        ExecutionRole = "deploy"
    };
    private static readonly PipelineStageDefinition Confirm = new()
    {
        Name = "Confirm",
        Os = "linux",
        Environment = "prod",
        ExecutionRole = "deploy",
        ApprovalTimeoutMinutes = 10
    };

    private static List<PipelineStageDefinition> ReleaseFastStages => [BuildImages, DeployBlueGreen, Confirm];

    [Fact]
    public async Task TheFirstDeploymentStage_AsksTheProdApproval_BeforeItIsDispatched()
    {
        var h = new Harness(approvals: []);

        await h.DispatchAsync(DeployBlueGreen, ReleaseFastStages);

        await h.Repo.Received(1).AddApprovalAsync(
            Arg.Is<PipelineApproval>(a => a.StageName == "Deploy blue-green"
                && a.EnvironmentId == ProdEnvironmentId
                && a.TimeoutMinutes == null),
            Arg.Any<CancellationToken>());
        await h.StepDispatcher.DidNotReceive().CreateTasksForStageAsync(
            Arg.Any<int>(), Arg.Any<Server>(), Arg.Any<List<PipelineStepRun>>(), Arg.Any<PipelineStageDefinition>(),
            Arg.Any<Dictionary<string, string>>(), Arg.Any<List<string>>(), Arg.Any<bool>(), Arg.Any<HashSet<string>>(),
            Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IPipelineChildRunLauncher>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnceProdIsApprovedForTheRun_TheDeploymentStageIsNotAskedAgain()
    {
        var h = new Harness(approvals:
        [
            new PipelineApproval
            {
                Id = 1, PipelineRunId = RunId, StageName = "Deploy blue-green",
                EnvironmentId = ProdEnvironmentId, Status = ApprovalStatus.Approved
            }
        ]);

        await h.DispatchAsync(DeployBlueGreen, ReleaseFastStages);

        await h.Repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APendingProdApproval_DoesNotLetTheDeploymentStageRun()
    {
        var h = new Harness(approvals:
        [
            new PipelineApproval
            {
                Id = 1, PipelineRunId = RunId, StageName = "Deploy blue-green",
                EnvironmentId = ProdEnvironmentId, Status = ApprovalStatus.Pending
            }
        ]);

        await h.DispatchAsync(DeployBlueGreen, ReleaseFastStages);

        await h.StepDispatcher.DidNotReceive().CreateTasksForStageAsync(
            Arg.Any<int>(), Arg.Any<Server>(), Arg.Any<List<PipelineStepRun>>(), Arg.Any<PipelineStageDefinition>(),
            Arg.Any<Dictionary<string, string>>(), Arg.Any<List<string>>(), Arg.Any<bool>(), Arg.Any<HashSet<string>>(),
            Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IPipelineChildRunLauncher>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhereProdRequiresNoApproval_NothingIsAskedBeforeTheDeployment()
    {
        var h = new Harness(approvals: [], prodRequiresApproval: false);

        await h.DispatchAsync(DeployBlueGreen, ReleaseFastStages);

        await h.Repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ABuildStage_IsNotADeploymentAndIsNotGated()
    {
        var h = new Harness(approvals: []);

        await h.DispatchAsync(BuildImages, ReleaseFastStages);

        await h.Repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ATypedDeployStepWithoutEnvironment_IsGatedToo()
    {
        var h = new Harness(approvals: []);
        var deployStep = new PipelineStageDefinition
        {
            Name = "Ship",
            Steps = [new PipelineStepDefinition { Name = "Deploy", Type = "deploy" }]
        };

        await h.DispatchAsync(deployStep, [deployStep, Confirm]);

        await h.Repo.Received(1).AddApprovalAsync(
            Arg.Is<PipelineApproval>(a => a.StageName == "Ship" && a.EnvironmentId == ProdEnvironmentId),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A deployment stage that names its own environment keeps the per-environment rule: a
    /// staging deployment is not held for the prod approval a later stage will ask for.</summary>
    [Fact]
    public async Task ADeploymentStageWithItsOwnEnvironment_KeepsItsOwnRule()
    {
        var h = new Harness(approvals: []);
        var staging = new PipelineStageDefinition { Name = "Deploy staging", Environment = "staging", ExecutionRole = "deploy" };

        await h.DispatchAsync(staging, [staging, Confirm]);

        await h.Repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Both approvals stay: the environment's before the go-live, the Confirm window after it.</summary>
    [Fact]
    public async Task TheConfirmWindow_StillAsksAfterTheEnvironmentApproval()
    {
        var h = new Harness(approvals:
        [
            new PipelineApproval
            {
                Id = 1, PipelineRunId = RunId, StageName = "Deploy blue-green",
                EnvironmentId = ProdEnvironmentId, Status = ApprovalStatus.Approved
            }
        ]);

        await h.DispatchAsync(Confirm, ReleaseFastStages);

        await h.Repo.Received(1).AddApprovalAsync(
            Arg.Is<PipelineApproval>(a => a.StageName == "Confirm" && a.TimeoutMinutes == 10),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A refused or expired environment approval ends the run: it is not a confirmation window,
    /// so no stage is failed into a rollback and nothing after it is dispatched.</summary>
    [Theory]
    [InlineData(ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.TimedOut)]
    public async Task ARefusedOrExpiredProdApproval_FailsTheRunWithoutDeploying(ApprovalStatus decision)
    {
        var repo = Substitute.For<IPipelineRepository>();
        var scheduler = Substitute.For<IPipelineRunScheduler>();
        repo.GetApprovalsAsync(RunId, Arg.Any<CancellationToken>()).Returns(
        [
            new PipelineApproval
            {
                Id = 1, PipelineRunId = RunId, StageName = "Deploy blue-green",
                EnvironmentId = ProdEnvironmentId, Status = decision,
                ResolvedAt = new DateTime(2026, 9, 26, 9, 5, 0, DateTimeKind.Utc)
            }
        ]);
        repo.TryTransitionPipelineRunStatusAsync(
            RunId, PipelineStatus.WaitingForApproval, PipelineStatus.Failed, Arg.Any<CancellationToken>()).Returns(true);
        var control = new PipelineRunControlService(
            repo, Substitute.For<IHubContext<PipelineHub>>(), Substitute.For<IPipelineRunDefinitionParser>(), Substitute.For<Aetheus.Back.Components.Audit.IAuditService>());

        var outcome = await control.ApplyRefusalAsync(
            RunId, scheduler, Substitute.For<IPipelineChildRunLauncher>(), CancellationToken.None);

        Assert.Equal(PipelineStatus.Failed, outcome);
        await scheduler.DidNotReceive().FailUnconfirmedStageAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<IPipelineChildRunLauncher>(), Arg.Any<CancellationToken>());
        await scheduler.DidNotReceive().DispatchNextStageWithScopedSecretsAsync(
            Arg.Any<PipelineRun>(), Arg.Any<PipelineYamlDefinition>(), Arg.Any<IPipelineChildRunLauncher>(), Arg.Any<CancellationToken>());
        await scheduler.DidNotReceive().DispatchNextStageSerializedAsync(
            Arg.Any<PipelineRun>(), Arg.Any<PipelineYamlDefinition>(), Arg.Any<IPipelineChildRunLauncher>(), Arg.Any<CancellationToken>());
    }

    private sealed class Harness
    {
        public IPipelineRepository Repo { get; } = Substitute.For<IPipelineRepository>();
        public IPipelineStepTaskDispatcher StepDispatcher { get; } = Substitute.For<IPipelineStepTaskDispatcher>();
        private readonly PipelineStageDispatchPlanner _planner;

        public Harness(List<PipelineApproval> approvals, bool prodRequiresApproval = true)
        {
            var finalizer = Substitute.For<IPipelineRunFinalizer>();
            var environmentChecks = Substitute.For<IPipelineEnvironmentCheckGuard>();
            var servers = Substitute.For<IPipelineDispatchServerResolver>();

            Repo.HasActiveArtifactCollectionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
            // The workspace is prepared: a stage with steps waits for System:Prepare otherwise.
            Repo.GetCompletedStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns([PipelineRunService.SystemPrepareStage]);
            Repo.GetTerminalStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns([PipelineRunService.SystemPrepareStage]);
            Repo.HasAnyFailedStepInRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
            Repo.HasAnyRunningStepInRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
            Repo.GetActiveStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
            Repo.GetRunAffinityServerIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((int?)null);
            Repo.GetSuccessfulStepOutputsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
            Repo.GetApprovalsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(approvals);
            Repo.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns([]);
            Repo.FindEnvironmentByNameAsync("prod", Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(new Aetheus.Back.Data.Entities.Environment
                {
                    Id = ProdEnvironmentId,
                    Name = "prod",
                    RequireApproval = prodRequiresApproval,
                    ApprovalTimeoutMinutes = 1440
                });
            Repo.FindEnvironmentByNameAsync("staging", Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(new Aetheus.Back.Data.Entities.Environment
                {
                    Id = StagingEnvironmentId,
                    Name = "staging",
                    RequireApproval = false,
                    ApprovalTimeoutMinutes = 1440
                });
            Repo.TryTransitionPipelineRunStatusAsync(
                Arg.Any<int>(), PipelineStatus.Running, PipelineStatus.WaitingForApproval, Arg.Any<CancellationToken>())
                .Returns(true);
            environmentChecks.CheckEnvironmentChecksAsync(Arg.Any<PipelineStageDefinition>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(true);
            finalizer.IsRunActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
            servers.ResolveEffectiveStageTarget(Arg.Any<PipelineStageDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>())
                .Returns(callInfo => callInfo.Arg<PipelineStageDefinition>());
            // A runner is online: without the gate, the deployment stage WOULD be dispatched.
            servers.ResolveServerForTargetAsync(
                    Arg.Any<PipelineStageDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                    Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(new Server { Id = 5, Name = "prod-host", DeploymentTargetAvailable = true });
            servers.CheckDeploymentPolicy(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Server>()).Returns((string?)null);

            _planner = new PipelineStageDispatchPlanner(
                Repo, servers, environmentChecks, finalizer,
                Substitute.For<IPipelineSystemTaskFactory>(),
                StepDispatcher,
                Substitute.For<IHubContext<PipelineHub>>(),
                Substitute.For<IDomainEventDispatcher>(),
                new FakeTimeProvider(new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.Zero)),
                Substitute.For<ILogger<PipelineStageDispatchPlanner>>());
        }

        /// <summary>Dispatches <paramref name="stage"/> as the only ready stage of a run whose
        /// definition holds <paramref name="runStages"/> (dependencies are not what is tested here).</summary>
        public Task DispatchAsync(PipelineStageDefinition stage, List<PipelineStageDefinition> runStages) =>
            _planner.CreateTasksForNextStageAsync(
                RunId,
                new PipelineYamlDefinition { Name = "aetheus-release-fast", Stages = runStages },
                [],
                [new PipelineStepRun { StageName = stage.Name, Status = TaskExecutionStatus.Pending }],
                null,
                [],
                Substitute.For<IPipelineChildRunLauncher>(),
                CancellationToken.None);
    }
}
