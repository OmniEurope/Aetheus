// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// Recette R-24: the page an approval mail's buttons open. Signed in, Approve and Reject decide through one POST
/// of the authenticated API; an approval that no longer waits is never decided again; Comment sends nothing until
/// the reader presses a button; a reader who is not signed in gets no decision at all.
/// </summary>
public sealed class PipelineApprovalLinkTests : BunitContext
{
    private const string Decide = "api/pipelines/approvals/34/decide";
    private const string Approvals = "api/pipelines/runs/12/approvals";

    private BunitTestHelper.TestHandler _handler = default!;
    private PipelineApprovalDto _stored = default!;

    private BunitNavigationManager Nav => Services.GetRequiredService<BunitNavigationManager>();

    /// <summary>The backend as the page sees it: the approvals read returns the stored approval, and a decision
    /// (when <paramref name="decides"/>) stores what was decided, by the signed-in reader.</summary>
    private void Backend(ApprovalStatus status, bool authenticated = true, bool decides = true)
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated);
        _handler.SetJsonResponse("api/pipelines/approvals/pending", new List<PendingApprovalDto>());
        _stored = Approval(status, "alice");
        _handler.SetAsyncJsonResponse(HttpMethod.Get, Approvals, _ => Task.FromResult(new List<PipelineApprovalDto> { _stored }));
        if (!decides)
        {
            _handler.SetResponse(HttpMethod.Post, Decide, HttpStatusCode.Forbidden);
            return;
        }
        _handler.SetAsyncJsonResponse(HttpMethod.Post, Decide, _ =>
        {
            var body = _handler.RequestDetails.Last(request => request.Method == "POST").Body ?? string.Empty;
            var decided = body.Contains("Rejected", StringComparison.Ordinal) || body.Contains("\"decision\":2", StringComparison.Ordinal)
                ? ApprovalStatus.Rejected : ApprovalStatus.Approved;
            _stored = Approval(decided, Services.GetRequiredService<AuthStateProvider>().Username);
            return Task.FromResult(_stored);
        });
    }

    private static PipelineApprovalDto Approval(ApprovalStatus status, string? decidedBy) => new()
    {
        Id = 34,
        PipelineRunId = 12,
        PipelineRunNumber = 5,
        StageName = "Deploy",
        Scope = ApprovalScope.Environment,
        EnvironmentName = "prod",
        Status = status,
        ResolvedByUsername = status == ApprovalStatus.Pending ? null : decidedBy,
        ResolvedAt = status == ApprovalStatus.Pending ? null : new DateTime(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc)
    };

    private IRenderedComponent<PipelineApprovalLink> Open(string? action)
    {
        Nav.NavigateTo("http://localhost/pipelines/runs/12/approvals/34" + (action is null ? "" : "?action=" + action));
        return Render<PipelineApprovalLink>(parameters => parameters
            .Add(page => page.RunId, 12)
            .Add(page => page.ApprovalId, 34));
    }

    // R-8: the crumb names the run by its number within the pipeline, its link by the technical key.
    [Fact]
    public void TheRunCrumb_ShowsTheRunNumber_AndLinksByTheTechnicalKey()
    {
        Backend(ApprovalStatus.Pending);

        var cut = Open(action: null);

        cut.WaitForAssertion(() =>
        {
            var crumb = Services.GetRequiredService<BreadcrumbService>().Items[1];
            Assert.Contains("#5", crumb.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("12", crumb.Text, StringComparison.Ordinal);
            Assert.Equal("/pipelines/runs/12", crumb.Href);
        });
    }

    private List<string?> Posts() => [.. _handler.RequestDetails
        .Where(request => request.Method == "POST" && request.Url.Contains(Decide, StringComparison.Ordinal))
        .Select(request => request.Body)];

    [Theory]
    [InlineData("approve", ApprovalStatus.Approved)]
    [InlineData("reject", ApprovalStatus.Rejected)]
    public void SignedIn_TheLinkDecidesOnce_ThroughThePost_ThenLandsOnTheRunPage(string action, ApprovalStatus decision)
    {
        Backend(ApprovalStatus.Pending);

        var cut = Open(action);

        cut.WaitForAssertion(() => Assert.Equal("http://localhost/pipelines/runs/12", Nav.Uri));
        var body = Assert.Single(Posts());
        Assert.Matches($"\"decision\":(\"{decision}\"|{(int)decision})", body!);
        Assert.Equal(decision, _stored.Status);
        // The action is spent: a new render decides nothing more.
        cut.Render();
        Assert.Single(Posts());
    }

    [Theory]
    [InlineData(ApprovalStatus.Approved)]
    [InlineData(ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.TimedOut)]
    public void AnApprovalAlreadyDecided_ShowsItsState_AndIsNeverDecidedAgain(ApprovalStatus status)
    {
        Backend(status);

        var cut = Open("approve");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".approval-link-already-decided")));
        Assert.Contains("ApprovalLinkStatus" + status, cut.Find(".approval-link-status").TextContent, StringComparison.Ordinal);
        Assert.Empty(Posts());
        Assert.Empty(cut.FindAll(".approval-decision"));
    }

    [Fact]
    public void NotSignedIn_TheLinkDecidesNothing()
    {
        Backend(ApprovalStatus.Pending, authenticated: false);

        var cut = Open("approve");

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestsSnapshot(), request => request.Url.Contains(Approvals, StringComparison.Ordinal)));
        cut.Render();
        Assert.Empty(Posts());
    }

    /// <summary>R-13: Comment has no one-click action; it lands on the run page at once and sends nothing.</summary>
    [Fact]
    public void Comment_LandsOnTheRunPage_AndSendsNothing()
    {
        Backend(ApprovalStatus.Pending);

        Open("comment");

        Assert.Equal("http://localhost/pipelines/runs/12", Nav.Uri);
        Assert.Empty(Posts());
    }

    [Fact]
    public void WithoutAction_NothingIsFocused_AndTheReaderStaysOnTheApprovalPage()
    {
        Backend(ApprovalStatus.Pending);

        var cut = Open(null);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.approval-decision")));
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "Aetheus.focusById");
        Assert.Equal("http://localhost/pipelines/runs/12/approvals/34", Nav.Uri);
    }

    /// <summary>F-FRONT-007: the link of approval A (<c>?action=approve</c>) is still reading when the reader moves to
    /// approval B of the same run. A's late read must not decide B, nor replace B's page state.</summary>
    [Fact]
    public async Task ALinkRead_AnsweredAfterTheReaderMovedToAnotherApproval_DecidesNothing()
    {
        Backend(ApprovalStatus.Pending);
        var other = Approval(ApprovalStatus.Pending, null) with { Id = 35 };
        var approvals = new List<PipelineApprovalDto> { Approval(ApprovalStatus.Pending, null), other };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        _handler.SetAsyncJsonResponse(HttpMethod.Get, Approvals, async _ =>
        {
            if (Interlocked.Increment(ref reads) == 1) await gate.Task;
            return approvals;
        });

        var cut = Open("approve");
        await _handler.WhenGetAsync(Approvals);
        // The reader moves to approval B while A's read is still in flight.
        Nav.NavigateTo("http://localhost/pipelines/runs/12/approvals/35");
        cut.Render(parameters => parameters
            .Add(page => page.RunId, 12)
            .Add(page => page.ApprovalId, 35));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.approval-decision")));
        gate.SetResult();
        await Task.Delay(200, Xunit.TestContext.Current.CancellationToken);
        cut.Render();

        Assert.DoesNotContain(_handler.RequestDetails, request => request.Method == "POST");
        Assert.NotEmpty(cut.FindAll("button.approval-decision"));
        Assert.Empty(cut.FindAll(".approval-link-outcome"));
    }

    [Fact]
    public void ARefusedDecision_LeavesTheApprovalWaiting_AndSendsNoSecondDecision()
    {
        Backend(ApprovalStatus.Pending, decides: false);

        var cut = Open("approve");

        cut.WaitForAssertion(() => Assert.Single(Posts()));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".approval-decision")));
        // R-13: a refused action keeps the reader on the approval page instead of redirecting.
        Assert.Equal("http://localhost/pipelines/runs/12/approvals/34", Nav.Uri);
        Assert.Empty(cut.FindAll(".approval-link-outcome"));
        cut.Render();
        Assert.Single(Posts());
    }
}
