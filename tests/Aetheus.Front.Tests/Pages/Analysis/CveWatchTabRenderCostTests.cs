// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Analysis;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Bunit;
using Microsoft.AspNetCore.Components;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Analysis;

/// <summary>R-15: what the CVE watch tab mounts for a full page of 25 CVE of 3 projects each. bUnit counts components and DOM
/// elements on the .NET side; it does NOT time the browser (WebAssembly runs these renders far slower than the test host), so
/// these numbers are a proxy for the work done on the main thread, never its duration. The render count bUnit reports for a
/// child is not that child's own renders, so none is asserted here.</summary>
public sealed class CveWatchTabRenderCostTests : BunitContext
{
    private readonly ITestOutputHelper _output;
    private readonly BunitTestHelper.TestHandler _handler;

    public CveWatchTabRenderCostTests(ITestOutputHelper output)
    {
        _output = output;
        _handler = BunitTestHelper.RegisterServices(this);
        // R-41: the tab also reads the CVE of the servers' host packages.
        _handler.SetPaginatedJsonResponse("api/servers/host-vulnerabilities", Array.Empty<Aetheus.Shared.Components.Analysis.ServerHostFindingDto>());
    }

    private static List<CveWatchAdvisoryGroupDto> FullPage() =>
    [
        .. Enumerable.Range(1, 25).Select(index => new CveWatchAdvisoryGroupDto
        {
            AdvisoryId = $"GHSA-{index:0000}-bbbb-cccc",
            CveId = $"CVE-2026-{21000 + index}",
            Summary = $"Summary of the advisory {index}.",
            Severity = index % 4 == 0 ? AnalysisSeverity.Critical : AnalysisSeverity.High,
            SeverityPublished = index % 3 != 0,
            ExploitedSince = index % 5 == 0 ? new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc) : null,
            Matches =
            [
                .. Enumerable.Range(1, 3).Select(project => new CveWatchMatchDto
                {
                    MatchId = index * 10 + project,
                    ProjectId = project,
                    ProjectName = $"Project {project}",
                    PackageName = $"Contoso.Package{index}",
                    Version = "1.0.0",
                    FixedVersion = project == 1 ? null : "1.0.3",
                    Status = VulnerabilityMatchStatus.Affected,
                    FirstMatchedAt = new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc)
                })
            ]
        })
    ];

    private static IReadOnlyList<AnalysisPortfolioProjectDto> Projects() =>
    [
        .. Enumerable.Range(1, 40).Select(index => new AnalysisPortfolioProjectDto
        {
            ProjectId = index,
            ProjectName = $"Project {index}",
            OrganizationId = 10 + (index % 4),
            OrganizationName = $"Org {index % 4}"
        })
    ];

    private static CveWatchOverviewDto FullOverview() => new()
    {
        CriticalCount = 6,
        ExploitedCount = 5,
        WithoutFixCount = 25,
        UncoveredProjects =
        [
            .. Enumerable.Range(1, 9).Select(index => new CveWatchUncoveredProjectDto
            {
                ProjectId = index,
                ProjectName = $"Project {index}",
                OrganizationId = 10,
                OrganizationName = "Org 0",
                Coverage = index % 2 == 0 ? ProjectInventoryCoverage.Partial : ProjectInventoryCoverage.None
            })
        ],
        Sources =
        [
            .. Enumerable.Range(1, 21).Select(index => new CveWatchSourceDto
            {
                Source = $"osv:Source{index}",
                LastSuccessAt = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc),
                IsStale = index % 7 == 0
            })
        ]
    };

    private IRenderedComponent<CveWatchTab> RenderFullTab(IReadOnlyList<AnalysisPortfolioProjectDto>? projects = null)
    {
        _handler.SetJsonResponse("api/analysis/cve-watch/overview", FullOverview());
        _handler.SetJsonResponse("api/analysis/cve-watch/advisories",
            new PaginatedResult<CveWatchAdvisoryGroupDto> { Items = FullPage(), TotalCount = 120, Page = 1, PageSize = 25 });
        var cut = Render<CveWatchTab>(parameters => parameters.Add(tab => tab.Projects, projects ?? Projects()));
        // The grid is virtual: it draws the first window of rows, not the 25 of the page.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".cve-watch-grid .cve-watch-cve-id")));
        return cut;
    }

    [Fact]
    public void AFullPageOfCollapsedCve_MountsNoDetail_AndCountsWhatItMounts()
    {
        var cut = RenderFullTab();

        var components = cut.FindComponents<IComponent>();
        var byType = components.GroupBy(component => component.Instance.GetType().Name.Split('`')[0])
            .OrderByDescending(group => group.Count()).Take(12).Select(group => $"{group.Key}={group.Count()}");
        _output.WriteLine($"rows={cut.FindAll(".cve-watch-grid .cve-watch-cve-id").Count}; components={components.Count}; top: {string.Join(", ", byType)}");
        int Elements(string selector) => cut.FindAll(selector).Count;
        _output.WriteLine($"elements: total={Elements("*")}, options={Elements("[role='option']")}, grid={Elements(".cve-watch-grid *")}, sources={Elements("[data-testid='cve-sources'] *")}, uncovered={Elements("[data-testid='cve-uncovered'] *")}, filters={Elements(".cve-watch-filters *")}");

        // A collapsed CVE mounts nothing of its detail: no project card group, no timeline (OE draws a detail row only when open).
        Assert.Empty(cut.FindComponents<OmniSelectableCardGroup<long, long?>>());
        Assert.Empty(cut.FindComponents<OmniTimeline>());
    }

    /// <summary>The organization and project dropdowns are handed the same option lists at every render of the tab (it renders
    /// twice for each read, and again whenever its page does): a list rebuilt at each pass is a changed parameter for the
    /// dropdown, and 40 projects sorted and mapped again for nothing.</summary>
    [Fact]
    public void TheFilterOptions_AreTheSameLists_AtEveryRender()
    {
        var projects = Projects();
        var cut = RenderFullTab(projects);
        var first = cut.FindComponents<OmniDropDown<int?>>().Select(dropDown => dropDown.Instance.Options).ToList();
        Assert.Equal(2, first.Count);
        Assert.All(first, options => Assert.NotEmpty(options));

        cut.Render(parameters => parameters.Add(tab => tab.Projects, projects));
        var second = cut.FindComponents<OmniDropDown<int?>>().Select(dropDown => dropDown.Instance.Options).ToList();

        Assert.Same(first[0], second[0]);
        Assert.Same(first[1], second[1]);
    }

    /// <summary>Choosing an organization narrows the projects offered (a new list, once), and only to that organization's.</summary>
    [Fact]
    public void ChoosingAnOrganization_NarrowsTheProjectOptions_OnceAndToThatOrganizationOnly()
    {
        var projects = Projects();
        var cut = RenderFullTab(projects);
        var organizationDropDown = cut.FindComponents<OmniDropDown<int?>>()[0];

        cut.InvokeAsync(() => organizationDropDown.Instance.ValueChanged.InvokeAsync(10));

        cut.WaitForAssertion(() =>
        {
            var offered = cut.FindComponents<OmniDropDown<int?>>()[1].Instance.Options;
            Assert.NotEmpty(offered);
            Assert.Equal(offered.Count, projects.Count(project => project.OrganizationId == 10));
        });
    }
}
