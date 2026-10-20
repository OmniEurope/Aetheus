// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Front.Components.Releases;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Releases;

/// <summary>
/// Recette R-13: a release whose recorded status is wrong (<c>c-424f31</c> left Failed while production
/// still serves it) is put back from the "More actions" menu of its page, through a dialog that asks the
/// status and the reason.
/// </summary>
public sealed class ReleaseStatusRestoreTests : BunitContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private BunitTestHelper.TestHandler _handler = null!;

    private void Setup(bool isAdmin)
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: isAdmin);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetPaginatedJsonResponse("api/git/repos", Array.Empty<GitLightRepoDto>());
    }

    private ImmediateDialogService DialogService => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private static ReleaseDto Stuck(ReleaseStatus status = ReleaseStatus.Failed) => new()
    {
        Id = 7,
        ProjectId = 1,
        ProjectName = "Aetheus",
        Version = "c-424f31",
        BranchName = "main",
        DetectedAt = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc),
        Status = status
    };

    private IRenderedComponent<ReleaseDetail> RenderPage(ReleaseDto release)
    {
        _handler.SetJsonResponse("api/releases/7", release);
        var cut = Render<ReleaseDetail>(p => p.Add(c => c.ReleaseId, 7));
        cut.WaitForState(() => cut.Markup.Contains("c-424f31"));
        return cut;
    }

    private RestoreReleaseStatusRequest LastRestoreBody()
    {
        var body = _handler.RequestDetails.Last(r => r.Method == "POST" && r.Url.Contains("api/releases/7/status")).Body;
        return JsonSerializer.Deserialize<RestoreReleaseStatusRequest>(body!, Json)!;
    }

    [Fact]
    public void R_13_TheRestore_SendsTheChosenStatusAndReason_AndShowsTheRestoredStatus()
    {
        Setup(isAdmin: true);
        DialogService.OpenResult = new RestoreReleaseStatusRequest { Status = ReleaseStatus.Deployed, Reason = "production serves it" };
        _handler.SetJsonResponse(HttpMethod.Post, "api/releases/7/status", Stuck(ReleaseStatus.Deployed));
        var cut = RenderPage(Stuck());

        cut.FindMenuItem("ReleaseStatusRestore")!.Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, r => r.Method == "POST" && r.Url.Contains("api/releases/7/status")));
        Assert.Equal(typeof(RestoreReleaseStatusDialog), DialogService.LastComponent);
        Assert.Equal(ReleaseStatus.Failed, DialogService.LastParameters!["CurrentStatus"]);
        var sent = LastRestoreBody();
        Assert.Equal(ReleaseStatus.Deployed, sent.Status);
        Assert.Equal("production serves it", sent.Reason);
        cut.WaitForAssertion(() => Assert.Contains("Enum_ReleaseStatus_Deployed", cut.Markup));
        Assert.DoesNotContain("Enum_ReleaseStatus_Failed", cut.Markup);
    }

    [Fact]
    public void R_13_ADismissedDialog_SendsNothing()
    {
        Setup(isAdmin: true);
        DialogService.OpenResult = null;
        var cut = RenderPage(Stuck());

        cut.FindMenuItem("ReleaseStatusRestore")!.Click();

        Assert.Equal(1, DialogService.OpenCount);
        Assert.DoesNotContain(_handler.RequestDetails, r => r.Url.Contains("api/releases/7/status"));
    }

    [Fact]
    public void R_13_ARefusedRestore_KeepsTheRecordedStatus()
    {
        Setup(isAdmin: true);
        DialogService.OpenResult = new RestoreReleaseStatusRequest { Status = ReleaseStatus.Deployed, Reason = "x" };
        _handler.SetJsonResponse(HttpMethod.Post, "api/releases/7/status", new ApiError { Message = "refused" }, HttpStatusCode.BadRequest);
        var cut = RenderPage(Stuck());

        cut.FindMenuItem("ReleaseStatusRestore")!.Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, r => r.Url.Contains("api/releases/7/status")));
        Assert.Contains("Enum_ReleaseStatus_Failed", cut.Markup);
        Assert.DoesNotContain("Enum_ReleaseStatus_Deployed", cut.Markup);
    }

    [Fact]
    public void R_13_WithoutProjectAdmin_TheRestoreIsNotOffered()
    {
        Setup(isAdmin: false);
        var cut = RenderPage(Stuck());

        Assert.Null(cut.FindMenuItem("ReleaseStatusRestore"));
    }

    [Fact]
    public void R_13_AnArchivedRelease_IsNotOfferedTheRestore()
    {
        Setup(isAdmin: true);
        var cut = RenderPage(Stuck(ReleaseStatus.Archived));

        Assert.Null(cut.FindMenuItem("ReleaseStatusRestore"));
    }

    // ---------- the dialog ----------

    private IRenderedComponent<RestoreReleaseStatusDialog> RenderDialog(ReleaseStatus current)
    {
        var cut = Render<RestoreReleaseStatusDialog>(p => p
            .Add(x => x.Version, "c-424f31")
            .Add(x => x.CurrentStatus, current));
        BunitTestHelper.Interactive(this);
        return cut;
    }

    [Fact]
    public void R_13_TheDialog_OffersTheRestorableStatusesButTheCurrentOne_DeployedFirst()
    {
        Setup(isAdmin: true);
        var cut = RenderDialog(ReleaseStatus.Failed);

        var options = cut.Find("#release-status-restore-target").QuerySelectorAll("option").Select(o => o.TextContent.Trim()).ToArray();
        Assert.Equal(["Enum_ReleaseStatus_Deployed", "Enum_ReleaseStatus_Superseded", "Enum_ReleaseStatus_Published"], options);
        Assert.Contains("ReleaseStatusRestoreDeployedHint", cut.Markup);
    }

    [Fact]
    public void R_13_TheDialog_WithoutReason_DoesNotClose()
    {
        Setup(isAdmin: true);
        var cut = RenderDialog(ReleaseStatus.Failed);

        cut.Find("form").Submit();

        Assert.Equal(0, DialogService.CloseCount);
    }

    [Fact]
    public void R_13_TheDialog_ClosesWithTheChosenStatusAndTheTrimmedReason()
    {
        Setup(isAdmin: true);
        var cut = RenderDialog(ReleaseStatus.Deployed);

        var select = cut.Find("#release-status-restore-target");
        var superseded = select.QuerySelectorAll("option").First(o => o.TextContent.Trim() == "Enum_ReleaseStatus_Superseded").GetAttribute("value");
        select.Change(superseded);
        cut.Find("#release-status-restore-reason").Input("  replaced by c-500000  ");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(1, DialogService.CloseCount));
        var request = Assert.IsType<RestoreReleaseStatusRequest>(DialogService.LastCloseResult);
        Assert.Equal(ReleaseStatus.Superseded, request.Status);
        Assert.Equal("replaced by c-500000", request.Reason);
    }
}
