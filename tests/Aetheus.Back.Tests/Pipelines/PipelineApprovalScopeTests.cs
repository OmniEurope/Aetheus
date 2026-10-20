// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary> A pipeline asks for its own approval (a stage declaring
/// <c>approval_timeout_minutes</c>), with or without an environment, and that approval is told
/// apart from the environment's. When both apply they are two decisions: the pipeline's first, then
/// the environment's, and approving one never stands for the other. </summary>
public class PipelineApprovalScopeTests
{
    private const int RunId = 9370;
    private const int ProdEnvironmentId = 1;

    private static readonly PipelineStageDefinition GateWithoutEnvironment = new()
    {
        Name = "Approve the run",
        ApprovalTimeoutMinutes = 30
    };
    private static readonly PipelineStageDefinition ConfirmProd = new()
    {
        Name = "Confirm",
        Environment = "prod",
        ApprovalTimeoutMinutes = 10
    };

    [Fact]
    public async Task AStageWithoutEnvironment_AsksThePipelineApproval_WithItsOwnDelay()
    {
        var (gate, repo) = Harness(approvals: []);

        var held = await gate.CheckAndCreateApprovalAsync(
            RunId, null, GateWithoutEnvironment.Name, GateWithoutEnvironment, [GateWithoutEnvironment], CancellationToken.None);

        Assert.True(held);
        await repo.Received(1).AddApprovalAsync(
            Arg.Is<PipelineApproval>(a => a.Scope == ApprovalScope.Pipeline
                && a.EnvironmentId == null
                && a.TimeoutMinutes == 30
                && a.StageName == "Approve the run"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhereBothApply_ThePipelineApprovalIsAskedFirst()
    {
        var (gate, repo) = Harness(approvals: []);

        var held = await gate.CheckAndCreateApprovalAsync(RunId, null, ConfirmProd.Name, ConfirmProd, [ConfirmProd], CancellationToken.None);

        Assert.True(held);
        await repo.Received(1).AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
        await repo.Received(1).AddApprovalAsync(
            Arg.Is<PipelineApproval>(a => a.Scope == ApprovalScope.Pipeline && a.EnvironmentId == ProdEnvironmentId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnApprovedPipelineApproval_DoesNotStandForTheEnvironment()
    {
        var (gate, repo) = Harness(approvals: [Approval(ApprovalScope.Pipeline, "Confirm", ApprovalStatus.Approved, timeout: 10)]);

        var held = await gate.CheckAndCreateApprovalAsync(RunId, null, ConfirmProd.Name, ConfirmProd, [ConfirmProd], CancellationToken.None);

        Assert.True(held);
        await repo.Received(1).AddApprovalAsync(
            Arg.Is<PipelineApproval>(a => a.Scope == ApprovalScope.Environment
                && a.EnvironmentId == ProdEnvironmentId
                && a.TimeoutMinutes == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnceBothAreApproved_TheStageIsNotHeld()
    {
        var (gate, repo) = Harness(approvals:
        [
            Approval(ApprovalScope.Environment, "Deploy", ApprovalStatus.Approved),
            Approval(ApprovalScope.Pipeline, "Confirm", ApprovalStatus.Approved, timeout: 10)
        ]);

        var held = await gate.CheckAndCreateApprovalAsync(RunId, null, ConfirmProd.Name, ConfirmProd, [ConfirmProd], CancellationToken.None);

        Assert.False(held);
        await repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnApprovedEnvironment_DoesNotStandForThePipelineApproval()
    {
        var (gate, repo) = Harness(approvals: [Approval(ApprovalScope.Environment, "Deploy", ApprovalStatus.Approved)]);

        var held = await gate.CheckAndCreateApprovalAsync(RunId, null, ConfirmProd.Name, ConfirmProd, [ConfirmProd], CancellationToken.None);

        Assert.True(held);
        await repo.Received(1).AddApprovalAsync(
            Arg.Is<PipelineApproval>(a => a.Scope == ApprovalScope.Pipeline && a.StageName == "Confirm"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ApprovalStatus.Pending)]
    [InlineData(ApprovalStatus.Rejected)]
    public async Task APipelineApprovalNotGiven_HoldsTheStageWithoutAskingAgain(ApprovalStatus status)
    {
        var (gate, repo) = Harness(approvals: [Approval(ApprovalScope.Pipeline, "Approve the run", status, timeout: 30, environmentId: null)]);

        var held = await gate.CheckAndCreateApprovalAsync(
            RunId, null, GateWithoutEnvironment.Name, GateWithoutEnvironment, [GateWithoutEnvironment], CancellationToken.None);

        Assert.True(held);
        await repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APendingEnvironmentApproval_HoldsTheStageWithoutASecondPrompt()
    {
        var (gate, repo) = Harness(approvals:
        [
            Approval(ApprovalScope.Pipeline, "Confirm", ApprovalStatus.Approved, timeout: 10),
            Approval(ApprovalScope.Environment, "Confirm", ApprovalStatus.Pending)
        ]);

        var held = await gate.CheckAndCreateApprovalAsync(RunId, null, ConfirmProd.Name, ConfirmProd, [ConfirmProd], CancellationToken.None);

        Assert.True(held);
        await repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(0, false)]
    [InlineData(1441, false)]
    public void TheValidator_AcceptsAPipelineApprovalWithoutEnvironment_WithinADay(int minutes, bool valid)
    {
        var definition = new PipelineYamlDefinition
        {
            Name = "gated",
            Stages =
            [
                new PipelineStageDefinition
                {
                    Name = "Approve the run",
                    Agent = "linux-01",
                    ApprovalTimeoutMinutes = minutes,
                    Steps = [new PipelineStepDefinition { Name = "noop", Shell = "true" }]
                }
            ]
        };
        var errors = new List<string>();

        PipelineDefinitionValidator.ValidateStages(definition, errors, []);

        Assert.Equal(valid, !errors.Any(error => error.Contains("approval_timeout_minutes", StringComparison.Ordinal)));
    }

    private static PipelineApproval Approval(
        ApprovalScope scope, string stageName, ApprovalStatus status, int? timeout = null, int? environmentId = ProdEnvironmentId) => new()
        {
            PipelineRunId = RunId,
            Scope = scope,
            StageName = stageName,
            EnvironmentId = environmentId,
            TimeoutMinutes = timeout,
            Status = status
        };

    private static (PipelineStageApprovalGate Gate, IPipelineRepository Repo) Harness(List<PipelineApproval> approvals)
    {
        var repo = Substitute.For<IPipelineRepository>();
        repo.GetApprovalsAsync(RunId, Arg.Any<CancellationToken>()).Returns(approvals);
        repo.FindEnvironmentByNameAsync("prod", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Aetheus.Back.Data.Entities.Environment
            {
                Id = ProdEnvironmentId,
                Name = "prod",
                RequireApproval = true,
                ApprovalTimeoutMinutes = 1440
            });
        repo.TryTransitionPipelineRunStatusAsync(
                RunId, PipelineStatus.Running, PipelineStatus.WaitingForApproval, Arg.Any<CancellationToken>())
            .Returns(true);
        var gate = new PipelineStageApprovalGate(
            repo,
            Substitute.For<IHubContext<PipelineHub>>(),
            Substitute.For<IDomainEventDispatcher>(),
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.Zero)));
        return (gate, repo);
    }
}
