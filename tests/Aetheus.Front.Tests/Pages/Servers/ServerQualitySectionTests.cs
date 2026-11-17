// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>R-7: the quality page of the servers (all of them, or one) shows the server grade block, the count tiles and the
/// findings; a server without a letter says why; a vulnerability without a fix is shown but not counted in the grade.</summary>
public class ServerQualitySectionTests : BunitContext
{
    private static ServerQualityDto Quality(int id, string name, AnalysisGrade? grade, AnalysisGradeFindingCountsDto? open = null,
        AnalysisGradeFindingCountsDto? withoutFix = null, int exploited = 0, HostVulnerabilityWatchState state = HostVulnerabilityWatchState.Watched) => new()
        {
            ServerId = id,
            ServerName = name,
            State = state,
            OsName = "Ubuntu 24.04.1 LTS",
            Grade = grade,
            Counts = open ?? new(),
            CountsWithoutFix = withoutFix ?? new(),
            Exploited = exploited,
            MatchedAt = DateTime.UtcNow
        };

    private static ServerHostFindingDto Finding(long id, AnalysisSeverity severity, string? fix, DateTime? exploited = null) => new()
    {
        Summary = $"Summary of finding {id}",
        Id = id,
        ServerId = 1,
        ServerName = "alpha",
        AdvisoryId = $"UBUNTU-CVE-2026-000{id}",
        CveId = $"CVE-2026-000{id}",
        PackageName = "openssl",
        BinaryPackages = "libssl3t64",
        InstalledVersion = "3.0.13",
        FixedVersion = fix,
        Severity = severity,
        SeverityPublished = true,
        ExploitedSince = exploited,
        Status = VulnerabilityMatchStatus.Affected
    };

