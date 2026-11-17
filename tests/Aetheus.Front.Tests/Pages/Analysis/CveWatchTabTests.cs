// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Analysis;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Analysis;

/// <summary>R-15 and R-21: the CVE watch tab of the quality page (<c>?tab=cve-watch</c>, the address the CVE mails link to) and the
/// Scan column of the Executions tab. The API is a double: these tests prove what the page asks for and how it draws the answer,
/// not what the server returns (the integration tests of the server do).</summary>
public sealed class CveWatchTabTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public CveWatchTabTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        // R-41: the tab also reads the CVE of the servers' host packages.
        _handler.SetPaginatedJsonResponse("api/servers/host-vulnerabilities", Array.Empty<Aetheus.Shared.Components.Analysis.ServerHostFindingDto>());
    }

    private void RegisterPortfolio(params AnalysisPortfolioRowDto[] rows)
    {
        _handler.SetJsonResponse("api/analysis/portfolio/projects", new List<AnalysisPortfolioProjectDto>
        {
            new() { ProjectId = 1, ProjectName = "Aetheus", OrganizationId = 10, OrganizationName = "Org A" },
            new() { ProjectId = 2, ProjectName = "Valnerya", OrganizationId = 20, OrganizationName = "Org B" }
        });
        _handler.SetJsonResponse("api/analysis/portfolio/filter-values", new AnalysisPortfolioFilterValuesDto());
        _handler.SetPaginatedJsonResponse("api/analysis/portfolio", rows);
    }

    private void RegisterCveWatch()
    {
        _handler.SetJsonResponse("api/analysis/cve-watch/overview", new CveWatchOverviewDto
        {
            CriticalCount = 3,
            ExploitedCount = 2,
            WithoutFixCount = 1,
            UncoveredProjects = [new() { ProjectId = 2, ProjectName = "Valnerya", OrganizationId = 20, OrganizationName = "Org B", Coverage = ProjectInventoryCoverage.Partial }],
            Sources = [new() { Source = "osv:NuGet", LastSuccessAt = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), IsStale = false }]
        });
        _handler.SetPaginatedJsonResponse("api/analysis/cve-watch/advisories",
        [
            new CveWatchAdvisoryGroupDto
            {
                AdvisoryId = "GHSA-aaaa-bbbb-cccc",
                CveId = "CVE-2026-21001",
                Summary = "Unsafe deserialisation in the session reader.",
                Severity = AnalysisSeverity.Critical,
                SeverityPublished = true,
                ExploitedSince = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc),
                Matches =
                [
                    new() { MatchId = 1, ProjectId = 1, ProjectName = "Aetheus", PackageName = "Contoso.Session.Core", Version = "4.1.0", FixedVersion = "4.1.3", Status = VulnerabilityMatchStatus.Affected, FirstMatchedAt = new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc) },
                    new() { MatchId = 2, ProjectId = 2, ProjectName = "Valnerya", PackageName = "Contoso.Session.Core", Version = "4.0.9", FixedVersion = "4.1.3", Status = VulnerabilityMatchStatus.Affected, FirstMatchedAt = new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc) }
                ]
            }
        ]);
    }

    [Fact]
    public void TheUrlTab_CveWatch_ShowsTheTab_WithTheCountsOfTheServer_AndTheCveGroupedByProject()
    {
        RegisterPortfolio();
        RegisterCveWatch();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/analysis?tab=cve-watch");

        var cut = Render<AnalysisPortfolio>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("3", cut.Find("[data-testid='cve-tile-critical']").TextContent);
            Assert.Contains("2", cut.Find("[data-testid='cve-tile-exploited']").TextContent);
            Assert.Contains("1", cut.Find("[data-testid='cve-tile-nofix']").TextContent);
            Assert.Contains("1", cut.Find("[data-testid='cve-tile-uncovered']").TextContent);
            Assert.Contains("CVE-2026-21001", cut.Find(".cve-watch-grid").TextContent);
            // One entry per CVE: the two projects are inside its detail, not two rows.
            Assert.Single(cut.FindAll(".cve-watch-grid .cve-watch-cve-id"));
        });
        var selectedTab = cut.FindAll("[role='tab']").Single(tab => tab.GetAttribute("aria-selected") == "true");
        Assert.Contains("CveWatchTab", selectedTab.TextContent, StringComparison.Ordinal);
    }

    /// <summary>The projects of an opened CVE are the cards of an OE selectable group: the first is chosen, picking another
    /// moves the choice (and the timeline that follows it) without asking the server again.</summary>
    [Fact]
    public void AnOpenedCve_OffersItsProjectsAsSelectableCards_AndPickingOneMovesTheChoice()
    {
        RegisterPortfolio();
        RegisterCveWatch();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/analysis?tab=cve-watch");
        var cut = Render<AnalysisPortfolio>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".cve-watch-grid .cve-watch-cve-id")));

        cut.Find("button.omni-data-grid__expand").Click();

        cut.WaitForAssertion(() =>
        {
            var cards = cut.FindAll("[data-testid='cve-project']");
            Assert.Equal(2, cards.Count);
            Assert.Equal("true", cards[0].GetAttribute("aria-checked"));
            Assert.Equal("false", cards[1].GetAttribute("aria-checked"));
            Assert.Contains("Aetheus", cards[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("4.1.3", cards[0].TextContent, StringComparison.Ordinal);
        });

        cut.FindAll("[data-testid='cve-project']")[1].Click();

        cut.WaitForAssertion(() =>
        {
            var cards = cut.FindAll("[data-testid='cve-project']");
            Assert.Equal("false", cards[0].GetAttribute("aria-checked"));
            Assert.Equal("true", cards[1].GetAttribute("aria-checked"));
        });
    }

    [Fact]
    public void ThePortfolio_DoesNotReadTheWatch_UntilItsTabIsShown()
    {
        RegisterPortfolio();
        RegisterCveWatch();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/analysis");

        var cut = Render<AnalysisPortfolio>();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Url.Contains("api/analysis/portfolio/projects", StringComparison.Ordinal)));
        Assert.Empty(cut.FindAll("[data-testid='cve-watch']"));
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("api/analysis/cve-watch", StringComparison.Ordinal));
    }

    [Fact]
    public void PressingTheCriticalTile_AsksTheServerForTheCriticalCve_AndPressingItAgainLetsGo()
    {
        RegisterPortfolio();
        RegisterCveWatch();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/analysis?tab=cve-watch");
        var cut = Render<AnalysisPortfolio>();
        cut.WaitForAssertion(() => Assert.Contains("3", cut.Find("[data-testid='cve-tile-critical']").TextContent));

        cut.Find("[data-testid='cve-tile-critical']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(_handler.Requests, request => request.Url.Contains("api/analysis/cve-watch/advisories", StringComparison.Ordinal)
                && request.Url.Contains("severity=Critical", StringComparison.Ordinal));
            Assert.Equal("true", cut.Find("[data-testid='cve-tile-critical']").GetAttribute("aria-pressed"));
        });

        cut.Find("[data-testid='cve-tile-critical']").Click();

        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("[data-testid='cve-tile-critical']").GetAttribute("aria-pressed")));
    }

    [Fact]
    public void AStaleSource_IsToldAboveTheTiles_SoThatNoCveIsNotTakenForProof()
    {
        RegisterPortfolio();
        _handler.SetJsonResponse("api/analysis/cve-watch/overview", new CveWatchOverviewDto
        {
            Sources = [new() { Source = "osv:npm", LastSuccessAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), IsStale = true }]
        });
        _handler.SetPaginatedJsonResponse("api/analysis/cve-watch/advisories", Array.Empty<CveWatchAdvisoryGroupDto>());
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/analysis?tab=cve-watch");

        var cut = Render<AnalysisPortfolio>();

        cut.WaitForAssertion(() => Assert.Contains("CveWatchStaleTitle", cut.Find("[data-testid='cve-watch-stale']").TextContent, StringComparison.Ordinal));
        // With a stale source and nothing found, the list does not say "no CVE" as if it were good news.
        Assert.Contains("CveWatchNoMatchNoProof", cut.Find("[data-testid='cve-watch']").TextContent, StringComparison.Ordinal);
    }

    /// <summary>R-21: the column is named Scan and each row links its own run, behind an icon that says where it comes from.</summary>
    [Fact]
    public void TheScanColumn_LinksThePipelineRun_ToItsPage_AndTheWatchRun_ToTheWatchPage()
    {
        RegisterPortfolio(
            new AnalysisPortfolioRowDto
            {
                AnalysisReportId = 900,
                ProjectId = 1,
                ProjectName = "Aetheus",
                OrganizationName = "Org A",
                ScannerName = "SonarQube",
                PipelineRunId = 77,
                PipelineRunNumber = 12,
                Source = AnalysisScanSource.Pipeline,
                CompletedAt = new DateTime(2026, 11, 3, 7, 0, 0, DateTimeKind.Utc)
            },
            new AnalysisPortfolioRowDto
            {
                AnalysisReportId = 58,
                ProjectId = 1,
                ProjectName = "Aetheus",
                OrganizationName = "Org A",
                ScannerName = "CVE watch",
                Source = AnalysisScanSource.CveWatch,
                CompletedAt = new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc)
            });
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/analysis?tab=runs");

        var cut = Render<AnalysisPortfolio>();

        cut.WaitForAssertion(() =>
        {
            var pipeline = cut.FindAll("a").Single(link => link.TextContent.Trim() == "#12");
            Assert.Equal("pipelines/runs/77", pipeline.GetAttribute("href"));
            var watch = cut.FindAll("a").Single(link => link.TextContent.Trim() == "V-58");
            Assert.Equal("analysis/cve-watch/runs/58", watch.GetAttribute("href"));
            // The source is named next to each identifier, by the localized value of the enum.
            Assert.Contains("Enum_AnalysisScanSource_Pipeline", pipeline.ParentElement!.ParentElement!.TextContent, StringComparison.Ordinal);
            Assert.Contains("Enum_AnalysisScanSource_CveWatch", watch.ParentElement!.ParentElement!.TextContent, StringComparison.Ordinal);
        });
        Assert.Contains(cut.FindAll("th"), header => header.TextContent.Contains("AnalysisScan", StringComparison.Ordinal));
        Assert.DoesNotContain(cut.FindAll("th"), header => header.TextContent.Trim() == "PipelineRun");
    }
}
