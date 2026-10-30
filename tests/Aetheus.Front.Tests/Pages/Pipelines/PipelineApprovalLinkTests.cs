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

    private List<string?> Posts() => [.. _handler.RequestDetails
        .Where(request => request.Method == "POST" && request.Url.Contains(Decide, StringComparison.Ordinal))
        .Select(request => request.Body)];

    [Theory]
    [InlineData("approve", ApprovalStatus.Approved)]
    [InlineData("reject", ApprovalStatus.Rejected)]
    public void SignedIn_TheLinkDecidesOnce_ThroughThePost(string action, ApprovalStatus decision)
    {
        Backend(ApprovalStatus.Pending);

        var cut = Open(action);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".approval-link-outcome")));
        var body = Assert.Single(Posts());
        Assert.Matches($"\"decision\":(\"{decision}\"|{(int)decision})", body!);
        Assert.Contains("ApprovalLinkStatus" + decision, cut.Find(".approval-link-status").TextContent, StringComparison.Ordinal);
        // The action is spent: the address no longer carries it, and a new render decides nothing more.
        Assert.Equal("http://localhost/pipelines/runs/12/approvals/34", Nav.Uri);
        cut.Render();
        Assert.Single(Posts());
        Assert.Empty(cut.FindAll(".approval-decision"));
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

    [Fact]
    public async Task Comment_OpensTheField_AndSendsOnlyWhenTheReaderDecides()
    {
        Backend(ApprovalStatus.Pending);

        var cut = Open("comment");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("textarea")));
        Assert.Empty(Posts());
        cut.Find("textarea").Input("checked the changelog");
        await cut.Find("button.approval-decision").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".approval-link-outcome")));
        var body = Assert.Single(Posts());
        Assert.Contains("checked the changelog", body, StringComparison.Ordinal);
    }

    /// <summary>R-24: the Comment link of the mail puts the focus on the comment field; the other links do not.</summary>
    [Fact]
    public void Comment_PutsTheFocusOnTheCommentField()
    {
        Backend(ApprovalStatus.Pending);

        var cut = Open("comment");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("textarea")));
        var fieldId = cut.Find("textarea").Id;
        Assert.False(string.IsNullOrEmpty(fieldId));
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations,
            call => call.Identifier == "Aetheus.focusById" && Equals(call.Arguments[0], fieldId)));
    }

    [Fact]
    public void WithoutComment_NothingIsFocused()
    {
        Backend(ApprovalStatus.Pending);

        var cut = Open(null);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.approval-decision")));
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "Aetheus.focusById");
    }

    [Fact]
    public void ARefusedDecision_LeavesTheApprovalWaiting_AndSendsNoSecondDecision()
    {
        Backend(ApprovalStatus.Pending, decides: false);

        var cut = Open("approve");

        cut.WaitForAssertion(() => Assert.Single(Posts()));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".approval-decision")));
        Assert.Empty(cut.FindAll(".approval-link-outcome"));
        cut.Render();
        Assert.Single(Posts());
    }
}
