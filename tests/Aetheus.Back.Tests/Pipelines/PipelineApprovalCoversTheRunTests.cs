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

/// <summary>Environment approval applies to the whole run, while a different environment requires a
/// separate authorization.</summary>
public class PipelineApprovalCoversTheRunTests
{
    private const int RunId = 7312;
    private const int ProdEnvironmentId = 1;
    private const int StagingEnvironmentId = 2;

    [Fact]
    public async Task ASecondProdStage_DoesNotAskAgainOnceTheRunIsApprovedForProd()
    {
        var repo = Harness(out var planner, approvals:
        [
            new PipelineApproval
            {
                Id = 1,
                PipelineRunId = RunId,
                StageName = "Restore candidate",
                EnvironmentId = ProdEnvironmentId,
                Status = ApprovalStatus.Approved
            }
        ]);

        await DispatchStageAsync(planner, "Deploy blue-green", "prod");

        await repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AStillPendingApprovalOnAnotherStage_HoldsTheStageWithoutASecondPrompt()
    {
        // Pending approval neither authorizes dispatch nor requires a duplicate prompt.
        var repo = Harness(out var planner, approvals:
        [
            new PipelineApproval
            {
                Id = 1,
                PipelineRunId = RunId,
                StageName = "Restore candidate",
                EnvironmentId = ProdEnvironmentId,
                Status = ApprovalStatus.Pending
            }
        ]);

        await DispatchStageAsync(planner, "Deploy blue-green", "prod");

        await repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApprovingStaging_DoesNotAuthoriseProd()
    {
        var repo = Harness(out var planner, approvals:
        [
            new PipelineApproval
            {
                Id = 1,
                PipelineRunId = RunId,
                StageName = "Deploy staging",
                EnvironmentId = StagingEnvironmentId,
                Status = ApprovalStatus.Approved
            }
        ]);

        await DispatchStageAsync(planner, "Deploy blue-green", "prod");

        await repo.Received(1).AddApprovalAsync(
            Arg.Is<PipelineApproval>(a => a.EnvironmentId == ProdEnvironmentId),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Stage confirmation is separate from environment authorization and expires on its
    /// own delay.</summary>
    [Fact]
    public async Task AStageWithItsOwnDelay_AsksAgain_WithThatDelay()
    {
        var repo = Harness(out var planner, approvals:
        [
            new PipelineApproval
            {
                Id = 1,
                PipelineRunId = RunId,
                StageName = "Restore candidate",
                EnvironmentId = ProdEnvironmentId,
                Status = ApprovalStatus.Approved
            }
        ]);

        await DispatchStageAsync(planner, "Confirm", "prod", approvalTimeoutMinutes: 10);

        await repo.Received(1).AddApprovalAsync(
            Arg.Is<PipelineApproval>(a => a.StageName == "Confirm" && a.TimeoutMinutes == 10 && a.EnvironmentId == ProdEnvironmentId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AStageWithItsOwnDelay_AsksEvenWhereTheEnvironmentRequiresNoApproval()
    {
        var repo = Harness(out var planner, approvals: [], prodRequiresApproval: false);

        await DispatchStageAsync(planner, "Confirm", "prod", approvalTimeoutMinutes: 10);
        await DispatchStageAsync(planner, "Deploy blue-green", "prod");

        await repo.Received(1).AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
        await repo.Received(1).AddApprovalAsync(Arg.Is<PipelineApproval>(a => a.StageName == "Confirm"), Arg.Any<CancellationToken>());
    }

    private static async Task DispatchStageAsync(
        PipelineStageDispatchPlanner planner, string stageName, string environment, int? approvalTimeoutMinutes = null)
    {
        var stage = new PipelineStageDefinition { Name = stageName, Environment = environment, ApprovalTimeoutMinutes = approvalTimeoutMinutes };
        await planner.CreateTasksForNextStageAsync(
            RunId,
            new PipelineYamlDefinition { Name = "aetheus-deploy-prod", Stages = [stage] },
            [],
            [new PipelineStepRun { StageName = stageName, Status = TaskExecutionStatus.Pending }],
            null,
            [],
            Substitute.For<IPipelineChildRunLauncher>(),
            CancellationToken.None);
    }

    private static IPipelineRepository Harness(
        out PipelineStageDispatchPlanner planner, List<PipelineApproval> approvals, bool prodRequiresApproval = true)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero));
        var repo = Substitute.For<IPipelineRepository>();
        var finalizer = Substitute.For<IPipelineRunFinalizer>();
        var environmentChecks = Substitute.For<IPipelineEnvironmentCheckGuard>();
        var servers = Substitute.For<IPipelineDispatchServerResolver>();

        repo.HasActiveArtifactCollectionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        repo.GetCompletedStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetTerminalStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.HasAnyFailedStepInRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        repo.HasAnyRunningStepInRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        repo.GetActiveStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetRunAffinityServerIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((int?)null);
        repo.GetSuccessfulStepOutputsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetApprovalsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(approvals);
        // Left unstubbed this returns null, and the selector does .Count on it: the resulting NRE
        // would masquerade as a gate failure in the one test where dispatch runs past the approval.
        repo.FindCandidateTargetServerIdsAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
            Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.FindEnvironmentByNameAsync("prod", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Aetheus.Back.Data.Entities.Environment
            {
                Id = ProdEnvironmentId,
                Name = "prod",
                RequireApproval = prodRequiresApproval,
                ApprovalTimeoutMinutes = 1440
            });
        repo.TryTransitionPipelineRunStatusAsync(
            Arg.Any<int>(), PipelineStatus.Running, PipelineStatus.WaitingForApproval, Arg.Any<CancellationToken>())
            .Returns(true);
        environmentChecks.CheckEnvironmentChecksAsync(Arg.Any<PipelineStageDefinition>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        finalizer.IsRunActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        servers.CheckDeploymentPolicy(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Server>()).Returns((string?)null);
        servers.ResolveEffectiveStageTarget(Arg.Any<PipelineStageDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(callInfo => callInfo.Arg<PipelineStageDefinition>());

        planner = new PipelineStageDispatchPlanner(
            repo, servers, environmentChecks, finalizer,
            Substitute.For<IPipelineSystemTaskFactory>(),
            Substitute.For<IPipelineStepTaskDispatcher>(),
            Substitute.For<IHubContext<PipelineHub>>(),
            Substitute.For<IDomainEventDispatcher>(),
            time,
            Substitute.For<ILogger<PipelineStageDispatchPlanner>>());
        return repo;
    }
}
