// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Analysis;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Analysis;

/// <summary>R-14: the CVE section of a project's quality page and the CVE tile of its overview. The API is a double: these tests prove what
/// the components ask for and how they draw the answer, not what the server returns (the integration tests of the server do).</summary>
public sealed class ProjectCveWatchTests : BunitContext
{
    private const string ProjectUrl = "api/analysis/cve-watch/projects/5";
    private const string Advisories = "api/analysis/cve-watch/advisories";

    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectCveWatchTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static CveWatchProjectDto Project(
        IEnumerable<CveWatchProjectInventoryDto>? inventories = null, IEnumerable<CveWatchSourceDto>? sources = null, int open = 3) => new()
        {
            ProjectId = 5,
            OpenCount = open,
            CriticalCount = open == 0 ? 0 : 1,
            HighCount = open == 0 ? 0 : 1,
            MediumCount = open == 0 ? 0 : 1,
            ExploitedCount = open == 0 ? 0 : 1,
            WithoutFixCount = open == 0 ? 0 : 1,
            LastWatchedAt = new DateTime(2026, 11, 3, 8, 17, 0, DateTimeKind.Utc),
            LastWatchRunId = 58,
            Inventories = [.. inventories ?? [Inventory(ProjectInventoryCoverage.Complete)]],
            Sources = [.. sources ?? [new CveWatchSourceDto { Source = "osv:NuGet", LastSuccessAt = new DateTime(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc) }]]
        };

    private static CveWatchProjectInventoryDto Inventory(ProjectInventoryCoverage coverage, string? reason = null) => new()
    {
        Origin = ProjectInventoryOrigin.Repository,
        RepositoryName = "app",
        Ref = "develop",
        ReadAt = new DateTime(2026, 11, 3, 7, 0, 0, DateTimeKind.Utc),
        Coverage = coverage,
        Reason = reason,
        ItemCount = 42
    };

    private void RegisterAdvisories(params CveWatchAdvisoryGroupDto[] groups) => _handler.SetPaginatedJsonResponse(Advisories, groups);

