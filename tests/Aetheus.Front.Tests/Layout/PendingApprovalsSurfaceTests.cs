// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Dashboards;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Layout;

/// <summary>PLAN-007 lot 7: an approval waiting for a decision shows in the top bar and on the home
/// page without opening the run, and nothing shows when nothing waits.</summary>
public sealed class PendingApprovalsSurfaceTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PendingApprovalsSurfaceTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PendingApprovalDto Approval(int runId, string pipeline = "aetheus-deploy-prod") => new()
    {
        ApprovalId = runId,
        PipelineRunId = runId,
        PipelineName = pipeline,
        StageName = "Restore candidate",
        EnvironmentName = "prod",
        RequestedAt = new DateTime(2026, 9, 12, 8, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void NothingPending_ShowsNothing()
    {
        Assert.Empty(Render<PendingApprovalsIndicator>().FindAll("button"));
        Assert.Empty(Render<PendingApprovalsPanel>().FindAll(".pending-approvals-panel"));
    }

    [Fact]
    public void Pending_TheTopBarButtonIsTheTaskTrackersTwin_AndListsEachApprovalWithItsRun()
    {
        _handler.SetJsonResponse("api/pipelines/approvals/pending", new List<PendingApprovalDto> { Approval(2334), Approval(2335, "demo-deploy") });

        var indicator = Render<PendingApprovalsIndicator>();
        var button = indicator.WaitForElement(".pending-approvals-btn");
        // The same box as the task tracker carrying a count: the header square doubled, its icon
        // sized as the header's, the count after it.
        foreach (var cls in new[] { "header-action-btn", "task-tracker-btn", "task-tracker-btn-wide" })
            Assert.Contains(cls, button.ClassList);
        Assert.NotNull(button.QuerySelector(".header-icon"));
        Assert.Equal("2", button.QuerySelector(".task-tracker-count")!.TextContent);

        // A click opens the list instead of leaving the page.
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var before = nav.Uri;
        button.Click();
        Assert.Equal(before, nav.Uri);
        indicator.WaitForAssertion(() => Assert.Equal(2, indicator.FindAll(".pending-approvals-row").Count));
        Assert.Contains(indicator.FindAll(".pending-approvals-row"), row => row.GetAttribute("href") == "/pipelines/runs/2334");
        Assert.Contains(indicator.FindAll(".pending-approvals-row"), row => row.GetAttribute("href") == "/pipelines/runs/2335");
    }

    /// <summary>Decision of 2026-10-06: the browser tab's title carries the waiting approvals too, with the
    /// attention message it alternates with in the background (layout.js does the blinking).</summary>
    [Fact]
    public void Pending_TheTabTitleIsToldTheCountAndTheAttentionMessage()
    {
        _handler.SetJsonResponse("api/pipelines/approvals/pending", new List<PendingApprovalDto> { Approval(2334), Approval(2335) });

        var indicator = Render<PendingApprovalsIndicator>();
        indicator.WaitForElement(".pending-approvals-btn");

        indicator.WaitForAssertion(() =>
        {
            var call = JSInterop.Invocations["Aetheus.titleAttention.set"].Last();
            Assert.Equal(2, Assert.IsType<int>(call.Arguments[0]));
            Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(call.Arguments[1])));
        });
    }

    [Fact]
    public void SeveralPending_TheHomePageListsEachWithItsLink()
    {
        _handler.SetJsonResponse("api/pipelines/approvals/pending", new List<PendingApprovalDto> { Approval(2334), Approval(2335, "demo-deploy") });

        var panel = Render<PendingApprovalsPanel>();
        panel.WaitForAssertion(() => Assert.Equal(2, panel.FindAll(".pending-approvals-list li").Count));

        Assert.Contains(panel.FindAll("a"), link => link.GetAttribute("href") == "/pipelines/runs/2334");
        Assert.Contains(panel.FindAll("a"), link => link.GetAttribute("href") == "/pipelines/runs/2335");
    }
}
