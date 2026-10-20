// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Aetheus.Front.Tests.TestDoubles;
using AngleSharp.Dom;
using Bunit;
using Bunit.Rendering;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// The three release actions that go through a dialog: triggering a build, promoting, and rolling
/// back. All of them were unreachable in tests because the real dialog never completes under
/// bUnit, so the rules that matter were unprotected: a rollback must be refused outright when the
/// preview says it cannot restore anything, and a promotion must not happen without a confirmation.
/// </summary>
public sealed class ReleasesListActionTests : BunitContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public ReleasesListActionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "aetheus", Status = ProjectStatus.Active }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/pipelines", new PaginatedResult<PipelineDto>
        {
            Items = [new PipelineDto { Id = 3, Name = "deploy", ProjectId = 1 }],
            TotalCount = 1
        });
    }

    private void WireReleases(params ReleaseDto[] releases) =>
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>
        {
            Items = [.. releases],
            TotalCount = releases.Length
        });

    private static ReleaseDto Release(
        int id = 1, string version = "1.0.0", ReleaseStatus status = ReleaseStatus.Published) =>
        new()
        {
            Id = id,
            ProjectId = 1,
            ProjectName = "aetheus",
            Version = version,
            Status = status,
            PublishedAt = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)
        };

    private IRenderedComponent<ReleasesList> RenderList(params ReleaseDto[] releases)
    {
        WireReleases(releases);
        var cut = Render<ReleasesList>();
        cut.WaitForState(
            () => cut.Markup.Contains(releases[0].Version, StringComparison.Ordinal));
        return cut;
    }

    /// <summary> Inside a project the sync action is contributed to the host's header outlet, so
    /// the list is rendered next to a <see cref="SectionOutlet"/> carrying that section name,
    /// exactly as the project page hosts it. </summary>
    private IRenderedComponent<ContainerFragment> RenderInProjectHost(int projectId) =>
        Render(builder =>
        {
            builder.OpenComponent<SectionOutlet>(0);
            builder.AddAttribute(1, nameof(SectionOutlet.SectionName), PipelinesList.HeaderActionsSection);
            builder.CloseComponent();
            builder.OpenComponent<ReleasesList>(2);
            builder.AddAttribute(3, nameof(ReleasesList.ProjectId), (int?)projectId);
            builder.CloseComponent();
        });

    /// <summary>The row action titled <paramref name="title"/>: a visible button, else (STD-BTN detection 11)
    /// an entry of the row's "More actions" menu, opened the way a user does.</summary>
    private static IElement Action<TComponent>(IRenderedComponent<TComponent> cut, string title)
        where TComponent : IComponent
    {
        static bool Titled(IElement element, string title) =>
            string.Equals(element.GetAttribute("title"), title, StringComparison.Ordinal);

        return cut.FindAll("button").FirstOrDefault(button => Titled(button, title))
            ?? (cut.FindMenuItem(title) is { } item && Titled(item, title) ? item : null)
            ?? throw new InvalidOperationException($"No row action titled {title}.");
    }

    private bool Sent(string method, string urlContains) =>
        _handler.Requests.Any(request => request.Method == method
            && request.Url.Contains(urlContains, StringComparison.Ordinal));

    private T LastBody<T>(string method, string urlContains)
    {
        var body = _handler.RequestDetails
            .Last(request => request.Method == method
                && request.Url.Contains(urlContains, StringComparison.Ordinal))
            .Body;
        return JsonSerializer.Deserialize<T>(body!, Json)!;
    }

    // ---------- trigger build ----------

    [Fact]
    public void TriggeringABuildSendsTheSelectedPipeline()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/releases/1/build", Release());
        _dialog.OpenResult = 3;
        var cut = RenderList(Release(status: ReleaseStatus.Detected));

        Action(cut, "TriggerBuild").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/releases/1/build")));
        Assert.Equal(3, LastBody<TriggerReleaseBuildRequest>("POST", "api/releases/1/build").PipelineId);
    }

    [Fact]
    public void CancellingThePipelineChoiceSendsNothing()
    {
        _dialog.OpenResult = null;
        var cut = RenderList(Release(status: ReleaseStatus.Detected));

        Action(cut, "TriggerBuild").Click();

        Assert.False(Sent("POST", "api/releases/1/build"));
    }

    [Fact]
    public void TriggeringABuildWithNoPipelineAtAllNeverOpensTheChooser()
    {
        _handler.SetJsonResponse("api/pipelines", new PaginatedResult<PipelineDto> { Items = [], TotalCount = 0 });
        var cut = RenderList(Release(status: ReleaseStatus.Detected));

        Action(cut, "TriggerBuild").Click();

        cut.WaitForAssertion(() => Assert.Equal(0, _dialog.OpenCount));
        Assert.False(Sent("POST", "api/releases/1/build"));
    }

    // ---------- relaunch an archived release from its commit (decision D2) ----------

    private const string ArchivedCommit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void RelaunchingAnArchivedReleaseSendsTheChosenPipeline_ThenOpensTheRun()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/releases/1/relaunch", new PipelineRunDto { Id = 77 });
        _dialog.OpenResult = 3;
        var cut = RenderList(Release(status: ReleaseStatus.Archived) with { CommitHash = ArchivedCommit });

        Action(cut, "RelaunchFromCommit").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/releases/1/relaunch")));
        Assert.Equal(3, LastBody<TriggerReleaseBuildRequest>("POST", "api/releases/1/relaunch").PipelineId);
        Assert.True(Sent("GET", "projectId=1"));
        cut.WaitForAssertion(() => Assert.EndsWith("/pipelines/runs/77",
            Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal));
    }

    [Fact]
    public void AnArchivedReleaseWithoutAFullCommit_CannotBeRelaunched()
    {
        var cut = RenderList(Release(status: ReleaseStatus.Archived) with { CommitHash = "abc1234" });

        var button = Action(cut, "RelaunchFromCommitUnavailable");

        Assert.True(button.HasAttribute("disabled"));
        Assert.False(Sent("POST", "api/releases/1/relaunch"));
    }

    // ---------- promote ----------

    [Fact]
    public void PromotingIsGatedByAConfirmation()
    {
        _dialog.ConfirmResult = false;
        var cut = RenderList(Release());

        Action(cut, "Promote").Click();

        Assert.False(Sent("POST", "api/releases/1/promote"));
    }

    [Fact]
    public void ConfirmingThePromotionCallsTheEndpoint()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/releases/1/promote", Release());
        _dialog.ConfirmResult = true;
        var cut = RenderList(Release());

        Action(cut, "Promote").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/releases/1/promote")));
    }

    // ---------- rollback ----------

    [Fact]
    public void RollingBackRefusesWhenThePreviewSaysItCannotRestoreAnything()
    {
        _handler.SetJsonResponse("api/releases/1/rollback-preview", new ReleaseRollbackPreviewDto
        {
            CanRollback = false,
            Reason = "The previous release artifact is no longer available in storage."
        });
        var cut = RenderList(Release());

        Action(cut, "Rollback").Click();

        cut.WaitForAssertion(
            () => Assert.True(Sent("GET", "api/releases/1/rollback-preview")));
        Assert.Equal(0, _dialog.OpenCount);
        Assert.False(Sent("POST", "api/releases/1/rollback"));
    }

    [Fact]
    public void RollingBackRefusesAPreviewThatNamesNoTargetVersion()
    {
        _handler.SetJsonResponse("api/releases/1/rollback-preview", new ReleaseRollbackPreviewDto
        {
            CanRollback = true,
            TargetVersion = "   "
        });
        var cut = RenderList(Release());

        Action(cut, "Rollback").Click();

        cut.WaitForAssertion(
            () => Assert.True(Sent("GET", "api/releases/1/rollback-preview")));
        Assert.False(Sent("POST", "api/releases/1/rollback"));
    }

    [Fact]
    public void RollingBackOpensTheDialogWithTheTargetVersionAndSendsWhatItReturns()
    {
        _handler.SetJsonResponse("api/releases/1/rollback-preview", new ReleaseRollbackPreviewDto
        {
            CanRollback = true,
            TargetVersion = "0.9.0",
            TargetPublishedAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc)
        });
        _handler.SetJsonResponse(HttpMethod.Post, "api/releases/1/rollback", new ReleaseRollbackDto
        {
            Id = 9,
            SourceReleaseId = 1,
            TargetReleaseId = 2
        });
        _dialog.OpenResult = new RollbackReleaseRequest { PipelineId = 3, RestoreDatabase = false };
        var cut = RenderList(Release());

        Action(cut, "Rollback").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/releases/1/rollback")));
        Assert.Equal("0.9.0", Assert.Contains("TargetVersion", _dialog.LastParameters!));
        Assert.Equal("1.0.0", Assert.Contains("SourceVersion", _dialog.LastParameters!));
        Assert.Equal(3, LastBody<RollbackReleaseRequest>("POST", "api/releases/1/rollback").PipelineId);
    }

    [Fact]
    public void CancellingTheRollbackDialogSendsNothing()
    {
        _handler.SetJsonResponse("api/releases/1/rollback-preview", new ReleaseRollbackPreviewDto
        {
            CanRollback = true,
            TargetVersion = "0.9.0"
        });
        _dialog.OpenResult = null;
        var cut = RenderList(Release());

        Action(cut, "Rollback").Click();

        cut.WaitForAssertion(
            () => Assert.True(Sent("GET", "api/releases/1/rollback-preview")));
        Assert.False(Sent("POST", "api/releases/1/rollback"));
    }

    /// <summary>
    /// Documents the current behaviour rather than a desired one: the preview call is NOT wrapped in
    /// a try/catch, so a transport failure surfaces to the renderer instead of degrading into the
    /// "rollback unavailable" toast. The rollback itself is still never sent, which is what matters
    /// for safety; the missing guard is a separate UX decision, not something this test invents.
    /// </summary>
    [Fact]
    public void RollingBackSurfacesAFailedPreviewInsteadOfSendingTheRollback()
    {
        _handler.SetResponse(HttpMethod.Get, "api/releases/1/rollback-preview", HttpStatusCode.NotFound);
        var cut = RenderList(Release());

        Assert.ThrowsAny<Exception>(() => Action(cut, "Rollback").Click());

        Assert.True(Sent("GET", "api/releases/1/rollback-preview"));
        Assert.False(Sent("POST", "api/releases/1/rollback"));
    }

    // ---------- refresh and sync ----------

    [Fact]
    public void HasNoRefreshButton_TheListFollowsTheReleasesHub()
    {
        // The releases hub keeps the list current, so the Refresh button is gone.
        var cut = RenderList(Release());

        Assert.DoesNotContain(cut.FindAll("button"), button =>
            button.GetAttribute("aria-label") == "Refresh" || button.GetAttribute("title") == "Refresh");
    }

    [Fact]
    public void SyncingInsideAProjectPostsToThatProject()
    {
        WireReleases(Release());
        _handler.SetJsonResponse(HttpMethod.Post, "api/releases/sync/1", new List<ReleaseDto> { Release(2, "1.1.0") });
        var cut = RenderInProjectHost(1);
        cut.WaitForState(() => cut.Markup.Contains("1.0.0", StringComparison.Ordinal));

        Action(cut, "SyncReleases").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/releases/sync/1")));
    }

    [Fact]
    public void SyncingReportsAnEmptyResultWithoutFailing()
    {
        WireReleases(Release());
        _handler.SetJsonResponse(HttpMethod.Post, "api/releases/sync/1", new List<ReleaseDto>());
        var cut = RenderInProjectHost(1);
        cut.WaitForState(() => cut.Markup.Contains("1.0.0", StringComparison.Ordinal));

        Action(cut, "SyncReleases").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/releases/sync/1")));
        Assert.Contains("1.0.0", cut.Markup);
    }
    // Grade and redeployment.

    [Fact]
    public void AReleaseWithoutASealedGradeReadsNotQualified()
    {
        var cut = RenderList(
            Release(id: 1, version: "c-sealed") with { AssuranceGrade = AnalysisGrade.C },
            Release(id: 2, version: "c-fast"));

        Assert.Equal("C", Assert.Single(cut.FindAll(".grade-badge")).TextContent.Trim());
        Assert.Contains("ReleaseNotQualified", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void RedeployingThePreviousDeploymentRunsTheDeployPipelineWithItsVersion()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/40/run", new PipelineRunDto { Id = 77, PipelineId = 40 });
        _dialog.ConfirmResult = true;
        var cut = RenderList(Release(id: 2, version: "c-previous", status: ReleaseStatus.Superseded) with
        {
            IsPreviousDeployment = true,
            RedeployPipelineId = 40
        });

        Action(cut, "Redeploy").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/pipelines/40/run")));
        Assert.Equal("c-previous", LastBody<Dictionary<string, string>>("POST", "api/pipelines/40/run")["candidateVersion"]);
    }

    [Fact]
    public void APreviousDeploymentTheGateWouldRefuseIsShownDisabled()
    {
        _dialog.ConfirmResult = true;
        var cut = RenderList(Release(id: 2, version: "c-fast", status: ReleaseStatus.Superseded) with
        {
            IsPreviousDeployment = true
        });

        Assert.True(Action(cut, "RedeployUnavailable").HasAttribute("disabled"));
        Assert.False(Sent("POST", "/run"));
    }

    /// <summary>Reverting runs the project's revert pipeline without a parameter because the host
    /// owns the retained colour and release.</summary>
    [Fact]
    public void TheLiveReleaseReturnsToNMinusOneThroughTheRevertPipeline()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/55/run", new PipelineRunDto { Id = 78, PipelineId = 55 });
        _dialog.ConfirmResult = true;
        var cut = RenderList(Release(id: 3, version: "c-live", status: ReleaseStatus.Deployed) with { RevertPipelineId = 55 });

        Action(cut, "RevertToPrevious").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/pipelines/55/run")));
        Assert.DoesNotContain("candidateVersion", LastBody<Dictionary<string, string>>("POST", "api/pipelines/55/run").Keys);
    }

    [Fact]
    public void WithoutARevertPipelineTheLiveReleaseOffersNoQuickReturn()
    {
        var cut = RenderList(Release(id: 3, version: "c-live", status: ReleaseStatus.Deployed));

        Assert.DoesNotContain("RevertToPrevious", cut.Markup, StringComparison.Ordinal);
        Assert.Null(cut.FindMenuItem("RevertToPrevious"));
    }

    [Fact]
    public void RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        var cut = RenderList(Release());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 2);
    }
}