    private static CveWatchAdvisoryGroupDto Group(string advisory, AnalysisSeverity severity, int matchId, int? findingId, string? fix = "2.0.0") => new()
    {
        AdvisoryId = advisory,
        CveId = $"CVE-{advisory}",
        Summary = $"Summary of {advisory}",
        Severity = severity,
        SeverityPublished = true,
        ExploitedSince = severity == AnalysisSeverity.Critical ? new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc) : null,
        Matches =
        [
            new() { MatchId = matchId, ProjectId = 5, ProjectName = "Aetheus", AdvisoryId = advisory, PackageName = $"lib-{matchId}", Version = "1.0.0", FixedVersion = fix, Status = VulnerabilityMatchStatus.Affected, FindingId = findingId }
        ]
    };

    private IRenderedComponent<ProjectCveWatchPanel> RenderPanel() => Render<ProjectCveWatchPanel>(parameters => parameters.Add(panel => panel.ProjectId, 5));

    [Fact]
    public void ThePanel_AsksForTheProjectsOwnOpenCve_AndDrawsTheFiguresAndTheRows()
    {
        _handler.SetJsonResponse(ProjectUrl, Project());
        RegisterAdvisories(Group("GHSA-aaaa", AnalysisSeverity.Critical, 1, 301), Group("GHSA-bbbb", AnalysisSeverity.High, 2, 302, fix: null));

        var cut = RenderPanel();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("3", cut.Find("[data-testid='project-cve-open']").TextContent, StringComparison.Ordinal);
            Assert.Contains("1", cut.Find("[data-testid='project-cve-critical']").TextContent, StringComparison.Ordinal);
            var list = cut.Find("[data-testid='project-cve-list']").TextContent;
            Assert.Contains("CVE-GHSA-aaaa", list, StringComparison.Ordinal);
            Assert.Contains("lib-1 1.0.0", list, StringComparison.Ordinal);
            Assert.Contains("CVE-GHSA-bbbb", list, StringComparison.Ordinal);
            Assert.Contains("CveWatchNoFix", list, StringComparison.Ordinal);
        });
        Assert.Contains(_handler.RequestsSnapshot(), request => request.Url.Contains($"{Advisories}?", StringComparison.Ordinal)
            && request.Url.Contains("projectId=5", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCoverage_ByOrigin_ShowsWhenItWasRead_HowCompleteItIs_AndWhy()
    {
        _handler.SetJsonResponse(ProjectUrl, Project(
        [
            Inventory(ProjectInventoryCoverage.Partial, "no-lock-file:npm=2"),
            new CveWatchProjectInventoryDto { Origin = ProjectInventoryOrigin.Sbom, ReadAt = new DateTime(2026, 11, 2, 22, 0, 0, DateTimeKind.Utc), Coverage = ProjectInventoryCoverage.Complete, ItemCount = 7 }
        ]));
        RegisterAdvisories();

        var cut = RenderPanel();

        cut.WaitForAssertion(() =>
        {
            var inventories = cut.Find("[data-testid='project-cve-inventories']").TextContent;
            Assert.Contains("Enum_ProjectInventoryOrigin_Repository", inventories, StringComparison.Ordinal);
            Assert.Contains("app develop", inventories, StringComparison.Ordinal);
            Assert.Contains("Enum_ProjectInventoryOrigin_Sbom", inventories, StringComparison.Ordinal);
            Assert.Contains("Enum_ProjectInventoryCoverage_Partial", inventories, StringComparison.Ordinal);
            Assert.Contains("Enum_ProjectInventoryCoverage_Complete", inventories, StringComparison.Ordinal);
            // The gap code is said through its translation key, with its technology and its count.
            Assert.Contains("CveInventoryGap_no-lock-file", inventories, StringComparison.Ordinal);
            Assert.Contains("(npm)", inventories, StringComparison.Ordinal);
            Assert.Contains("x2", inventories, StringComparison.Ordinal);
            Assert.Contains("analysis/cve-watch/runs/58", cut.Find("[data-testid='project-cve-last-pass']").InnerHtml, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void NoCve_IsNotAProof_WhenAnInventoryIsPartial_OrASourceIsStale_AndIsGoodNewsOtherwise()
    {
        _handler.SetJsonResponse(ProjectUrl, Project([Inventory(ProjectInventoryCoverage.Partial, "no-lock-file=1")], open: 0));
        RegisterAdvisories();
        var partial = RenderPanel();
        partial.WaitForAssertion(() => Assert.Contains("CveWatchNoMatchNoProof", partial.Markup, StringComparison.Ordinal));

        _handler.SetJsonResponse(ProjectUrl, Project(sources: [new CveWatchSourceDto { Source = "osv:npm", IsStale = true }], open: 0));
        var stale = RenderPanel();
        stale.WaitForAssertion(() =>
        {
            Assert.Contains("CveWatchNoMatchNoProof", stale.Markup, StringComparison.Ordinal);
            Assert.Contains("CveWatchStaleText", stale.Find("[data-testid='project-cve-stale']").TextContent, StringComparison.Ordinal);
        });

        _handler.SetJsonResponse(ProjectUrl, Project(open: 0));
        var proved = RenderPanel();
        proved.WaitForAssertion(() => Assert.Contains("CveWatchNoMatchHint", proved.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("CveWatchNoMatchNoProof", proved.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AProjectWithNoInventory_SaysSo()
    {
        _handler.SetJsonResponse(ProjectUrl, Project(inventories: [], open: 0));
        RegisterAdvisories();

        var cut = RenderPanel();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("CveProjectNoInventory", cut.Find("[data-testid='project-cve-inventories']").TextContent, StringComparison.Ordinal);
            Assert.Contains("CveWatchNoMatchNoProof", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void OnePackage_MatchedInTheBranchHead_AndInTheDeployedRelease_ReadsAsTwoRowsWithTheirOrigin()
    {
        _handler.SetJsonResponse(ProjectUrl, Project());
        var group = Group("GHSA-aaaa", AnalysisSeverity.High, 1, 301) with
        {
            Matches =
            [
                new() { MatchId = 1, ProjectId = 5, AdvisoryId = "GHSA-aaaa", PackageName = "undici", Version = "7.30.0", Status = VulnerabilityMatchStatus.Affected, FindingId = 301, InventoryOrigin = ProjectInventoryOrigin.Repository, InventoryRef = "develop" },
                new() { MatchId = 2, ProjectId = 5, AdvisoryId = "GHSA-aaaa", PackageName = "undici", Version = "7.30.0", Status = VulnerabilityMatchStatus.Affected, FindingId = 301, InventoryOrigin = ProjectInventoryOrigin.ReleaseCommit, InventoryRef = "9f24e1b109851319de3b5d0ecd489c9cc96288eb" }
            ]
        };
        RegisterAdvisories(group);

        var cut = RenderPanel();

        cut.WaitForAssertion(() =>
        {
            var origins = cut.FindAll("[data-testid='project-cve-match-origin']").Select(origin => origin.TextContent.Trim()).ToList();
            Assert.Equal(2, origins.Count);
            Assert.Contains("Enum_ProjectInventoryOrigin_Repository - develop", origins);
            // The commit is shortened like every release reference.
            Assert.Contains(origins, origin => origin.StartsWith("Enum_ProjectInventoryOrigin_ReleaseCommit - 9f24e1b", StringComparison.Ordinal)
                && !origin.Contains("9f24e1b109851319de3b5d0ecd489c9cc96288eb", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ARowOpensTheFindingOfItsMatch()
    {
        _handler.SetJsonResponse(ProjectUrl, Project());
        RegisterAdvisories(Group("GHSA-aaaa", AnalysisSeverity.Critical, 1, 301));
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.Contains("CVE-GHSA-aaaa", cut.Find("[data-testid='project-cve-list']").TextContent, StringComparison.Ordinal));

        cut.FindAll("[data-testid='project-cve-list'] tbody tr, [data-testid='project-cve-list'] [role='row']")
            .First(row => row.TextContent.Contains("CVE-GHSA-aaaa", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.EndsWith("/analysis/findings/301", navigation.Uri, StringComparison.Ordinal));
    }

    [Fact]
    public void AFailedRead_SaysSoInPlace_AndNeverDrawsAnEmptyVerdict()
    {
        _handler.SetResponse(ProjectUrl, HttpStatusCode.InternalServerError);
        RegisterAdvisories();

        var cut = RenderPanel();

        cut.WaitForAssertion(() => Assert.Contains("LoadFailed", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("CveWatchNoMatch", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(Services.Toasts());
    }

    private IRenderedComponent<ProjectCveTile> RenderTile() => Render<ProjectCveTile>(parameters => parameters.Add(tile => tile.ProjectId, 5));

    [Fact]
    public void TheTile_SumsUpTheOpenCveBySeverity_AndLinksToTheCveSection()
    {
        _handler.SetJsonResponse(ProjectUrl, Project());

        var cut = RenderTile();

        cut.WaitForAssertion(() =>
        {
            var tile = cut.Find("[data-testid='project-cve-tile']");
            Assert.Contains("3", tile.TextContent, StringComparison.Ordinal);
            Assert.Contains("1 Enum_AnalysisSeverity_Critical", tile.TextContent, StringComparison.Ordinal);
            Assert.Contains("1 Enum_AnalysisSeverity_High", tile.TextContent, StringComparison.Ordinal);
            Assert.Contains("1 Enum_AnalysisSeverity_Medium", tile.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("Enum_AnalysisSeverity_Low", tile.TextContent, StringComparison.Ordinal);
            Assert.Contains("projects/5/quality?tab=cve", tile.GetAttribute("href"), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheTile_WithNoOpenCve_SaysWhetherThatIsProved()
    {
        _handler.SetJsonResponse(ProjectUrl, Project(open: 0));
        var proved = RenderTile();
        proved.WaitForAssertion(() => Assert.Contains("CveWatchNoMatch", proved.Find("[data-testid='project-cve-tile']").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("CveWatchNoMatchNoProof", proved.Markup, StringComparison.Ordinal);

        _handler.SetJsonResponse(ProjectUrl, Project([Inventory(ProjectInventoryCoverage.None)], open: 0));
        var unproved = RenderTile();
        unproved.WaitForAssertion(() => Assert.Contains("CveWatchNoMatchNoProof", unproved.Find("[data-testid='project-cve-tile']").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void TheTile_IsNotDrawn_ForAProjectTheCallerMayNotRead_OrWhenTheReadFails()
    {
        _handler.SetResponse(ProjectUrl, HttpStatusCode.NotFound);
        var notReadable = RenderTile();
        notReadable.WaitForAssertion(() => Assert.Contains(_handler.RequestsSnapshot(), request => request.Url.EndsWith(ProjectUrl, StringComparison.Ordinal)));
        Assert.Empty(notReadable.FindAll("[data-testid='project-cve-tile']"));

        _handler.SetResponse(ProjectUrl, HttpStatusCode.InternalServerError);
        var failed = RenderTile();
        failed.WaitForAssertion(() => Assert.Contains(_handler.RequestsSnapshot(), request => request.Url.EndsWith(ProjectUrl, StringComparison.Ordinal)));
        Assert.Empty(failed.FindAll("[data-testid='project-cve-tile']"));
        Assert.Empty(Services.Toasts());
    }
}
