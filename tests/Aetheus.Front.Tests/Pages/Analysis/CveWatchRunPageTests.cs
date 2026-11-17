// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Analysis;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Analysis;

/// <summary>R-22: the page of one run of the CVE watch, the target of the link of the Scan column.</summary>
public sealed class CveWatchRunPageTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public CveWatchRunPageTests() => _handler = BunitTestHelper.RegisterServices(this);

    [Fact]
    public void TheRun_ShowsItsCounts_ItsFindingsAndTheSourcesOfNow()
    {
        _handler.SetJsonResponse("api/analysis/cve-watch/runs/58", new CveWatchRunDto
        {
            ReportId = 58,
            ProjectId = 1,
            ProjectName = "Aetheus",
            Category = AnalysisCategory.Dependencies,
            WrittenAt = new DateTime(2026, 11, 3, 8, 17, 0, DateTimeKind.Utc),
            FindingCount = 4,
            NewFindingCount = 1,
            PackageCount = 3,
            Findings =
            [
                new() { FindingId = 301, Title = "CVE-2026-21001 in Contoso.Session.Core", Severity = AnalysisSeverity.Critical, Status = AnalysisFindingStatus.Open, IsNew = true, Packages = ["Contoso.Session.Core"] }
            ],
            Sources = [new() { Source = "kev", IsStale = false, LastSuccessAt = new DateTime(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc) }]
        });

        var cut = Render<CveWatchRunPage>(parameters => parameters.Add(page => page.Id, 58));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("4", cut.Find("[data-testid='cve-run-found']").TextContent);
            Assert.Contains("1", cut.Find("[data-testid='cve-run-new']").TextContent);
            Assert.Contains("3", cut.Find("[data-testid='cve-run-packages']").TextContent);
            var finding = cut.FindAll("a").Single(link => link.TextContent.Contains("Contoso.Session.Core", StringComparison.Ordinal) && link.GetAttribute("href")!.Contains("analysis/findings/301", StringComparison.Ordinal));
            Assert.NotNull(finding);
            Assert.Contains("kev", cut.Find("[data-testid='cve-sources']").TextContent, StringComparison.Ordinal);
        });
        Assert.Contains(_handler.Requests, request => request.Url.EndsWith("api/analysis/cve-watch/runs/58", StringComparison.Ordinal));
    }

    [Fact]
    public void R21_TheRunOfAProject_StaysInThatProject_TrailMenuAndBackArrow()
    {
        _handler.SetJsonResponse("api/analysis/cve-watch/runs/58", new CveWatchRunDto
        {
            ReportId = 58,
            ProjectId = 5,
            ProjectName = "Wordmorrow",
            WrittenAt = new DateTime(2026, 11, 3, 8, 17, 0, DateTimeKind.Utc)
        });

        var cut = Render<CveWatchRunPage>(parameters => parameters.Add(page => page.Id, 58));

        cut.WaitForAssertion(() =>
        {
            var items = Services.GetRequiredService<Aetheus.Front.Components.Shared.BreadcrumbService>().Items;
            Assert.Equal(["Projects", "Wordmorrow", "AnalysisPortfolio"], items.Take(3).Select(item => item.Text));
            Assert.Equal("/projects/5/quality", items[2].Href);
            Assert.Equal(5, Services.GetRequiredService<Aetheus.Front.Components.Shared.ProjectNavContextService>().ProjectId);
            Assert.Contains("/projects/5/quality", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AnUnknownOrUnreadableRun_IsNotFound_NotAnEmptyRun()
    {
        _handler.SetResponse("api/analysis/cve-watch/runs/999", HttpStatusCode.NotFound);

        var cut = Render<CveWatchRunPage>(parameters => parameters.Add(page => page.Id, 999));

        cut.WaitForAssertion(() => Assert.Contains("CveWatchRunNotFound", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='cve-run-found']"));
    }
}
