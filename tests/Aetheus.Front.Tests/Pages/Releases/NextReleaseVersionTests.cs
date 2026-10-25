// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Front.Components.Releases;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Bunit.Rendering;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Releases;

/// <summary>
/// An administrator fixes the next version of a project from the project's release page: sync stays the
/// main action, « Fixer la prochaine version » is in its menu, and the dialog sends the series and the
/// release number (1.4 and 0 make the next release 1.4.0).
/// </summary>
public sealed class NextReleaseVersionTests : BunitContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private BunitTestHelper.TestHandler _handler = null!;

    private ImmediateDialogService DialogService => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private void Setup(bool isAdmin)
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: isAdmin);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetPaginatedJsonResponse("api/releases", Array.Empty<ReleaseDto>());
        _handler.SetJsonResponse("api/projects/1/release-series", new List<ProjectReleaseSeriesDto>
        {
            new() { Series = "1.3", NextNumber = 17 }
        });
    }

    private IRenderedComponent<ContainerFragment> RenderInProjectHost() =>
        Render(builder =>
        {
            builder.OpenComponent<SectionOutlet>(0);
            builder.AddAttribute(1, nameof(SectionOutlet.SectionName), PageHeaderActions.SectionName);
            builder.CloseComponent();
            builder.OpenComponent<ReleasesList>(2);
            builder.AddAttribute(3, nameof(ReleasesList.ProjectId), (int?)1);
            builder.CloseComponent();
        });

    private static void OpenSplitMenu(IRenderedComponent<ContainerFragment> cut)
    {
        cut.WaitForElement(".omni-split-button__toggle").Click();
        cut.WaitForElement(".omni-menu__item");
    }

    [Fact]
    public void AnAdministrator_SeesSyncAsTheMainActionAndTheNextVersionInItsMenu()
    {
        Setup(isAdmin: true);
        var cut = RenderInProjectHost();

        var main = cut.WaitForElement(".omni-split-button__main");
        Assert.Equal("SyncReleases", main.GetAttribute("aria-label"));
        OpenSplitMenu(cut);
        Assert.Contains(cut.FindAll(".omni-menu__item"), item => item.TextContent.Contains("SetNextVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void AUserWhoIsNotProjectAdmin_HasOnlyTheSyncButton()
    {
        Setup(isAdmin: false);
        var cut = RenderInProjectHost();

        cut.WaitForState(() => cut.Markup.Contains("SyncReleases", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".omni-split-button__toggle"));
        Assert.DoesNotContain("SetNextVersion", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ChoosingTheMenuEntry_OffersTheKnownSeries_AndTheProjectToTheDialog()
    {
        Setup(isAdmin: true);
        DialogService.OpenResult = new ProjectReleaseSeriesDto { Series = "1.4", NextNumber = 0 };
        var cut = RenderInProjectHost();

        OpenSplitMenu(cut);
        cut.FindAll(".omni-menu__item").First(item => item.TextContent.Contains("SetNextVersion", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(typeof(NextReleaseVersionDialog), DialogService.LastComponent));
        var known = Assert.IsType<List<ProjectReleaseSeriesDto>>(DialogService.LastParameters!["KnownSeries"]);
        Assert.Equal("1.3", known.Single().Series);
        Assert.Equal(1, DialogService.LastParameters!["ProjectId"]);
        Assert.DoesNotContain(_handler.RequestDetails, r => r.Method == "PUT");
    }

    [Fact]
    public void ADismissedDialog_SendsNothing()
    {
        Setup(isAdmin: true);
        DialogService.OpenResult = null;
        var cut = RenderInProjectHost();

        OpenSplitMenu(cut);
        cut.FindAll(".omni-menu__item").First(item => item.TextContent.Contains("SetNextVersion", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(typeof(NextReleaseVersionDialog), DialogService.LastComponent));
        Assert.DoesNotContain(_handler.RequestDetails, r => r.Method == "PUT");
    }

    [Fact]
    public void TheDialog_StartsOnTheNewestSeriesWithItsNextNumber_AndPreviewsTheVersion()
    {
        Setup(isAdmin: true);
        var cut = Render<NextReleaseVersionDialog>(p => p.Add(c => c.KnownSeries,
            [new ProjectReleaseSeriesDto { Series = "1.3", NextNumber = 17 }]));

        Assert.Equal("1.3", cut.Find("#next-version-series").GetAttribute("value"));
        Assert.Equal("17", cut.Find("#next-version-number").GetAttribute("value"));
        Assert.Contains("NextVersionPreview", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDialog_ANewSeriesAndNumber_AreSentToTheApi_AndTheDialogClosesWithTheSavedSeries()
    {
        Setup(isAdmin: true);
        _handler.SetJsonResponse(HttpMethod.Put, "api/projects/1/release-series",
            new ProjectReleaseSeriesDto { Series = "1.4", NextNumber = 0 });
        var cut = RenderDialog();

        BunitTestHelper.Interactive(this);
        cut.Find("#next-version-series").Input("1.4");
        cut.Find("#next-version-number").Change("0");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(1, DialogService.CloseCount));
        var saved = Assert.IsType<ProjectReleaseSeriesDto>(DialogService.LastCloseResult);
        Assert.Equal("1.4", saved.Series);
        var body = _handler.RequestDetails.Last(r => r.Method == "PUT" && r.Url.Contains("api/projects/1/release-series")).Body;
        var sent = JsonSerializer.Deserialize<SetNextReleaseNumberRequest>(body!, Json)!;
        Assert.Equal("1.4", sent.Series);
        Assert.Equal(0, sent.NextNumber);
    }

    [Fact]
    public void TheDialog_ANumberAnEarlierRunTook_StaysOpenAndSaysSo_WithTheNextFreeNumber()
    {
        Setup(isAdmin: true);
        _handler.SetJsonResponse(HttpMethod.Put, "api/projects/1/release-series",
            new { message = "Version 1.3.5 was already taken by a run of this project." }, HttpStatusCode.Conflict);
        var cut = RenderDialog();

        BunitTestHelper.Interactive(this);
        cut.Find("#next-version-number").Change("5");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("NextVersionAlreadyUsedSuggest", cut.Markup, StringComparison.Ordinal));
        Assert.NotEmpty(cut.FindAll(".omni-alert"));
        Assert.Equal(0, DialogService.CloseCount);
        Assert.Null(DialogService.LastCloseResult);
        Assert.NotNull(cut.Find("form"));
    }

    [Fact]
    public void TheDialog_AConflictOnAnUnknownSeries_SaysTheNumberWasUsedWithoutSuggestion()
    {
        Setup(isAdmin: true);
        _handler.SetJsonResponse(HttpMethod.Put, "api/projects/1/release-series",
            new { message = "taken" }, HttpStatusCode.Conflict);
        var cut = RenderDialog();

        BunitTestHelper.Interactive(this);
        cut.Find("#next-version-series").Input("1.9");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("NextVersionAlreadyUsed", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("NextVersionAlreadyUsedSuggest", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(0, DialogService.CloseCount);
    }

    private IRenderedComponent<NextReleaseVersionDialog> RenderDialog() =>
        Render<NextReleaseVersionDialog>(p => p
            .Add(c => c.ProjectId, 1)
            .Add(c => c.KnownSeries, [new ProjectReleaseSeriesDto { Series = "1.3", NextNumber = 17 }]));

    [Fact]
    public void TheDialog_ASeriesThatIsNotMajorMinor_IsNotSubmitted()
    {
        Setup(isAdmin: true);
        var cut = Render<NextReleaseVersionDialog>();

        BunitTestHelper.Interactive(this);
        cut.Find("#next-version-series").Input("1.4.0");
        cut.Find("form").Submit();

        Assert.Null(DialogService.LastCloseResult);
        Assert.Equal(0, DialogService.CloseCount);
    }
}