    private IRenderedComponent<ServerQualitySection> Render(int? serverId, List<ServerQualityDto> servers, params ServerHostFindingDto[] findings)
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse(HttpMethod.Get, "host-quality", servers);
        handler.SetJsonResponse(HttpMethod.Get, "host-vulnerabilities", new ServerHostVulnerabilitiesDto
        {
            State = HostVulnerabilityWatchState.Watched,
            OsName = "Ubuntu 24.04.1 LTS",
            MatchedAt = DateTime.UtcNow,
            Findings = [.. findings]
        });
        return Render<ServerQualitySection>(p => p.Add(c => c.ServerId, serverId));
    }

    [Fact]
    public void AllServers_ListTheirGrades_AndTheWorstOne_TheDetailBeingEachServersOwnPage()
    {
        var cut = Render(null,
        [
            Quality(1, "alpha", AnalysisGrade.F, new() { Critical = 2, Medium = 3 }, new() { Medium = 1 }, exploited: 1),
            Quality(2, "beta", AnalysisGrade.A)
        ]);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=server-quality-servers]")));
        Assert.Contains("alpha", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("beta", cut.Markup, StringComparison.Ordinal);
        Assert.StartsWith("F", cut.Find("[data-testid=server-quality-tile-worst]").TextContent.Trim(), StringComparison.Ordinal);
        Assert.Contains("5", cut.Find("[data-testid=server-quality-tile-open]").TextContent, StringComparison.Ordinal);
        // The same fixable / no-fix split as the server page: 5 open, 1 of them with no published fix.
        Assert.Contains("4", cut.Find("[data-testid=server-quality-tile-fixable]").TextContent, StringComparison.Ordinal);
        Assert.Contains("1", cut.Find("[data-testid=server-quality-tile-nofix]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=server-quality-detail]"));

        cut.Find("button[title=ServerQualityShowDetail]").Click();

        Assert.EndsWith("servers/1/quality", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void OneServer_ShowsItsDetailOnly_WithoutTheListOfServers()
    {
        var cut = Render(1, [Quality(1, "alpha", AnalysisGrade.A, new() { Critical = 1 }, new() { Critical = 1 })], Finding(1, AnalysisSeverity.Critical, null));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=server-quality-detail]")));
        Assert.Empty(cut.FindAll("[data-testid=server-quality-overview]"));
        // The only vulnerability has no fix: it is shown but not counted, so the grade has no cause; the table of the vulnerabilities
        // with no fix shows its count without a letter, the fixable table shows zero with an A.
        Assert.Contains("ServerQualityCauseNone", cut.Find("[data-testid=grade-row-gate-cause]").TextContent, StringComparison.Ordinal);
        Assert.Contains("1", cut.Find("[data-testid=server-quality-table-nofix-critical] .analysis-grade-value").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=server-quality-table-nofix-critical] .analysis-grade-row-grade *"));
        Assert.Equal("0", cut.Find("[data-testid=server-quality-table-fixable-critical] .analysis-grade-value").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll("[data-testid=server-quality-table-fixable-critical] .analysis-grade-row-grade *"));
        // The finding is on the tab of the findings without a fix.
        Services.GetRequiredService<NavigationManager>().NavigateTo("servers/1/quality?tab=nofix");
        cut.WaitForAssertion(() => Assert.Contains("CveWatchNoFix", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void OneServer_HasThreeTabs_OverviewThenTheFindingsWithAFixThenThoseWithNone_EachCountedInItsTitle()
    {
        var cut = Render(1, [Quality(1, "alpha", AnalysisGrade.F, new() { Critical = 3, Medium = 1 }, new() { Critical = 1 })],
            Finding(1, AnalysisSeverity.Critical, "1.1"), Finding(2, AnalysisSeverity.Critical, "1.2"), Finding(3, AnalysisSeverity.Critical, null), Finding(4, AnalysisSeverity.Medium, "1.3"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=server-quality-grade-block]")));
        var tabs = cut.FindAll("[role=tab]").Select(tab => tab.TextContent.Trim()).ToList();
        Assert.Equal(["AnalysisOverview", "ServerQualityTabFindings · 3", "ServerQualityTabNoFix · 1"], tabs);
        Assert.Empty(cut.FindAll("[data-testid=server-host-findings]"));

        Services.GetRequiredService<NavigationManager>().NavigateTo("servers/1/quality?tab=findings");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=server-host-findings]")));
        Assert.Empty(cut.FindAll("[data-testid=server-quality-grade-block]"));
        // Only the three vulnerabilities with a fix are in this grid, the one with none is on the next tab.
        Assert.Equal(3, cut.FindAll("[data-testid=server-host-findings] a.server-host-advisory-link, [data-testid=server-host-findings] .cve-watch-cve-id").Count);
        Assert.DoesNotContain("CVE-2026-0003", cut.Find("[data-testid=server-host-findings]").TextContent, StringComparison.Ordinal);
        // A ZoneVeil host keeps a 10rem minimum height even when not busy: left beside the grid it was a 160px gap.
        Assert.Empty(cut.FindAll(".zone-veil-host"));

        Services.GetRequiredService<NavigationManager>().NavigateTo("servers/1/quality?tab=nofix");

        cut.WaitForAssertion(() => Assert.Contains("CVE-2026-0003", cut.Find("[data-testid=server-host-findings]").TextContent, StringComparison.Ordinal));
        var noFix = cut.Find("[data-testid=server-host-findings]").TextContent;
        Assert.DoesNotContain("CVE-2026-0001", noFix, StringComparison.Ordinal);
        Assert.DoesNotContain("CVE-2026-0004", noFix, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTabCounts_AreTheDtoCounts_TheSameSplitAsTheOverviewTables()
    {
        var server = Quality(1, "alpha", AnalysisGrade.F, new() { Critical = 3, High = 2, Medium = 4 }, new() { Critical = 1, Medium = 2 });
        var cut = Render(1, [server]);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=server-quality-tables]")));
        Assert.Equal(6, server.FixableCount);
        Assert.Equal(3, server.WithoutFixCount);
        var tabs = cut.FindAll("[role=tab]").Select(tab => tab.TextContent.Trim()).ToList();
        Assert.Equal($"ServerQualityTabFindings · {server.FixableCount}", tabs[1]);
        Assert.Equal($"ServerQualityTabNoFix · {server.WithoutFixCount}", tabs[2]);
        // The fixable table counts what the Findings tab counts (6 = 2 critical + 2 high + 2 medium), the other what the last tab counts (3).
        Assert.Equal("2", cut.Find("[data-testid=server-quality-table-fixable-critical] .analysis-grade-value").TextContent.Trim());
        Assert.Equal("2", cut.Find("[data-testid=server-quality-table-fixable-high] .analysis-grade-value").TextContent.Trim());
        Assert.Equal("2", cut.Find("[data-testid=server-quality-table-fixable-other] .analysis-grade-value").TextContent.Trim());
        Assert.Equal("1", cut.Find("[data-testid=server-quality-table-nofix-critical] .analysis-grade-value").TextContent.Trim());
        Assert.Equal("0", cut.Find("[data-testid=server-quality-table-nofix-high] .analysis-grade-value").TextContent.Trim());
        Assert.Equal("2", cut.Find("[data-testid=server-quality-table-nofix-other] .analysis-grade-value").TextContent.Trim());
    }

    [Fact]
    public void TheOverview_HasTwoIdenticalTablesUnderTheCard_TheFixableOneWithGradeLetters_TheOtherWithout()
    {
        var cut = Render(1, [Quality(1, "alpha", AnalysisGrade.F, new() { Critical = 2, Low = 3 }, new() { Critical = 1 })]);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=server-quality-tables]")));
        Assert.Contains("ServerQualityTableFixable", cut.Find("[data-testid=server-quality-table-fixable]").TextContent, StringComparison.Ordinal);
        Assert.Contains("ServerQualityTableNoFix", cut.Find("[data-testid=server-quality-table-nofix]").TextContent, StringComparison.Ordinal);
        // Same rows in both, every one shown (zeros included): critical, high, medium and low.
        foreach (var table in new[] { "fixable", "nofix" })
        {
            Assert.Equal(3, cut.FindAll($"[data-testid=server-quality-table-{table}] .analysis-grade-row").Count);
        }

        // Letters: only the fixable table has them (1 critical with a fix: F; no high: A; 3 low: B).
        var letters = cut.FindAll("[data-testid=server-quality-table-fixable] .analysis-grade-row-grade").Select(cell => cell.TextContent.Trim()).ToList();
        Assert.Equal(["F", "A", "B"], letters);
        Assert.Empty(cut.FindAll("[data-testid=server-quality-table-nofix] .analysis-grade-row-grade *"));
        // They come under the overview card.
        var markup = cut.Markup;
        Assert.True(markup.IndexOf("server-quality-overview-card", StringComparison.Ordinal) < markup.IndexOf("server-quality-table-fixable", StringComparison.Ordinal));
        Assert.True(markup.IndexOf("server-quality-table-fixable", StringComparison.Ordinal) < markup.IndexOf("server-quality-table-nofix", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFindingsGrid_EndsWithTheDescription_ThenTheShortAdvisory_ThenTheNarrowActionsColumn()
    {
        var cut = Render(1, [Quality(1, "alpha", AnalysisGrade.A, new() { Critical = 1 })], Finding(1, AnalysisSeverity.Critical, "1.1"));
        Services.GetRequiredService<NavigationManager>().NavigateTo("servers/1/quality?tab=findings");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=server-host-findings] a.server-host-advisory-link, [data-testid=server-host-findings] .cve-watch-cve-id")));
        var grid = cut.Find("[data-testid=server-host-findings]");
        var titles = grid.QuerySelectorAll("th .omni-data-grid__title").Select(title => title.TextContent.Trim()).ToList();
        Assert.Equal(["Severity", "Package", "ServerHostCveInstalled", "CveWatchExploited", "CveWatchFixedIn", "Description", "CveWatchAdvisory", ""], titles);
        // The data columns, the Description taking the room left, then the short advisory (owner, R-18), then the narrow actions column.
        var columns = grid.QuerySelectorAll("col").Select(col => col.GetAttribute("data-omni-col")).ToList();
        Assert.Equal(["Severity", "PackageName", "InstalledVersion", "Exploited", "FixedIn", "Summary", "AdvisoryId", ""], columns);
        Assert.Equal("true", grid.QuerySelector("col[data-omni-col=Summary]")!.GetAttribute("data-omni-grow"));
        // The advisory column does not carry the text of the advisory: the Description is its own column.
        Assert.DoesNotContain("Summary of finding 1", cut.Find(".cve-watch-advisory").TextContent, StringComparison.Ordinal);
        Assert.Contains("Summary of finding 1", cut.Find("[data-testid=server-host-findings]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSplitRule_IsOne_FixableMeansAPublishedFix()
    {
        Assert.True(ServerHostFixability.IsFixable(Finding(1, AnalysisSeverity.High, "1.1")));
        Assert.False(ServerHostFixability.IsFixable(Finding(2, AnalysisSeverity.High, null)));
        var counts = ServerHostFixability.Fixable(new() { Critical = 2, High = 1, Medium = 3, Info = 1 }, new() { Critical = 1, Medium = 3 });
        Assert.Equal(new AnalysisGradeFindingCountsDto { Critical = 1, High = 1, Medium = 0, Low = 0, Info = 1 }, counts);
        // The grade is read from the same split: the one critical vulnerability left is the F.
        Assert.Equal(AnalysisGrade.F, ServerHostGrade.Of(new() { Critical = 2 }, new() { Critical = 1 }));
        Assert.Equal(AnalysisGrade.A, ServerHostGrade.Of(new() { Critical = 2 }, new() { Critical = 2 }));
    }

    [Fact]
    public void TheFindingsFilters_AreGridColumns_NotABarAboveTheGrid()
    {
        var cut = Render(1, [Quality(1, "alpha", AnalysisGrade.A, new() { Critical = 1 }, new() { Critical = 1 })], Finding(1, AnalysisSeverity.Critical, "1.1", DateTime.UtcNow));
        Services.GetRequiredService<NavigationManager>().NavigateTo("servers/1/quality?tab=findings");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=server-host-findings]")));
        Assert.Empty(cut.FindAll(".server-quality-filters"));
        var headers = cut.Find("[data-testid=server-host-findings]").TextContent;
        Assert.Contains("Severity", headers, StringComparison.Ordinal);
        Assert.Contains("CveWatchExploited", headers, StringComparison.Ordinal);
        Assert.Contains("CveWatchFixedIn", headers, StringComparison.Ordinal);
        Assert.DoesNotContain("CveWatchAdvisoryAttribution", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AServerWithoutALetter_SaysWhy_NeverAnA()
    {
        var cut = Render(1, [Quality(1, "alpha", null, state: HostVulnerabilityWatchState.UnsupportedSystem)]);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=server-quality-not-graded]")));
        Assert.Empty(cut.FindAll("[data-testid=server-quality-grade-block]"));
    }

    [Fact]
    public void NoServerAnalyzed_IsSaidSo()
    {
        var cut = Render(null, []);

        cut.WaitForAssertion(() => Assert.Contains("ServerQualityNoneTitle", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid=server-quality]"));
    }

    [Fact]
    public void TheColumnFilters_KeepTheSeverity_TheExploited_AndTheOnesWithAFix()
    {
        ServerHostFindingDto[] all =
        [
            Finding(1, AnalysisSeverity.Critical, "1.1", DateTime.UtcNow),
            Finding(2, AnalysisSeverity.High, null),
            Finding(3, AnalysisSeverity.Medium, "1.1")
        ];
        static List<long> Ids(IEnumerable<ServerHostFindingDto> findings, Func<ServerHostFindingDto, string, bool> match, string filter) =>
            [.. findings.Where(finding => match(finding, filter)).Select(finding => finding.Id)];

        Assert.Equal([2L], Ids(all, ServerHostFindingsGrid.SeverityMatches, OmniDataGridFilterValues.Join([nameof(AnalysisSeverity.High)])));
        Assert.Equal([1L, 2L], Ids(all, ServerHostFindingsGrid.SeverityMatches, OmniDataGridFilterValues.Join([nameof(AnalysisSeverity.Critical), nameof(AnalysisSeverity.High)])));
        Assert.Equal([1L], Ids(all, ServerHostFindingsGrid.ExploitedMatches, OmniDataGridFilterValues.Join([bool.TrueString])));
        Assert.Equal([2L, 3L], Ids(all, ServerHostFindingsGrid.ExploitedMatches, OmniDataGridFilterValues.Join([bool.FalseString])));
        Assert.Equal([1L, 3L], Ids(all, ServerHostFindingsGrid.FixMatches, OmniDataGridFilterValues.Join([bool.TrueString])));
        Assert.Equal([2L], Ids(all, ServerHostFindingsGrid.FixMatches, OmniDataGridFilterValues.Join([bool.FalseString])));
    }

    [Fact]
    public void TheCause_IsReadFromTheFixableOnes_ANoFixFindingIsNeverACause()
    {
        Assert.Equal(ServerQualityOverview.GradeCause.Critical, ServerQualityOverview.CauseOf(Quality(1, "a", AnalysisGrade.F, new() { Critical = 2 }, new() { Critical = 1 })));
        Assert.Equal(ServerQualityOverview.GradeCause.High, ServerQualityOverview.CauseOf(Quality(1, "a", AnalysisGrade.D, new() { Critical = 1, High = 1 }, new() { Critical = 1 })));
        Assert.Equal(ServerQualityOverview.GradeCause.Other, ServerQualityOverview.CauseOf(Quality(1, "a", AnalysisGrade.B, new() { Low = 2 })));
        Assert.Equal(ServerQualityOverview.GradeCause.None, ServerQualityOverview.CauseOf(Quality(1, "a", AnalysisGrade.A, new() { Critical = 1 }, new() { Critical = 1 })));
        Assert.Equal(ServerQualityOverview.GradeCause.None, ServerQualityOverview.CauseOf(Quality(1, "a", AnalysisGrade.A)));
    }
}
