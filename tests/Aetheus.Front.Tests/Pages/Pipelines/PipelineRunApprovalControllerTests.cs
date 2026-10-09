// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// The run page's approval state. Its two states used to look identical from the page: a run waiting on
/// a decision that exists, and one waiting on a decision that does not, the second rendering nothing at
/// all. Run 2152 sat in that second state for eight days, so both are pinned here, along with the
/// difference between "no approval exists" and "the approval could not be loaded".
/// </summary>
public class PipelineRunApprovalControllerTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunApprovalControllerTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private PipelineRunApprovalController Sut(Func<Task>? reload = null) => new(
        Services.GetRequiredService<ApiClient>(),
        Services.GetRequiredService<NotifyHelper>(),
        reload ?? (() => Task.CompletedTask));

    private static PipelineRunDto Waiting(int id = 1) => new()
    {
        Id = id,
        Status = PipelineStatus.WaitingForApproval
    };

    private static PipelineApprovalDto Pending(int id = 7, int environmentId = 3) => new()
    {
        Id = id,
        PipelineRunId = 1,
        StageName = "deploy",
        EnvironmentId = environmentId,
        Status = ApprovalStatus.Pending
    };

    [Fact]
    public async Task LoadAsync_PendingApproval_ExposesItWithoutFlaggingAnythingMissing()
    {
        _handler.SetJsonResponse("api/pipelines/runs/1/approvals", new List<PipelineApprovalDto> { Pending() });
        _handler.SetJsonResponse("api/environments/3", new EnvironmentDto
        {
            Id = 3,
            ApprovalInstructions = "check the changelog"
        });
        var sut = Sut();

        await sut.LoadAsync(Waiting(), Xunit.TestContext.Current.CancellationToken);

        Assert.NotNull(sut.Pending);
        Assert.Equal("deploy", sut.Pending.StageName);
        Assert.Equal("check the changelog", sut.EnvironmentInstructions);
        Assert.False(sut.WaitingWithoutApproval);
        Assert.False(sut.LoadFailed);
    }

    /// <summary>R-370: the pipeline's own approval has no environment instructions, and a run that then
    /// waits on the environment's approval shows that environment's, not the ones of the first.</summary>
    [Fact]
    public async Task LoadAsync_InstructionsFollowTheApprovalShown_AndOnlyAnEnvironmentApprovalHasAny()
    {
        _handler.SetJsonResponse("api/pipelines/runs/1/approvals", new List<PipelineApprovalDto>
        {
            new() { Id = 7, PipelineRunId = 1, StageName = "deploy", Scope = ApprovalScope.Pipeline, EnvironmentId = 3, Status = ApprovalStatus.Pending }
        });
        _handler.SetJsonResponse("api/environments/3", new EnvironmentDto { Id = 3, ApprovalInstructions = "check the changelog" });
        var sut = Sut();

        await sut.LoadAsync(Waiting(), Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(ApprovalScope.Pipeline, sut.Pending?.Scope);
        Assert.Null(sut.EnvironmentInstructions);

        _handler.SetJsonResponse("api/pipelines/runs/1/approvals", new List<PipelineApprovalDto>
        {
            new() { Id = 7, PipelineRunId = 1, StageName = "deploy", Scope = ApprovalScope.Pipeline, EnvironmentId = 3, Status = ApprovalStatus.Approved },
            new() { Id = 8, PipelineRunId = 1, StageName = "deploy", Scope = ApprovalScope.Environment, EnvironmentId = 3, Status = ApprovalStatus.Pending }
        });

        await sut.LoadAsync(Waiting(), Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(8, sut.Pending?.Id);
        Assert.Equal("check the changelog", sut.EnvironmentInstructions);
    }

    [Fact]
    public async Task LoadAsync_WaitingRunWithNoPendingApproval_IsReportedAsMissingNotAsAFailure()
    {
        // The run 2152 state: the backend answers, and answers that nothing is pending. The page must
        // say so rather than render an empty region, and must not blame the transport for it.
        _handler.SetJsonResponse("api/pipelines/runs/1/approvals", new List<PipelineApprovalDto>
        {
            new() { Id = 7, PipelineRunId = 1, StageName = "deploy", Status = ApprovalStatus.Approved }
        });
        var sut = Sut();

        await sut.LoadAsync(Waiting(), Xunit.TestContext.Current.CancellationToken);

        Assert.Null(sut.Pending);
        Assert.True(sut.WaitingWithoutApproval);
        Assert.False(sut.LoadFailed);
    }

    [Fact]
    public async Task LoadAsync_ApprovalsCallFails_SeparatesTheFailureFromAnAbsence()
    {
        _handler.SetResponse("api/pipelines/runs/1/approvals", HttpStatusCode.ServiceUnavailable);
        var sut = Sut();

        await sut.LoadAsync(Waiting(), Xunit.TestContext.Current.CancellationToken);

        Assert.Null(sut.Pending);
        Assert.True(sut.WaitingWithoutApproval);
        Assert.True(sut.LoadFailed);
    }

    [Fact]
    public async Task LoadAsync_RunNotWaiting_ClearsEveryFlag()
    {
        _handler.SetResponse("api/pipelines/runs/1/approvals", HttpStatusCode.ServiceUnavailable);
        var sut = Sut();
        await sut.LoadAsync(Waiting(), Xunit.TestContext.Current.CancellationToken);
        Assert.True(sut.LoadFailed);

        await sut.LoadAsync(
            new PipelineRunDto { Id = 1, Status = PipelineStatus.Running },
            Xunit.TestContext.Current.CancellationToken);

        Assert.Null(sut.Pending);
        Assert.False(sut.WaitingWithoutApproval);
        Assert.False(sut.LoadFailed);
    }

    [Fact]
    public async Task DecideAsync_Accepted_ClearsTheCommentAndReloadsTheRun()
    {
        _handler.SetJsonResponse("api/pipelines/runs/1/approvals", new List<PipelineApprovalDto> { Pending() });
        _handler.SetJsonResponse("api/environments/3", new EnvironmentDto { Id = 3 });
        _handler.SetJsonResponse(
            HttpMethod.Post,
            "api/pipelines/approvals/7/decide",
            new PipelineApprovalDto { Id = 7, PipelineRunId = 1, Status = ApprovalStatus.Approved });
        var reloaded = 0;
        var sut = Sut(() => { reloaded++; return Task.CompletedTask; });
        await sut.LoadAsync(Waiting(), Xunit.TestContext.Current.CancellationToken);
        sut.Comments = "shipping it";

        await sut.DecideAsync(ApprovalStatus.Approved);

        Assert.Equal(1, reloaded);
        Assert.Null(sut.Comments);
        Assert.False(sut.Deciding);
    }

    [Fact]
    public async Task DecideAsync_Refused_SaysWhy_KeepsTheComment_AndRefreshesTheBanner()
    {
        // Recette R2-116: on release-fast #2555 a refused click showed one generic toast and left the
        // banner as it was, offering again an approval the server had already settled. The reason the
        // server gives is shown, and the banner is read again. The comment is the operator's typed
        // input, kept for the next attempt.
        _handler.SetJsonResponse("api/pipelines/runs/1/approvals", new List<PipelineApprovalDto> { Pending() });
        _handler.SetJsonResponse("api/environments/3", new EnvironmentDto { Id = 3 });
        _handler.SetRawResponse(
            HttpMethod.Post, "api/pipelines/approvals/7/decide",
            """{"message":"Approval 7 (run 1, stage 'deploy') was already Approved by sony; there is nothing left to decide."}""",
            "application/json", HttpStatusCode.Conflict);
        var reloaded = 0;
        var sut = Sut(() => { reloaded++; return Task.CompletedTask; });
        await sut.LoadAsync(Waiting(), Xunit.TestContext.Current.CancellationToken);
        sut.Comments = "not yet";

        await sut.DecideAsync(ApprovalStatus.Approved);

        Assert.Equal(1, reloaded);
        Assert.Equal("not yet", sut.Comments);
        Assert.False(sut.Deciding);
        var toast = Assert.Single(Services.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
        Assert.Contains("already Approved by sony", toast.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecideAsync_RefusedWithoutAReason_SaysWhatTheServerAnswered()
    {
        _handler.SetJsonResponse("api/pipelines/runs/1/approvals", new List<PipelineApprovalDto> { Pending() });
        _handler.SetJsonResponse("api/environments/3", new EnvironmentDto { Id = 3 });
        _handler.SetResponse(HttpMethod.Post, "api/pipelines/approvals/7/decide", HttpStatusCode.Forbidden);
        var sut = Sut();
        await sut.LoadAsync(Waiting(), Xunit.TestContext.Current.CancellationToken);

        await sut.DecideAsync(ApprovalStatus.Approved);

        var toast = Assert.Single(Services.Toasts());
        Assert.Contains("ApprovalDecisionFailedStatus", toast.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecideAsync_NothingPending_DoesNotCallTheApi()
    {
        var reloaded = 0;
        var sut = Sut(() => { reloaded++; return Task.CompletedTask; });

        await sut.DecideAsync(ApprovalStatus.Approved);

        Assert.Equal(0, reloaded);
        Assert.False(sut.Deciding);
    }

    [Theory]
    [InlineData(PipelineStatus.Failed)]
    [InlineData(PipelineStatus.Cancelled)]
    [InlineData(PipelineStatus.Success)]
    [InlineData(PipelineStatus.Partial)]
    public async Task LoadAsync_AnEndedRun_NeverOffersADecision_EvenWithAPendingRowLeftOver(PipelineStatus status)
    {
        // PLAN-005 lot 3 / D34: run 2323 ended Cancelled with a Pending Rollback approval in the
        // database. The page must not offer a decision for it, and must not even ask.
        _handler.SetJsonResponse("api/pipelines/runs/1/approvals", new List<PipelineApprovalDto> { Pending() });
        var sut = Sut();

        await sut.LoadAsync(new PipelineRunDto { Id = 1, Status = status }, Xunit.TestContext.Current.CancellationToken);

        Assert.Null(sut.Pending);
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("/approvals", StringComparison.Ordinal));
    }
}
