// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Analysis;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Covers lineage follow-ups, finding-decision reversal and version-gated
/// approval.</summary>
public sealed class RecetteR2RunPageTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public RecetteR2RunPageTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        Services.GetRequiredService<PermissionService>().SetPermissions([], isAdmin: true);
        // The Gate tab pages through the tree's findings on the server; it serves the gate's open
        // findings by default and every one when the decided ones are asked for.
        AnalysisRunFindingsPageDto Page(IReadOnlyList<AnalysisRunGateFindingDto> items, int open, int decided) =>
            new() { Items = items, TotalCount = items.Count, Page = 1, PageSize = 25, OpenCount = open, DecidedCount = decided, NewOpenCount = 1 };
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings",
            Page([.. Gate().Findings.Where(finding => finding.Status == AnalysisFindingStatus.Open)], 1, 2));
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "includeDecided=true", _ => Task.FromResult(Page(Gate().Findings, 1, 2)));
    }

    private static PipelineRunDto Run(int id) =>
        new() { Id = id, PipelineId = 5, PipelineName = "candidate", Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow };

    private static List<PipelineRunLinkDto> FollowUps(int count) =>
        [.. Enumerable.Range(1, count).Select(index => new PipelineRunLinkDto
        {
            RunId = 100 + index,
            PipelineId = 9,
            ProjectId = 8,
            PipelineName = $"deliver-{index}",
            BuildNumber = index,
            Status = PipelineStatus.Success
        })];

    // Lineage tile.

    [Fact]
    public async Task R2_026_ManyFollowUps_ShowsThree_AndSeeAllOpensTheFullList()
    {
        _handler.SetJsonResponse("api/pipelines/runs/7/lineage", new PipelineRunLineageDto { Downstream = FollowUps(5) });

        var cut = Render<PipelineRunLineageTile>(parameters => parameters.Add(component => component.Run, Run(7)));

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("a.run-lineage-child").Count));
        Assert.Equal(["deliver-1 #1", "deliver-2 #2", "deliver-3 #3"],
            cut.FindAll("a.run-lineage-child").Select(link => link.TextContent.Trim()).ToList());
        var seeAll = cut.Find("button.run-lineage-see-all");
        Assert.Contains("SeeAll (5)", seeAll.TextContent, StringComparison.Ordinal);
        Assert.Contains("omni-button--secondary", seeAll.ClassName, StringComparison.Ordinal);

        await cut.InvokeAsync(() => seeAll.Click());

        Assert.Equal(typeof(PipelineRunLineageDialog), _dialog.LastComponent);
        Assert.Equal("RunFollowUps", _dialog.LastTitle);
        var runs = Assert.IsAssignableFrom<IReadOnlyList<PipelineRunLinkDto>>(_dialog.LastParameters![nameof(PipelineRunLineageDialog.Runs)]);
        Assert.Equal(5, runs.Count);
    }

    [Fact]
    public void R2_026_ThreeFollowUpsOrFewer_HaveNoSeeAll()
    {
        _handler.SetJsonResponse("api/pipelines/runs/7/lineage", new PipelineRunLineageDto { Downstream = FollowUps(3) });

        var cut = Render<PipelineRunLineageTile>(parameters => parameters.Add(component => component.Run, Run(7)));

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("a.run-lineage-child").Count));
        Assert.Empty(cut.FindAll("button.run-lineage-see-all"));
    }

    [Fact]
    public void R2_048_ATileThatNamesARun_TakesTwoColumns_AShortActorOne()
    {
        _handler.SetJsonResponse("api/pipelines/runs/7/lineage", new PipelineRunLineageDto { Downstream = FollowUps(1) });
        var naming = Render<PipelineRunLineageTile>(parameters => parameters.Add(component => component.Run, Run(7)));
        naming.WaitForAssertion(() => Assert.Contains("run-lineage-tile--wide", naming.Find(".run-lineage-tile").ClassName, StringComparison.Ordinal));
        // The two columns are OE's (OmniFactTile.DoubleWidth), not a rule of the application.
        Assert.Contains("omni-tile--double", naming.Find(".run-lineage-tile").ClassName, StringComparison.Ordinal);

        _handler.SetJsonResponse("api/pipelines/runs/8/lineage", new PipelineRunLineageDto { TriggeredBy = "claude" });
        var alone = Render<PipelineRunLineageTile>(parameters => parameters.Add(component => component.Run, Run(8)));
        alone.WaitForAssertion(() => Assert.Contains("claude", alone.Find(".run-lineage-actor").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("run-lineage-tile--wide", alone.Find(".run-lineage-tile").ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain("omni-tile--double", alone.Find(".run-lineage-tile").ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void R2_026_TheDialog_ListsEveryFollowUp_AsLinks()
    {
        var cut = Render<PipelineRunLineageDialog>(parameters => parameters
            .Add(component => component.Runs, FollowUps(5))
            .Add(component => component.ProjectId, 8));

        var links = cut.FindAll("a.run-lineage-child");
        Assert.Equal(5, links.Count);
        Assert.Equal("/pipelines/runs/105?projectId=8", links[4].GetAttribute("href"));
    }

    // Finding-decision reversal.

    private static AnalysisRunGateDto Gate() => new()
    {
        PipelineRunId = 40,
        Status = AnalysisGateStatus.Warning,
        ReportCount = 1,
        FindingCount = 1,
        DecidedFindingCount = 2,
        Findings =
        [
            new AnalysisRunGateFindingDto { FindingId = 1, Title = "open", Status = AnalysisFindingStatus.Open, IsNew = true },
            new AnalysisRunGateFindingDto { FindingId = 2, Title = "accepted", Status = AnalysisFindingStatus.Accepted, IsNew = true },
            new AnalysisRunGateFindingDto { FindingId = 3, Title = "fixed", Status = AnalysisFindingStatus.Fixed }
        ]
    };

    [Fact]
    public async Task R2_027_ShownDecidedFindings_CanBeReverted_OnlyWhereADecisionHolds()
    {
        _handler.SetResponse(HttpMethod.Delete, "api/analysis/findings/2/decisions/active", HttpStatusCode.NoContent);
        _dialog.ConfirmResult = true;
        var reopened = new List<int>();
        var cut = Render<PipelineRunGateFindings>(parameters => parameters
            .Add(component => component.Gate, Gate())
            .Add(component => component.ProjectId, 8)
            .Add(component => component.FindingReopened, (int findingId) => reopened.Add(findingId)));
        Assert.Empty(cut.FindAll("button.analysis-finding-revert-decision"));

        await cut.InvokeAsync(() => cut.FindAll("button.omni-switch").Single(toggle => toggle.TextContent.Contains("AnalysisGateShowDecided", StringComparison.Ordinal)).Click());

        // The accepted finding carries the action; the open and the fixed ones have no decision to revert.
        var revert = cut.WaitForElement("button.analysis-finding-revert-decision");
        Assert.Single(cut.FindAll("button.analysis-finding-revert-decision"));
        Assert.Contains("omni-button--secondary", revert.ClassName, StringComparison.Ordinal);
        await cut.InvokeAsync(() => revert.Click());

        Assert.Equal("AnalysisRevertDecision", _dialog.LastTitle);
        Assert.Equal("AnalysisReopenFinding", _dialog.LastConfirmOptions!.OkButtonText);
        Assert.Equal("GoBack", _dialog.LastConfirmOptions.CancelButtonText);
        Assert.Contains(_handler.Requests, request => request.Method == "DELETE"
            && request.Url.Contains("api/analysis/findings/2/decisions/active", StringComparison.Ordinal));
        Assert.Equal([2], reopened);
    }

    [Fact]
    public async Task R2_027_GoingBackFromTheConfirmation_RevertsNothing()
    {
        _dialog.ConfirmResult = false;
        var reopened = new List<int>();
        var cut = Render<PipelineRunGateFindings>(parameters => parameters
            .Add(component => component.Gate, Gate())
            .Add(component => component.ProjectId, 8)
            .Add(component => component.FindingReopened, (int findingId) => reopened.Add(findingId)));
        await cut.InvokeAsync(() => cut.FindAll("button.omni-switch").Single(toggle => toggle.TextContent.Contains("AnalysisGateShowDecided", StringComparison.Ordinal)).Click());

        await cut.InvokeAsync(() => cut.WaitForElement("button.analysis-finding-revert-decision").Click());

        Assert.DoesNotContain(_handler.Requests, request => request.Method == "DELETE");
        Assert.Empty(reopened);
    }

    [Fact]
    public void R2_027_WithoutTheProjectAdministration_NoRevertIsOffered()
    {
        Services.GetRequiredService<PermissionService>().SetPermissions([], isAdmin: false);
        var cut = Render<PipelineRunGateFindings>(parameters => parameters
            .Add(component => component.Gate, Gate())
            .Add(component => component.ProjectId, 8));

        cut.FindAll("button.omni-switch").Single(toggle => toggle.TextContent.Contains("AnalysisGateShowDecided", StringComparison.Ordinal)).Click();

        Assert.Empty(cut.FindAll("button.analysis-finding-revert-decision"));
    }

    /// <summary>Reverting a finding refreshes whole-tree figures while retaining the evaluated
    /// verdict.</summary>
    [Fact]
    public async Task R2_027_AReopenedFinding_CountsAsOpenAgain_InTheMergedGate()
    {
        var api = Services.GetRequiredService<ApiClient>();
        _handler.SetJsonResponse("api/analysis/runs/40", Gate());
        var state = new PipelineRunGateState();
        await state.LoadAsync(api, Run(40), [], ct: Xunit.TestContext.Current.CancellationToken);
        Assert.Equal((1, 2), (state.Result!.FindingCount, state.Result.DecidedFindingCount));

        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings",
            new AnalysisRunFindingsPageDto { OpenCount = 2, NewOpenCount = 2, DecidedCount = 1 });
        await state.ReopenFindingAsync(api, ct: Xunit.TestContext.Current.CancellationToken);

        var gate = state.Result!;
        Assert.Equal(2, gate.FindingCount);
        Assert.Equal(2, gate.NewFindingCount);
        Assert.Equal(1, gate.DecidedFindingCount);
        Assert.Equal(AnalysisGateStatus.Warning, gate.Status);
    }

    [Fact]
    public async Task R2_027_AReopenedFinding_DropsTheCachedGates_SoAReloadReadsTheServer()
    {
        var run = Run(40);
        var cache = new PipelineRunPageCache();
        cache.StoreGate(run, Gate());
        var state = new PipelineRunGateState();
        await state.LoadAsync(Services.GetRequiredService<ApiClient>(), run, [], cache, Xunit.TestContext.Current.CancellationToken);
        Assert.True(cache.TryGetGate(40, out _));
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("api/analysis/runs/40", StringComparison.Ordinal));

        await state.ReopenFindingAsync(Services.GetRequiredService<ApiClient>(), cache, Xunit.TestContext.Current.CancellationToken);

        Assert.False(cache.TryGetGate(40, out _));
        // Within the 20 s the cache would have answered, the reload now asks the server, whose
        // finding is open again.
        _handler.SetJsonResponse("api/analysis/runs/40", Gate() with
        {
            FindingCount = 2,
            DecidedFindingCount = 1,
            Findings = [.. Gate().Findings.Select(finding => finding.FindingId == 2 ? finding with { Status = AnalysisFindingStatus.Open } : finding)]
        });
        await state.LoadAsync(Services.GetRequiredService<ApiClient>(), run, [], cache, Xunit.TestContext.Current.CancellationToken);

        Assert.Contains(_handler.Requests, request => request.Url.Contains("api/analysis/runs/40", StringComparison.Ordinal));
        Assert.Equal(AnalysisFindingStatus.Open, state.Result!.Findings.Single(finding => finding.FindingId == 2).Status);
    }

    [Fact]
    public async Task R2_027_TheFindingPage_RevertsTheDecisionInForce()
    {
        _handler.SetJsonResponse("api/analysis/findings/10", new AnalysisFindingDto
        {
            Id = 10,
            ProjectId = 1,
            Title = "Review required",
            Status = AnalysisFindingStatus.Accepted
        });
        _handler.SetJsonResponse("api/analysis/findings/10/occurrences?take=100", new List<AnalysisFindingOccurrenceDto>());
        _handler.SetJsonResponse("api/analysis/findings/10/decisions", new List<AnalysisFindingDecisionDto>
        {
            new() { Id = 3, AnalysisFindingId = 10, Status = AnalysisFindingStatus.Accepted, Reason = "accepted for now", CreatedByUsername = "admin" }
        });
        _handler.SetResponse(HttpMethod.Delete, "api/analysis/findings/10/decisions/active", HttpStatusCode.NoContent);
        _dialog.ConfirmResult = true;
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/analysis/findings/10?tab=decisions");

        var cut = Render<AnalysisFindingDetail>(parameters => parameters.Add(component => component.Id, 10));
        // Reopening is one of the decision tiles.
        var revert = cut.WaitForElement(".analysis-decision-choices .analysis-decision-reopen button.analysis-decision-revert");
        await cut.InvokeAsync(() => revert.Click());

        Assert.Contains(_handler.Requests, request => request.Method == "DELETE"
            && request.Url.Contains("api/analysis/findings/10/decisions/active", StringComparison.Ordinal));
    }

    [Fact]
    public void R2_027_TheFindingPage_OffersNoRevert_WhenNoDecisionHolds()
    {
        _handler.SetJsonResponse("api/analysis/findings/11", new AnalysisFindingDto
        {
            Id = 11,
            ProjectId = 1,
            Title = "Reopened",
            Status = AnalysisFindingStatus.Open
        });
        _handler.SetJsonResponse("api/analysis/findings/11/occurrences?take=100", new List<AnalysisFindingOccurrenceDto>());
        _handler.SetJsonResponse("api/analysis/findings/11/decisions", new List<AnalysisFindingDecisionDto>
        {
            new() { Id = 4, AnalysisFindingId = 11, Status = AnalysisFindingStatus.Accepted, Reason = "reverted", RevokedAt = DateTime.UtcNow }
        });
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/analysis/findings/11?tab=decisions");

        var cut = Render<AnalysisFindingDetail>(parameters => parameters.Add(component => component.Id, 11));
        cut.WaitForState(() => cut.Markup.Contains("analysis-decision-card", StringComparison.Ordinal));

        Assert.Empty(cut.FindAll("button.analysis-decision-revert"));
    }

    // Approval waits for the deployed version.

    private PipelineRunApprovalController PendingApproval()
    {
        var controller = new PipelineRunApprovalController(
            Services.GetRequiredService<ApiClient>(), Services.GetRequiredService<NotifyHelper>(), () => Task.CompletedTask);
        typeof(PipelineRunApprovalController).GetProperty(nameof(PipelineRunApprovalController.Pending))!
            .SetValue(controller, new PipelineApprovalDto { Id = 31, PipelineRunId = 40, StageName = "Confirm", Scope = ApprovalScope.Pipeline });
        return controller;
    }

    [Fact]
    public void R2_021_OnTheVersionDeployed_TheApprovalIsOffered()
    {
        var cut = Render<PipelineRunApprovalPanel>(parameters => parameters.Add(component => component.Approval, PendingApproval()));

        cut.WaitForAssertion(() => Assert.Contains("Approve", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".approval-reload-first"));
        Assert.Contains(_handler.Requests, request => request.Url.Contains("appsettings.json", StringComparison.Ordinal));
    }

    // Environment approval and stage confirmation have distinct titles.

    [Theory]
    [InlineData(ApprovalScope.Environment, "EnvironmentApprovalTitle", "PipelineApprovalTitle")]
    [InlineData(ApprovalScope.Pipeline, "PipelineApprovalTitle", "EnvironmentApprovalTitle")]
    public void R2_116_TheEnvironmentApproval_AndTheConfirmation_HaveDistinctTitles(
        ApprovalScope scope, string expected, string other)
    {
        var controller = PendingApproval();
        typeof(PipelineRunApprovalController).GetProperty(nameof(PipelineRunApprovalController.Pending))!
            .SetValue(controller, new PipelineApprovalDto
            {
                Id = 32,
                PipelineRunId = 40,
                StageName = "Confirm",
                Scope = scope,
                EnvironmentName = "prod"
            });

        var cut = Render<PipelineRunApprovalPanel>(parameters => parameters.Add(component => component.Approval, controller));

        var banner = cut.Find(".run-approval-banner");
        Assert.Contains(expected, banner.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(other, banner.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("ApprovalRequiredTitle", cut.Markup, StringComparison.Ordinal);
    }

    // Approvers receive the environment's instructions.

    [Fact]
    public async Task R1_24_TheApprover_SeesTheEnvironmentApprovalInstructions_BesideTheDecision()
    {
        _handler.SetJsonResponse("api/pipelines/runs/40/approvals", new List<PipelineApprovalDto>
        {
            new()
            {
                Id = 33, PipelineRunId = 40, StageName = "deploy", Scope = ApprovalScope.Environment,
                EnvironmentId = 3, EnvironmentName = "prod", Status = ApprovalStatus.Pending,
                EnvironmentInstructions = "Check the QA results before approving."
            }
        });
        var controller = new PipelineRunApprovalController(
            Services.GetRequiredService<ApiClient>(), Services.GetRequiredService<NotifyHelper>(), () => Task.CompletedTask);
        await controller.LoadAsync(
            new PipelineRunDto { Id = 40, Status = PipelineStatus.WaitingForApproval },
            Xunit.TestContext.Current.CancellationToken);

        var cut = Render<PipelineRunApprovalPanel>(parameters => parameters.Add(component => component.Approval, controller));

        var instructions = cut.Find(".run-approval-banner .approval-instructions");
        Assert.Contains("ApprovalInstructionsForApprover", instructions.TextContent, StringComparison.Ordinal);
        Assert.Contains("Check the QA results before approving.", instructions.TextContent, StringComparison.Ordinal);
        Assert.Contains("Approve", cut.Find(".run-approval-banner").TextContent, StringComparison.Ordinal);
        // The instructions travel with the approval: the approver needs no read access to the environment.
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("api/environments/3", StringComparison.Ordinal));
    }

    [Fact]
    public void R2_046_TheCommentField_AppearsOnRequest_BesideApproveAndReject()
    {
        var cut = Render<PipelineRunApprovalPanel>(parameters => parameters.Add(component => component.Approval, PendingApproval()));
        cut.WaitForAssertion(() => Assert.Contains("Approve", cut.Markup, StringComparison.Ordinal));

        Assert.Empty(cut.FindAll("textarea"));
        var add = cut.Find("button.approval-add-comment");
        Assert.Contains("omni-button--secondary", add.ClassName, StringComparison.Ordinal);
        Assert.Contains("ApprovalAddComment", add.TextContent, StringComparison.Ordinal);

        add.Click();

        Assert.Single(cut.FindAll("textarea"));
        Assert.Empty(cut.FindAll("button.approval-add-comment"));
        Assert.Single(cut.FindAll("button.omni-button--primary"));
    }

    [Fact]
    public void R2_095_OnAPhone_ApproveAndRejectCanShrinkToIcons_WithTheirNameAndTooltip_AndTheButtonsWrap()
    {
        // The responsive classes identify icon-only phone actions; actual geometry is covered by
        // the mobile approval-banner E2E scenario.
        var cut = Render<PipelineRunApprovalPanel>(parameters => parameters.Add(component => component.Approval, PendingApproval()));
        cut.WaitForAssertion(() => Assert.Contains("Approve", cut.Markup, StringComparison.Ordinal));

        Assert.Single(cut.FindAll(".omni-alert.run-approval-banner"));
        var decisions = cut.FindAll(".run-approval-banner button.approval-decision");
        Assert.Equal(2, decisions.Count);
        Assert.All(decisions, button =>
        {
            Assert.False(string.IsNullOrWhiteSpace(button.GetAttribute("aria-label")));
            Assert.Equal(button.GetAttribute("aria-label"), button.GetAttribute("title"));
            Assert.NotNull(button.QuerySelector(".omni-icon"));
        });
        Assert.Equal(["Approve", "Reject"], decisions.Select(button => button.GetAttribute("aria-label")));
        // Comment keeps its text: it does not carry the class that hides the label.
        Assert.DoesNotContain("approval-decision", cut.Find("button.approval-add-comment").ClassName, StringComparison.Ordinal);
        // The row of buttons wraps instead of widening the banner.
        Assert.Contains("omni-stack--wrap", decisions[0].ParentElement!.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void R2_021_WhenANewerVersionIsDeployed_TheApprovalWaitsForAReload()
    {
        _handler.SetRawResponse("appsettings.json", "{\"App\":{\"Version\":\"newer-version\"}}");
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        var cut = Render<PipelineRunApprovalPanel>(parameters => parameters.Add(component => component.Approval, PendingApproval()));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".approval-reload-first")));
        Assert.Contains("ApprovalReloadFirst", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Approve", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Reject", cut.Markup, StringComparison.Ordinal);

        cut.Find(".approval-reload-first button").Click();

        Assert.Contains(navigation.History, entry => entry.Options.ForceLoad);
    }

    [Fact]
    public void R2_021_TheLayoutsVersionMonitor_AlsoHoldsAnApprovalAlreadyShown()
    {
        var cut = Render<PipelineRunApprovalPanel>(parameters => parameters.Add(component => component.Approval, PendingApproval()));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".approval-reload-first")));

        cut.InvokeAsync(() => Services.GetRequiredService<Aetheus.Front.Components.Shared.ApplicationVersionState>().MarkNewVersionAvailable());

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".approval-reload-first")));
    }
}
