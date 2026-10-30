// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Shared;
using Aetheus.Front.Components.Users;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Rendered ownership dialogs preview permission gaps before transfer and name the
/// successor during deactivation or deletion, exercising the API client request path.</summary>
public sealed class PipelineOwnershipDialogsTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    private static readonly List<PipelineOwnerCandidateDto> Candidates = [new(1, "admin"), new(2, "ops"), new(3, "root")];

    public PipelineOwnershipDialogsTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<NavigationManager>(), sp.GetRequiredService<IJSRuntime>()));
        _handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/ownership/candidates", Candidates);
    }

    private SpyDialogService Spy() => (SpyDialogService)Services.GetRequiredService<OmniDialogService>();

    private static PipelineOwnerTransferResultDto Result(bool preview, bool holdsAdmin) => new()
    {
        NewOwnerUsername = "ops",
        Preview = preview,
        Items =
        [
            new PipelineOwnerTransferItemDto
            {
                PipelineId = 10,
                PipelineName = "aetheus-candidate",
                PreviousOwnerUsername = "admin",
                NewOwnerHoldsServerAdmin = holdsAdmin,
                ServersMissingAdmin = holdsAdmin ? [] : ["vps-prod"],
            },
        ],
    };

    private IRenderedComponent<PipelineOwnerTransferDialog> RenderTransfer(string? defaultOwner = "ops") =>
        Render<PipelineOwnerTransferDialog>(p => p
            .Add(c => c.Scope, new PipelineOwnerTransferRequest { PipelineId = 10 })
            .Add(c => c.DefaultOwner, defaultOwner));

    /// <summary>Recette R-116: while the preview is read, OE's busy veil lies over the zone it will fill; no loader is added.</summary>
    [Fact]
    public void TransferDialog_WhileThePreviewIsRead_TheZoneIsVeiled_AndNoLoaderIsAdded()
    {
        var preview = new TaskCompletionSource<PipelineOwnerTransferResultDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Post, "api/pipelines/ownership/preview", _ => preview.Task);

        var cut = RenderTransfer();

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=owner-transfer-preview-pending]").GetAttribute("aria-busy")));
        Assert.Contains("omni-busy", cut.Find("[data-testid=owner-transfer-preview-pending]").ClassName, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".aetheus-loader-logo"));

        preview.SetResult(Result(preview: true, holdsAdmin: true));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=owner-transfer-preview-pending]")));
    }

    [Fact]
    public void TransferDialog_PreviewsTheDefaultOwner_AndWarnsWhereServerAdminIsMissing()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/ownership/preview", Result(preview: true, holdsAdmin: false));

        var cut = RenderTransfer();

        cut.WaitForAssertion(() => Assert.Contains("PipelineOwnerLacksServerAdminTitle", cut.Markup));
        Assert.Contains("aetheus-candidate : vps-prod", cut.Find(".pipeline-owner-missing-admin").TextContent);
        Assert.Contains(_handler.RequestDetails, r => r.Method == "POST" && r.Url.Contains("ownership/preview")
            && r.Body!.Contains("\"pipelineId\":10") && r.Body.Contains("\"newOwnerUsername\":\"ops\""));
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("ownership/transfer"));
    }

    [Fact]
    public void TransferDialog_Confirm_PostsTheTransfer_AndClosesWithItsResult()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/ownership/preview", Result(preview: true, holdsAdmin: true));
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/ownership/transfer", Result(preview: false, holdsAdmin: true));
        var cut = RenderTransfer();
        cut.WaitForAssertion(() => Assert.Contains("aetheus-candidate", cut.Find(".pipeline-owner-transfer-item").TextContent));
        Assert.DoesNotContain("PipelineOwnerLacksServerAdminTitle", cut.Markup);

        cut.FindAll("button").Single(b => b.TextContent.Contains("TransferOwnership")).Click();

        cut.WaitForAssertion(() => Assert.True(Spy().Closed));
        var result = Assert.IsType<PipelineOwnerTransferResultDto>(Spy().LastResult);
        Assert.False(result.Preview);
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("ownership/transfer"));
    }

    [Fact]
    public void TransferDialog_RefusedPreview_ShowsTheReason_AndKeepsTheTransferDisabled()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/ownership/preview",
            new ApiError { Message = "The new owner 'ops' is not an active user." }, HttpStatusCode.BadRequest);

        var cut = RenderTransfer();

        cut.WaitForAssertion(() => Assert.Contains("is not an active user", cut.Markup));
        Assert.True(cut.FindAll("button").Single(b => b.TextContent.Contains("TransferOwnership")).HasAttribute("disabled"));
    }

    private IRenderedComponent<UserPipelineHandoverDialog> RenderHandover(bool delete = false) =>
        Render<UserPipelineHandoverDialog>(p => p
            .Add(c => c.Username, "admin")
            .Add(c => c.Message, "DeactivateUserConfirm")
            .Add(c => c.Delete, delete)
            .Add(c => c.DefaultTarget, "root"));

    private void OwnedByAdmin(params string[] names) =>
        _handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/ownership/owned",
            names.Select((name, index) => new PipelineOwnershipDto { PipelineId = index + 1, PipelineName = name, OwnerUsername = "admin" }).ToList());

    [Fact]
    public void HandoverDialog_ListsTheOwnedPipelines_AndProposesTheActingAdministrator()
    {
        OwnedByAdmin("aetheus-candidate", "aetheus-deploy-prod");
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/ownership/preview", Result(preview: true, holdsAdmin: true));

        var cut = RenderHandover();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".user-owned-pipeline-item").Count));
        Assert.Contains(_handler.RequestDetails, r => r.Url.Contains("ownership/preview")
            && r.Body!.Contains("\"currentOwnerUsername\":\"admin\"") && r.Body.Contains("\"newOwnerUsername\":\"root\""));

        // The confirmation is enabled once the reads, and the default owner's preview when one runs, complete.
        cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").Single(b => b.TextContent.Contains("Deactivate")).HasAttribute("disabled")));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Deactivate")).Click();

        Assert.True(Spy().Closed);
        Assert.Equal(new UserPipelineHandover("root", false), Spy().LastResult);
    }

    [Fact]
    public void HandoverDialog_Forced_ClosesWithoutATarget()
    {
        OwnedByAdmin("aetheus-candidate");
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/ownership/preview", Result(preview: true, holdsAdmin: true));
        var cut = RenderHandover(delete: true);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".user-owned-pipeline-item")));

        cut.Find("input[type=checkbox]").Change(true);
        // The confirmation is enabled once the reads, and the default owner's preview when one runs, complete.
        cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").Single(b => b.TextContent.Contains("Delete")).HasAttribute("disabled")));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Delete")).Click();

        Assert.Equal(new UserPipelineHandover(null, true), Spy().LastResult);
    }

    [Fact]
    public void HandoverDialog_UserOwningNothing_ConfirmsDirectly()
    {
        OwnedByAdmin();

        var cut = RenderHandover();
        cut.WaitForAssertion(() => Assert.DoesNotContain("UserOwnsPipelinesWarning", cut.Markup));
        // The confirmation is enabled once the reads, and the default owner's preview when one runs, complete.
        cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").Single(b => b.TextContent.Contains("Deactivate")).HasAttribute("disabled")));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Deactivate")).Click();

        Assert.Equal(new UserPipelineHandover(null, false), Spy().LastResult);
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("ownership/preview"));
    }

    private static bool ConfirmDisabled(IRenderedComponent<UserPipelineHandoverDialog> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains("Deactivate")).HasAttribute("disabled");

    [Fact]
    public async Task HandoverDialog_SlowPipelineRead_SaysItIsLoading_AndRefusesTheConfirmation()
    {
        var release = new TaskCompletionSource<List<PipelineOwnershipDto>>();
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/pipelines/ownership/owned", _ => release.Task);

        var cut = RenderHandover();

        // The loading line waits for the read to be slow (a quick read made the dialog jump, read as a second dialog).
        Assert.Empty(cut.FindAll(".user-handover-phase"));
        Assert.True(ConfirmDisabled(cut));
        await cut.WaitForAssertionAsync(
            () => Assert.Contains("UserHandoverLoadingPipelines", cut.Find(".user-handover-phase").TextContent));
        Assert.True(ConfirmDisabled(cut));
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/ownership/preview", Result(preview: true, holdsAdmin: true));
        release.SetResult([new PipelineOwnershipDto { PipelineId = 1, PipelineName = "aetheus-candidate", OwnerUsername = "admin" }]);
        // The released list resumes on the thread pool, then the preview is read before the dialog
        // re-renders (two asynchronous hops; a loaded run failed with "Check count: 0"): wait with the
        // loaded-machine ceiling, not bUnit's implicit second.
        await cut.WaitForAssertionAsync(() => Assert.Empty(cut.FindAll(".user-handover-phase")));
        Assert.False(ConfirmDisabled(cut));
    }

    [Fact]
    public async Task HandoverDialog_PendingPipelineRead_ShowsNoLoadingLineNorLoader_BeforeTheNoticeDelay()
    {
        var release = new TaskCompletionSource<List<PipelineOwnershipDto>>();
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/pipelines/ownership/owned", _ => release.Task);

        var cut = RenderHandover();

        // The dialog keeps its final shape while a quick read is pending: no line or loader that would vanish.
        Assert.Empty(cut.FindAll(".user-handover-phase"));
        Assert.DoesNotContain("aetheus-loader", cut.Markup, StringComparison.Ordinal);
        release.SetResult([]);
        await cut.WaitForAssertionAsync(() => Assert.False(ConfirmDisabled(cut)));
        Assert.Empty(cut.FindAll(".user-handover-phase"));
    }

    [Fact]
    public void HandoverDialog_FailedPipelineRead_ShowsAnError_AndNeverLetsTheConfirmationThrough()
    {
        _handler.SetResponse(HttpMethod.Get, "api/pipelines/ownership/owned", HttpStatusCode.InternalServerError);

        var cut = RenderHandover();

        cut.WaitForAssertion(() => Assert.Contains("UserHandoverLoadFailed", cut.Markup));
        Assert.Empty(cut.FindAll(".user-handover-phase"));
        Assert.True(ConfirmDisabled(cut));
    }

    [Fact]
    public async Task HandoverDialog_SlowPreview_SaysTheRightsAreChecked_AndRefusesTheConfirmationUntilItEnds()
    {
        OwnedByAdmin("aetheus-candidate");
        var release = new TaskCompletionSource<PipelineOwnerTransferResultDto>();
        _handler.SetAsyncJsonResponse(HttpMethod.Post, "api/pipelines/ownership/preview", _ => release.Task);

        var cut = RenderHandover();

        await cut.WaitForAssertionAsync(() => Assert.Contains("UserHandoverCheckingRights", cut.Find(".user-handover-phase").TextContent));
        Assert.True(ConfirmDisabled(cut));
        release.SetResult(Result(preview: true, holdsAdmin: true));
        // The released preview resumes on the thread pool before the dialog re-renders: wait with the
        // loaded-machine ceiling, not bUnit's implicit second.
        await cut.WaitForAssertionAsync(() => Assert.Empty(cut.FindAll(".user-handover-phase")));
        Assert.False(ConfirmDisabled(cut));
    }

    [Fact]
    public void HandoverDialog_RefusedPreview_ShowsTheReason_AndRefusesTheConfirmation()
    {
        OwnedByAdmin("aetheus-candidate");
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/ownership/preview",
            new ApiError { Message = "The new owner 'root' is not an active user." }, HttpStatusCode.BadRequest);

        var cut = RenderHandover();

        cut.WaitForAssertion(() => Assert.Contains("is not an active user", cut.Markup));
        Assert.True(ConfirmDisabled(cut));
    }

    [Fact]
    public async Task HandoverDialog_FastTargetChange_IgnoresTheStalePreview()
    {
        OwnedByAdmin("aetheus-candidate");
        var pending = new Queue<TaskCompletionSource<PipelineOwnerTransferResultDto>>();
        var first = new TaskCompletionSource<PipelineOwnerTransferResultDto>();
        var second = new TaskCompletionSource<PipelineOwnerTransferResultDto>();
        pending.Enqueue(first);
        pending.Enqueue(second);
        _handler.SetAsyncJsonResponse(HttpMethod.Post, "api/pipelines/ownership/preview", _ => pending.Dequeue().Task);
        var cut = RenderHandover();
        await _handler.WhenRequestedAsync(r => r.Url.Contains("ownership/preview", StringComparison.Ordinal), 1, "preview 1");

        // Not awaited: the handler waits on the second preview, released below.
        var change = cut.InvokeAsync(() => cut.FindComponent<OmniDropDown<string>>().Instance.ValueChanged.InvokeAsync("ops"));
        await _handler.WhenRequestedAsync(r => r.Url.Contains("ownership/preview", StringComparison.Ordinal), 2, "preview 2");
        second.SetResult(Result(preview: true, holdsAdmin: false));
        await cut.WhenRenderedAsync(() => Assert.Contains("PipelineOwnerLacksServerAdminTitle", cut.Markup));
        first.SetResult(Result(preview: true, holdsAdmin: true));
        await change;
        // The stale answer completed synchronously into the page: its continuation was queued on the
        // renderer before this item, which therefore runs once the page has dealt with it.
        await cut.InvokeAsync(() => { });

        Assert.Contains("PipelineOwnerLacksServerAdminTitle", cut.Markup);
        Assert.False(ConfirmDisabled(cut));
    }

    [Fact]
    public void OwnerBadge_ShowsOnlyForAnInactiveOrMissingOwner()
    {
        var inactive = Render<PipelineOwnerBadge>(p => p.Add(c => c.OwnerUsername, "admin").Add(c => c.OwnerInactive, true));
        var missing = Render<PipelineOwnerBadge>(p => p.Add(c => c.OwnerUsername, null).Add(c => c.OwnerInactive, true));
        var active = Render<PipelineOwnerBadge>(p => p.Add(c => c.OwnerUsername, "ops").Add(c => c.OwnerInactive, false));

        Assert.Contains("PipelineOwnerInactive", inactive.Find(".pipeline-owner-inactive-badge").TextContent);
        Assert.Contains("PipelineOwnerMissing", missing.Find(".pipeline-owner-inactive-badge").TextContent);
        Assert.Empty(active.FindAll(".pipeline-owner-inactive-badge"));
    }
}
