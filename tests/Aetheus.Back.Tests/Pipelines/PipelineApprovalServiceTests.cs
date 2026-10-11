// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class PipelineApprovalServiceTests
{
    private readonly IPipelineRepository _repoMock = Substitute.For<IPipelineRepository>();
    private readonly IPipelineRunService _runServiceMock = Substitute.For<IPipelineRunService>();
    private readonly IHttpContextAccessor _httpContextAccessorMock = Substitute.For<IHttpContextAccessor>();
    private readonly IHubContext<PipelineHub> _hubMock = Substitute.For<IHubContext<PipelineHub>>();
    private readonly IClientProxy _clientProxyMock;
    private readonly Aetheus.Back.Components.Audit.IAuditService _audit = Substitute.For<Aetheus.Back.Components.Audit.IAuditService>();
    private readonly PipelineApprovalService _sut;

    public PipelineApprovalServiceTests()
    {
        var clientProxyMock = Substitute.For<IClientProxy>();
        _clientProxyMock = clientProxyMock;
        clientProxyMock
            .SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var clientsMock = Substitute.For<IHubClients>();
        clientsMock.All.Returns(clientProxyMock);
        clientsMock.Group(Arg.Any<string>()).Returns(clientProxyMock);
        clientsMock.Groups(Arg.Any<IReadOnlyList<string>>()).Returns(clientProxyMock);
        _hubMock.Clients.Returns(clientsMock);
        // By default the approval exists, is pending, and its run is still waiting on it.
        _repoMock.FindApprovalAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => new PipelineApproval
            {
                Id = call.Arg<int>(),
                PipelineRunId = 10,
                StageName = "deploy",
                Status = ApprovalStatus.Pending
            });
        _repoMock.GetRunStatusesByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineStatus> { [10] = PipelineStatus.WaitingForApproval });

        _sut = new PipelineApprovalService(
            _repoMock, _runServiceMock, _httpContextAccessorMock, _hubMock, _audit,
            NullLogger<PipelineApprovalService>.Instance, TimeProvider.System);
    }

    [Fact]
    public async Task GetApprovalsAsync_ReturnsMappedList()
    {
        _repoMock.GetApprovalsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new PipelineApproval
            {
                Id = 1, PipelineRunId = 1, StageName = "deploy",
                EnvironmentId = 1, Status = ApprovalStatus.Pending
            }]);

        var result = await _sut.GetApprovalsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("deploy", result[0].StageName);
        Assert.Equal(ApprovalStatus.Pending, result[0].Status);
    }

    [Fact]
    public async Task R1_24_GetApprovalsAsync_CarriesTheEnvironmentInstructions_OnlyOnAnEnvironmentApproval()
    {
        var environment = new Aetheus.Back.Data.Entities.Environment
        {
            Id = 3,
            Name = "prod",
            ApprovalInstructions = "Check the QA results before approving."
        };
        _repoMock.GetApprovalsAsync(1, Arg.Any<CancellationToken>())
            .Returns([
                new PipelineApproval
                {
                    Id = 1, PipelineRunId = 1, StageName = "deploy", Scope = ApprovalScope.Pipeline,
                    EnvironmentId = 3, Environment = environment, Status = ApprovalStatus.Approved
                },
                new PipelineApproval
                {
                    Id = 2, PipelineRunId = 1, StageName = "deploy", Scope = ApprovalScope.Environment,
                    EnvironmentId = 3, Environment = environment, Status = ApprovalStatus.Pending
                }
            ]);

        var result = await _sut.GetApprovalsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Null(result[0].EnvironmentInstructions);
        Assert.Equal("Check the QA results before approving.", result[1].EnvironmentInstructions);
    }

    [Fact]
    public async Task DecideApprovalAsync_InvalidDecision_IsRefusedWithItsReason()
    {
        var refusal = await Assert.ThrowsAsync<BadRequestException>(() => _sut.DecideApprovalAsync(1, new ApprovalDecisionRequest
        {
            Decision = ApprovalStatus.Pending
        }, ct: TestContext.Current.CancellationToken));

        Assert.Contains("not a decision", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecideApprovalAsync_ApprovalNotFound_IsANotFoundWithItsReason()
    {
        _repoMock.FindApprovalAsync(99, Arg.Any<CancellationToken>()).Returns((PipelineApproval?)null);

        var refusal = await Assert.ThrowsAsync<NotFoundException>(() => _sut.DecideApprovalAsync(99, new ApprovalDecisionRequest
        {
            Decision = ApprovalStatus.Approved
        }, ct: TestContext.Current.CancellationToken));

        Assert.Contains("Approval 99 does not exist", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecideApprovalAsync_AlreadyResolved_SaysByWhomAndWhen_AndIsAudited()
    {
        // Recette R2-116: on release-fast #2555 every click on a banner offering an approval already
        // decided came back as the same bare 404, indistinguishable from a click that was lost.
        _repoMock.FindApprovalAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineApproval
        {
            Id = 1,
            PipelineRunId = 2555,
            StageName = "Deploy blue-green",
            Status = ApprovalStatus.Approved,
            ResolvedAt = new DateTime(2026, 10, 4, 13, 35, 0, DateTimeKind.Utc),
            ResolvedByUser = new User { Username = "sony" }
        });

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => _sut.DecideApprovalAsync(1, new ApprovalDecisionRequest
        {
            Decision = ApprovalStatus.Approved
        }, ct: TestContext.Current.CancellationToken));

        Assert.Contains("already Approved by sony at 2026-10-04 13:35:00 UTC", refusal.Message, StringComparison.Ordinal);
        await _repoMock.DidNotReceive().TryResolveApprovalAsync(
            Arg.Any<int>(), Arg.Any<ApprovalStatus>(), Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            "ApprovalDecision", "PipelineApproval", 1,
            Arg.Is<string>(details => details.Contains("Approved on run 2555, stage 'Deploy blue-green'", StringComparison.Ordinal)
                && details.Contains("refused: ", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideApprovalAsync_DecidedByAnotherClickInBetween_IsAConflictWithItsReason()
    {
        _repoMock.TryResolveApprovalAsync(
                1, ApprovalStatus.Approved, Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((PipelineApproval?)null);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => _sut.DecideApprovalAsync(1, new ApprovalDecisionRequest
        {
            Decision = ApprovalStatus.Approved
        }, ct: TestContext.Current.CancellationToken));

        Assert.Contains("Approval 1", refusal.Message, StringComparison.Ordinal);
        await _runServiceMock.DidNotReceive().ResumeAfterApprovalAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideApprovalAsync_AnAppliedDecision_IsAuditedWithRunStageAndResult()
    {
        _repoMock.FindApprovalAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineApproval
        {
            Id = 1,
            PipelineRunId = 10,
            StageName = "Deploy blue-green",
            Scope = ApprovalScope.Environment,
            Environment = new Aetheus.Back.Data.Entities.Environment { Name = "prod" },
            Status = ApprovalStatus.Pending
        });
        _repoMock.TryResolveApprovalAsync(
                1, ApprovalStatus.Approved, Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineApproval { Id = 1, PipelineRunId = 10, StageName = "Deploy blue-green", Status = ApprovalStatus.Approved });
        _runServiceMock.ResumeAfterApprovalAsync(10, Arg.Any<CancellationToken>()).Returns(true);

        await _sut.DecideApprovalAsync(1, new ApprovalDecisionRequest { Decision = ApprovalStatus.Approved },
            ct: TestContext.Current.CancellationToken);

        await _audit.Received(1).LogAsync(
            "ApprovalDecision", "PipelineApproval", 1,
            "Approved on run 10, stage 'Deploy blue-green', Environment approval 1 (environment 'prod'): approved, run resumed",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideApprovalAsync_AFailureWhileResuming_IsAuditedThenRethrown()
    {
        _repoMock.TryResolveApprovalAsync(
                1, ApprovalStatus.Approved, Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineApproval { Id = 1, PipelineRunId = 10, StageName = "deploy", Status = ApprovalStatus.Approved });
        _runServiceMock.ResumeAfterApprovalAsync(10, Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.DecideApprovalAsync(
            1, new ApprovalDecisionRequest { Decision = ApprovalStatus.Approved }, ct: TestContext.Current.CancellationToken));

        await _audit.Received(1).LogAsync(
            "ApprovalDecision", "PipelineApproval", 1,
            Arg.Is<string>(details => details.EndsWith("failed: InvalidOperationException", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideApprovalAsync_Approved_ResumesRunAndNotifies()
    {
        _repoMock.TryResolveApprovalAsync(
                1, ApprovalStatus.Approved, Arg.Any<DateTime>(), Arg.Any<int?>(), "LGTM", Arg.Any<CancellationToken>())
            .Returns(new PipelineApproval
            {
                Id = 1,
                PipelineRunId = 10,
                StageName = "deploy",
                EnvironmentId = 1,
                Status = ApprovalStatus.Approved,
                Comments = "LGTM"
            });
        _runServiceMock.ResumeAfterApprovalAsync(10, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.DecideApprovalAsync(1, new ApprovalDecisionRequest
        {
            Decision = ApprovalStatus.Approved,
            Comments = "LGTM"
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ApprovalStatus.Approved, result.Status);
        Assert.Equal("LGTM", result.Comments);
        await _runServiceMock.Received(1).ResumeAfterApprovalAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideApprovalAsync_Rejected_FailsRunAndNotifies()
    {
        _repoMock.TryResolveApprovalAsync(
                1, ApprovalStatus.Rejected, Arg.Any<DateTime>(), Arg.Any<int?>(), "Not ready", Arg.Any<CancellationToken>())
            .Returns(new PipelineApproval
            {
                Id = 1,
                PipelineRunId = 10,
                StageName = "deploy",
                EnvironmentId = 1,
                Status = ApprovalStatus.Rejected,
                Comments = "Not ready"
            });
        _runServiceMock.ApplyRefusalAsync(10, Arg.Any<CancellationToken>()).Returns(PipelineStatus.Failed);

        var result = await _sut.DecideApprovalAsync(1, new ApprovalDecisionRequest
        {
            Decision = ApprovalStatus.Rejected,
            Comments = "Not ready"
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ApprovalStatus.Rejected, result.Status);
        await _runServiceMock.Received(1).ApplyRefusalAsync(10, Arg.Any<CancellationToken>());
        await _runServiceMock.DidNotReceive().ResumeAfterApprovalAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideApprovalAsync_TimedOut_FailsRunAndBroadcastsTimedOutReason()
    {
        _repoMock.TryResolveApprovalAsync(
                1, ApprovalStatus.TimedOut, Arg.Any<DateTime>(), Arg.Any<int?>(), "Approval timed out", Arg.Any<CancellationToken>())
            .Returns(new PipelineApproval
            {
                Id = 1,
                PipelineRunId = 10,
                StageName = "deploy",
                EnvironmentId = 1,
                Status = ApprovalStatus.TimedOut,
                ResolvedAt = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc),
                Comments = "Approval timed out"
            });
        _runServiceMock.ApplyRefusalAsync(10, Arg.Any<CancellationToken>()).Returns(PipelineStatus.Failed);

        var result = await _sut.DecideApprovalAsync(1, new ApprovalDecisionRequest
        {
            Decision = ApprovalStatus.TimedOut,
            Comments = "Approval timed out"
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ApprovalStatus.TimedOut, result.Status);
        Assert.Equal("Approval timed out", result.Comments);
        Assert.NotNull(result.ResolvedAt);
        await _runServiceMock.Received(1).ApplyRefusalAsync(10, Arg.Any<CancellationToken>());
        await _runServiceMock.DidNotReceive().ResumeAfterApprovalAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _clientProxyMock.Received(1).SendCoreAsync(
            "ApprovalResolved",
            Arg.Is<object?[]>(args => Equals(args[2], "TimedOut")),
            Arg.Any<CancellationToken>());
        await _clientProxyMock.Received(1).SendCoreAsync(
            "PipelineRunCompleted",
            Arg.Is<object?[]>(args => Equals(args[1], PipelineStatus.Failed)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// PLAN-003 2.7: a refused confirmation hands the run to its Rollback, so it has not completed yet;
    /// announcing a completion here would tell the page the run ended while the rollback runs.
    /// </summary>
    [Fact]
    public async Task DecideApprovalAsync_RefusedConfirmation_DoesNotAnnounceACompletedRun()
    {
        _repoMock.TryResolveApprovalAsync(
                1, ApprovalStatus.Rejected, Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineApproval
            {
                Id = 1,
                PipelineRunId = 10,
                StageName = "Confirm",
                EnvironmentId = 1,
                Status = ApprovalStatus.Rejected,
                TimeoutMinutes = 10
            });
        _runServiceMock.ApplyRefusalAsync(10, Arg.Any<CancellationToken>()).Returns(PipelineStatus.Running);

        var result = await _sut.DecideApprovalAsync(1, new ApprovalDecisionRequest
        {
            Decision = ApprovalStatus.Rejected
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _clientProxyMock.Received(1).SendCoreAsync(
            "ApprovalResolved", Arg.Is<object?[]>(args => Equals(args[2], "Rejected")), Arg.Any<CancellationToken>());
        await _clientProxyMock.DidNotReceive().SendCoreAsync(
            "PipelineRunCompleted", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideApprovalAsync_TimedOut_RunNoLongerWaiting_IsAConflict()
    {
        _repoMock.TryResolveApprovalAsync(
                1, ApprovalStatus.TimedOut, Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineApproval
            {
                Id = 1,
                PipelineRunId = 10,
                StageName = "deploy",
                EnvironmentId = 1,
                Status = ApprovalStatus.TimedOut
            });
        _runServiceMock.ApplyRefusalAsync(10, Arg.Any<CancellationToken>()).Returns((PipelineStatus?)null);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.DecideApprovalAsync(1, new ApprovalDecisionRequest
        {
            Decision = ApprovalStatus.TimedOut
        }, ct: TestContext.Current.CancellationToken));

        await _clientProxyMock.DidNotReceive().SendCoreAsync(
            Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideApprovalAsync_ApprovedButResumeRefused_LeavesTheRunForTheReconciler()
    {
        // The approval row is resolved before the run is moved, so a refused resume strands the run in
        // WaitingForApproval with nothing pending on it: no decision offered on the page, and invisible
        // to the timeout sweep, which only reads pending rows. Run 2152 sat like that for eight days.
        _repoMock.TryResolveApprovalAsync(
                1, ApprovalStatus.Approved, Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineApproval
            {
                Id = 1,
                PipelineRunId = 10,
                StageName = "deploy",
                EnvironmentId = 1,
                Status = ApprovalStatus.Approved
            });
        _runServiceMock.ResumeAfterApprovalAsync(10, Arg.Any<CancellationToken>()).Returns(false);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => _sut.DecideApprovalAsync(1, new ApprovalDecisionRequest
        {
            Decision = ApprovalStatus.Approved
        }, ct: TestContext.Current.CancellationToken));

        Assert.Contains("The approval was recorded, but run 10 did not resume", refusal.Message, StringComparison.Ordinal);
        await _runServiceMock.Received(1).ResumeAfterApprovalAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideApprovalAsync_RejectedButTransitionRefused_LeavesTheRunForTheReconciler()
    {
        _repoMock.TryResolveApprovalAsync(
                1, ApprovalStatus.Rejected, Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineApproval
            {
                Id = 1,
                PipelineRunId = 10,
                StageName = "deploy",
                EnvironmentId = 1,
                Status = ApprovalStatus.Rejected
            });
        _runServiceMock.ApplyRefusalAsync(10, Arg.Any<CancellationToken>()).Returns((PipelineStatus?)null);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => _sut.DecideApprovalAsync(1, new ApprovalDecisionRequest
        {
            Decision = ApprovalStatus.Rejected
        }, ct: TestContext.Current.CancellationToken));

        Assert.Contains("did not stop", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PipelineStatus.Failed, ApprovalStatus.Approved)]
    [InlineData(PipelineStatus.Cancelled, ApprovalStatus.Rejected)]
    [InlineData(PipelineStatus.Success, ApprovalStatus.Approved)]
    public async Task DecideApprovalAsync_OnARunThatHasEnded_IsRefused_AndDecidesNothing(PipelineStatus runStatus, ApprovalStatus decision)
    {
        // PLAN-005 lot 3 / D34: run 2323 ended Cancelled with its Rollback approval still Pending.
        // Approving it would try to resume a finished run; the decision is refused instead.
        _repoMock.FindApprovalAsync(10, Arg.Any<CancellationToken>())
            .Returns(new PipelineApproval { Id = 10, PipelineRunId = 2323, StageName = "Rollback", Status = ApprovalStatus.Pending });
        _repoMock.GetRunStatusesByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineStatus> { [2323] = runStatus });

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.DecideApprovalAsync(10, new ApprovalDecisionRequest { Decision = decision }, TestContext.Current.CancellationToken));

        Assert.Contains("already ended", refusal.Message, StringComparison.Ordinal);
        await _repoMock.DidNotReceive().TryResolveApprovalAsync(
            Arg.Any<int>(), Arg.Any<ApprovalStatus>(), Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _runServiceMock.DidNotReceive().ResumeAfterApprovalAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
