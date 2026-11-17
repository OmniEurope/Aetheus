// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Analysis;
using Aetheus.Front.Components.Shared;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Analysis;

/// <summary>R-15: the CVE watch tab never concludes before it has been answered. While the overview and the advisories are on
/// their way the zone is under the one page veil, the content is mounted beneath it and no sentence says "no source", "all
/// covered" or "no CVE"; a failed read draws an error and none of those sentences. The API is a double that holds its answer:
/// these tests prove what the page says before, while and after the answer, not how long the server takes.</summary>
public sealed class CveWatchTabLoadingTests : BunitContext
{
    private const string NoSource = "CveWatchNoSource";
    private const string AllCovered = "CveWatchAllCovered";
    private const string NoMatch = "CveWatchNoMatch";

    private readonly BunitTestHelper.TestHandler _handler;

    public CveWatchTabLoadingTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        // R-41: the tab also reads the CVE of the servers' host packages.
        _handler.SetPaginatedJsonResponse("api/servers/host-vulnerabilities", Array.Empty<Aetheus.Shared.Components.Analysis.ServerHostFindingDto>());
    }

    private BusyVeil Veil => Services.GetRequiredService<BusyVeil>();

    private static CveWatchOverviewDto Overview() => new()
    {
        CriticalCount = 3,
        UncoveredProjects = [new() { ProjectId = 2, ProjectName = "Valnerya", OrganizationId = 20, OrganizationName = "Org B", Coverage = ProjectInventoryCoverage.Partial }],
        Sources = [new() { Source = "osv:NuGet", LastSuccessAt = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), IsStale = false }]
    };

    private static PaginatedResult<CveWatchAdvisoryGroupDto> Page(params CveWatchAdvisoryGroupDto[] groups) =>
        new() { Items = [.. groups], TotalCount = groups.Length, Page = 1, PageSize = 25 };

    private IRenderedComponent<CveWatchTab> RenderTab() => Render<CveWatchTab>();

    private static void AssertNoConclusion(IRenderedComponent<CveWatchTab> cut)
    {
        var text = cut.Find("[data-testid='cve-watch']").TextContent;
        Assert.DoesNotContain(NoSource, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AllCovered, text, StringComparison.Ordinal);
        Assert.DoesNotContain(NoMatch, text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhileTheOverviewAndTheAdvisoriesAreOnTheirWay_TheZoneIsVeiled_AndNoEmptySentenceIsDrawn()
    {
        var overview = new TaskCompletionSource<CveWatchOverviewDto>();
        var advisories = new TaskCompletionSource<PaginatedResult<CveWatchAdvisoryGroupDto>>();
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/analysis/cve-watch/overview", _ => overview.Task);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/analysis/cve-watch/advisories", _ => advisories.Task);

        var cut = RenderTab();

        cut.WaitForAssertion(() =>
        {
            Assert.True(Veil.IsBusy);
            // The content stays mounted under the veil: the tiles, the grid, the sources and the uncovered cards.
            Assert.NotNull(cut.Find("[data-testid='cve-tile-critical']"));
            Assert.NotNull(cut.Find(".cve-watch-grid"));
            Assert.NotNull(cut.Find("[data-testid='cve-sources']"));
            Assert.NotNull(cut.Find("[data-testid='cve-uncovered']"));
            Assert.Contains("-", cut.Find("[data-testid='cve-tile-critical']").TextContent, StringComparison.Ordinal);
            AssertNoConclusion(cut);
        });
        // The veil is the page's one: no loader of its own is added to the tab.
        Assert.Empty(cut.FindAll(".aetheus-loader-logo"));

        overview.SetResult(Overview());
        advisories.SetResult(Page());

        cut.WaitForAssertion(() =>
        {
            Assert.False(Veil.IsBusy);
            Assert.Contains("osv:NuGet", cut.Find("[data-testid='cve-sources']").TextContent, StringComparison.Ordinal);
            Assert.Contains("Valnerya", cut.Find("[data-testid='cve-uncovered']").TextContent, StringComparison.Ordinal);
            // An answered watch with nothing found says so (the coverage gap makes it "no proof", not "none").
            Assert.Contains(NoMatch, cut.Find("[data-testid='cve-watch']").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void WhileOnlyTheAdvisoriesAreOnTheirWay_NoCveSentenceIsDrawn_EvenWithTheOverviewAnswered()
    {
        var advisories = new TaskCompletionSource<PaginatedResult<CveWatchAdvisoryGroupDto>>();
        _handler.SetJsonResponse("api/analysis/cve-watch/overview", Overview());
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/analysis/cve-watch/advisories", _ => advisories.Task);

        var cut = RenderTab();

        cut.WaitForAssertion(() =>
        {
            Assert.True(Veil.IsBusy);
            Assert.DoesNotContain(NoMatch, cut.Find("[data-testid='cve-watch']").TextContent, StringComparison.Ordinal);
        });

        advisories.SetResult(Page());
        cut.WaitForAssertion(() => Assert.Contains(NoMatch, cut.Find("[data-testid='cve-watch']").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void WhenTheOverviewAnswersWithNothingToWorryAbout_TheEmptySentencesAppear_OnlyThen()
    {
        _handler.SetJsonResponse("api/analysis/cve-watch/overview", new CveWatchOverviewDto());
        _handler.SetPaginatedJsonResponse("api/analysis/cve-watch/advisories", Array.Empty<CveWatchAdvisoryGroupDto>());

        var cut = RenderTab();

        cut.WaitForAssertion(() =>
        {
            Assert.False(Veil.IsBusy);
            var text = cut.Find("[data-testid='cve-watch']").TextContent;
            Assert.Contains(NoSource, text, StringComparison.Ordinal);
            Assert.Contains(AllCovered, text, StringComparison.Ordinal);
            Assert.Contains(NoMatch, text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void WhenTheOverviewFails_TheTabShowsAnError_AndNoEmptySentence()
    {
        _handler.SetResponse(HttpMethod.Get, "api/analysis/cve-watch/overview", System.Net.HttpStatusCode.InternalServerError);
        _handler.SetPaginatedJsonResponse("api/analysis/cve-watch/advisories", Array.Empty<CveWatchAdvisoryGroupDto>());

        var cut = RenderTab();

        cut.WaitForAssertion(() =>
        {
            Assert.False(Veil.IsBusy);
            Assert.Contains("LoadFailed", cut.Find("[data-testid='cve-watch']").TextContent, StringComparison.Ordinal);
            AssertNoConclusion(cut);
        });
    }

    [Fact]
    public void WhenTheAdvisoriesFail_TheTabShowsAnError_AndNoEmptySentence()
    {
        _handler.SetJsonResponse("api/analysis/cve-watch/overview", Overview());
        _handler.SetResponse(HttpMethod.Get, "api/analysis/cve-watch/advisories", System.Net.HttpStatusCode.InternalServerError);

        var cut = RenderTab();

        cut.WaitForAssertion(() =>
        {
            Assert.False(Veil.IsBusy);
            Assert.Contains("LoadFailed", cut.Find("[data-testid='cve-watch']").TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain(NoMatch, cut.Find("[data-testid='cve-watch']").TextContent, StringComparison.Ordinal);
        });
    }
}
