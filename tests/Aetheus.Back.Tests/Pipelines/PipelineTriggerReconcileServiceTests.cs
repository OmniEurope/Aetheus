// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Guards the orchestration self-heal (the fix for stuck run #49): pass 1 mirrors a terminal child onto a
/// wedged <c>type: trigger</c> step and re-enters the stage machine; pass 2 force-fails a run wedged in
/// Running with no in-flight work. Previously this service had no test coverage at all.
/// </summary>
public class PipelineTriggerReconcileServiceTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IPipelineRunService _runService = Substitute.For<IPipelineRunService>();
    private readonly IPipelineApprovalService _approvalService = Substitute.For<IPipelineApprovalService>();
    private readonly PipelineTriggerReconcileService _sut;

    public PipelineTriggerReconcileServiceTests()
    {
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IPipelineRepository)).Returns(_repo);
        sp.GetService(typeof(IPipelineRunService)).Returns(_runService);
        sp.GetService(typeof(IPipelineApprovalService)).Returns(_approvalService);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        // Default both sweeps to "nothing to do"; each test overrides the pass it exercises.
        _repo.GetStuckRunningTriggerStepsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _repo.GetStalledSchedulableRunIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _repo.GetStalledCancellationRunIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _repo.GetStuckRunningRunIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _repo.GetExpiredPendingApprovalIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _repo.GetStrandedApprovalRunsAsync(Arg.Any<CancellationToken>())
            .Returns([]);

        _sut = new PipelineTriggerReconcileService(
            scopeFactory,
            NullLogger<PipelineTriggerReconcileService>.Instance,
            Options.Create(new BackgroundServicesOptions()),
            TimeProvider.System);
    }

    private static PipelineStepRun TriggerStep(int childRunId) => new()
    {
        Id = 5,
        PipelineRunId = 1,
        StageName = "orchestrate",
        StepName = "trigger-child",
        Status = TaskExecutionStatus.Running,
        TriggeredRunId = childRunId,
        StartedAt = DateTime.UtcNow.AddMinutes(-10)
    };

    [Fact]
    public async Task Pass1_ChildSucceeded_ResolvesStepToSuccessAndAdvances()
    {
        var step = TriggerStep(99);
        _repo.GetStuckRunningTriggerStepsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([step]);
        _repo.GetRunStatusesByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineStatus> { [99] = PipelineStatus.Success });
        _repo.GetSuccessfulStepOutputsAsync(99, Arg.Any<CancellationToken>()).Returns(
            new List<StepOutputProjection>
            {
                new("CI", "Build", "{\"CANDIDATE_VERSION\":\"c-source-123\"}")
            });
        _runService.ResolveCompletedTriggerStepAsync(
                step,
                PipelineStatus.Success,
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        await _runService.Received(1).ResolveCompletedTriggerStepAsync(
            step,
            PipelineStatus.Success,
            Arg.Is<IReadOnlyDictionary<string, string>>(outputs =>
                outputs["CANDIDATE_VERSION"] == "c-source-123"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pass1_ChildFailed_ResolvesStepToFailed()
    {
        var step = TriggerStep(99);
        _repo.GetStuckRunningTriggerStepsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([step]);
        _repo.GetRunStatusesByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineStatus> { [99] = PipelineStatus.Failed });

        await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        await _runService.Received(1).ResolveCompletedTriggerStepAsync(
            step,
            PipelineStatus.Failed,
            Arg.Is<IReadOnlyDictionary<string, string>>(outputs => outputs.Count == 0),
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().GetSuccessfulStepOutputsAsync(99, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pass1_ChildRunMissing_FailsStepRatherThanHang()
    {
        var step = TriggerStep(99);
        _repo.GetStuckRunningTriggerStepsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([step]);
        // Child row gone (deleted/retention): no entry in the status map.
        _repo.GetRunStatusesByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineStatus>());

        await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        await _runService.Received(1).ResolveCompletedTriggerStepAsync(
            step,
            PipelineStatus.Failed,
            Arg.Is<IReadOnlyDictionary<string, string>>(outputs => outputs.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pass1_ChildStillRunningWithinGrace_LeavesStepUntouched()
    {
        var step = TriggerStep(99);
        _repo.GetStuckRunningTriggerStepsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([step]);
        _repo.GetRunStatusesByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineStatus> { [99] = PipelineStatus.Running });

        await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        // Child not terminal and within the hard ceiling → nothing resolved, no advance.
        Assert.Equal(TaskExecutionStatus.Running, step.Status);
        await _runService.DidNotReceive().ResolveCompletedTriggerStepAsync(
            Arg.Any<PipelineStepRun>(),
            Arg.Any<PipelineStatus>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pass2_StuckRun_ForceFailed()
    {
        _repo.GetStuckRunningRunIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([7]);

        await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        await _runService.Received(1).FailStuckRunAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pass2_PendingStepsWithoutInflightWork_RedrivesScheduler()
    {
        _repo.GetStalledSchedulableRunIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([220]);

        await _sut.ReconcileAsync(CancellationToken.None);

        await _runService.Received(1).ReconcileRunAsync(220, Arg.Any<CancellationToken>());
        await _runService.DidNotReceive().FailStuckRunAsync(220, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancellationRecovery_NoInflightWork_ReappliesCancellation()
    {
        _repo.GetStalledCancellationRunIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([1524]);
        _runService.CancelRunAsync(1524, Arg.Is<PipelineRunCancellation>(c => c.By == PipelineRunCancellation.SystemAuthor), Arg.Any<CancellationToken>()).Returns(true);

        await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        await _runService.Received(1).CancelRunAsync(1524, Arg.Is<PipelineRunCancellation>(c => c.By == PipelineRunCancellation.SystemAuthor), Arg.Any<CancellationToken>());
        await _runService.DidNotReceive().FailStuckRunAsync(1524, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SchedulerRecovery_OneRunThrows_ContinuesWithOtherRuns()
    {
        _repo.GetStalledSchedulableRunIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([220, 221]);
        _runService.ReconcileRunAsync(220, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new BadRequestException("invalid recovery state"));

        await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        await _runService.Received(1).ReconcileRunAsync(220, Arg.Any<CancellationToken>());
        await _runService.Received(1).ReconcileRunAsync(221, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StuckRunFinalization_OneRunThrows_ContinuesWithOtherRuns()
    {
        _repo.GetStuckRunningRunIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([7, 8]);
        _runService.FailStuckRunAsync(7, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("invalid terminal state"));

        await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        await _runService.Received(1).FailStuckRunAsync(7, Arg.Any<CancellationToken>());
        await _runService.Received(1).FailStuckRunAsync(8, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pass4_ExpiredPendingApproval_AutoDecidedTimedOut()
    {
        _repo.GetExpiredPendingApprovalIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([42]);
        _approvalService.DecideApprovalAsync(42, Arg.Any<ApprovalDecisionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineApprovalDto { Id = 42, PipelineRunId = 10, Status = ApprovalStatus.TimedOut });

        await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        await _approvalService.Received(1).DecideApprovalAsync(
            42,
            Arg.Is<ApprovalDecisionRequest>(r =>
                r.Decision == ApprovalStatus.TimedOut && !string.IsNullOrWhiteSpace(r.Comments)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pass4_OneApprovalThrows_ContinuesWithOtherApprovals()
    {
        _repo.GetExpiredPendingApprovalIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([42, 43]);
        _approvalService.DecideApprovalAsync(42, Arg.Any<ApprovalDecisionRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<PipelineApprovalDto>>(_ => throw new InvalidOperationException("transient failure"));

        await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        await _approvalService.Received(1).DecideApprovalAsync(
            42, Arg.Any<ApprovalDecisionRequest>(), Arg.Any<CancellationToken>());
        await _approvalService.Received(1).DecideApprovalAsync(
            43, Arg.Any<ApprovalDecisionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reconcile_NothingStuck_TouchesNothing()
    {
        await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _runService.DidNotReceive().ResolveCompletedTriggerStepAsync(
            Arg.Any<PipelineStepRun>(),
            Arg.Any<PipelineStatus>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
        await _runService.DidNotReceive().ReconcileRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _runService.DidNotReceive().CancelRunAsync(Arg.Any<int>(), Arg.Any<PipelineRunCancellation>(), Arg.Any<CancellationToken>());
        await _runService.DidNotReceive().FailStuckRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _approvalService.DidNotReceive().DecideApprovalAsync(
            Arg.Any<int>(), Arg.Any<ApprovalDecisionRequest>(), Arg.Any<CancellationToken>());
    }

    // --- Pass 6: approval decisions recorded but never applied to their run ---

    [Fact]
    public async Task ReconcileAsync_ApprovedRunNeverResumed_ReAppliesTheResume()
    {
        _repo.GetStrandedApprovalRunsAsync(Arg.Any<CancellationToken>())
            .Returns([new StrandedApprovalRun(2152, ApprovalStatus.Approved)]);

        await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        await _runService.Received(1).ResumeAfterApprovalAsync(2152, Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().TryTransitionPipelineRunStatusAsync(
            2152, PipelineStatus.WaitingForApproval, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.TimedOut)]
    public async Task ReconcileAsync_RefusedRunNeverFailed_ReAppliesTheFailure(ApprovalStatus decision)
    {
        _repo.GetStrandedApprovalRunsAsync(Arg.Any<CancellationToken>())
            .Returns([new StrandedApprovalRun(2152, decision)]);

        await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        // Re-applied through the run service, which also runs a refused confirmation's Rollback.
        await _runService.Received(1).ApplyRefusalAsync(2152, Arg.Any<CancellationToken>());
        await _runService.DidNotReceive().ResumeAfterApprovalAsync(2152, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_RunWaitingWithNoApprovalAtAll_DecidesNothing()
    {
        // No recorded decision to re-apply, and inventing one would approve or fail a production
        // deployment on the sweep's own authority. It is logged and left to a human.
        _repo.GetStrandedApprovalRunsAsync(Arg.Any<CancellationToken>())
            .Returns([new StrandedApprovalRun(2152, null)]);

        await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        await _runService.DidNotReceive().ResumeAfterApprovalAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().TryTransitionPipelineRunStatusAsync(
            Arg.Any<int>(), PipelineStatus.WaitingForApproval, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_OneStrandedRunThrowing_DoesNotStopTheNext()
    {
        _repo.GetStrandedApprovalRunsAsync(Arg.Any<CancellationToken>())
            .Returns([new StrandedApprovalRun(1, ApprovalStatus.Approved), new StrandedApprovalRun(2, ApprovalStatus.Approved)]);
        _runService.ResumeAfterApprovalAsync(1, Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("boom"));

        await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        await _runService.Received(1).ResumeAfterApprovalAsync(2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_RunThatNeverLeavesTheStrandedState_StopsBeingRetried()
    {
        // Without a budget, a run whose resume keeps failing is retried on every tick forever, writing one
        // LogCritical per tick per run and changing nothing. Six sweeps, five attempts, then silence.
        _repo.GetStrandedApprovalRunsAsync(Arg.Any<CancellationToken>())
            .Returns([new StrandedApprovalRun(2152, ApprovalStatus.Approved)]);
        _runService.ResumeAfterApprovalAsync(2152, Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("still stranded"));

        for (var sweep = 0; sweep < 8; sweep++)
            await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        await _runService.Received(5).ResumeAfterApprovalAsync(2152, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_RunThatRecovers_KeepsAFullBudgetForALaterStranding()
    {
        // The budget is spent on consecutive failures, not on the run's lifetime: a run that gets acted on
        // without throwing forgets its count, so an unrelated stranding weeks later is not born exhausted.
        _repo.GetStrandedApprovalRunsAsync(Arg.Any<CancellationToken>())
            .Returns([new StrandedApprovalRun(2152, ApprovalStatus.Approved)]);

        for (var sweep = 0; sweep < 8; sweep++)
            await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        await _runService.Received(8).ResumeAfterApprovalAsync(2152, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EveryTick_ClosesApprovalsLeftPendingOnEndedRuns_BeforeExpiringAny()
    {
        // PLAN-005 lot 3 / D34: covers every run that ended outside the finalizer, run 2323 among them.
        await _sut.ReconcileAsync(TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            _repo.CloseApprovalsOfEndedRunsAsync(null, Arg.Any<DateTime>(), EndedRunApprovals.Reason, Arg.Any<CancellationToken>());
            _repo.GetExpiredPendingApprovalIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        });
    }
}
