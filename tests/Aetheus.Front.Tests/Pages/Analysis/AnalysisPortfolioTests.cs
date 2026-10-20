// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Analysis;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Analysis;

public sealed class AnalysisPortfolioTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AnalysisPortfolioTests() => _handler = BunitTestHelper.RegisterServices(this);

    /// <summary>Filter the paged portfolio through column headers with choices from the
    /// filter-values endpoint.</summary>
    [Fact]
    public void FilterBar_IsReplacedByTheColumnHeaderFilters()
    {
        _handler.SetJsonResponse("api/analysis/portfolio/projects", new List<AnalysisPortfolioProjectDto>());
        _handler.SetJsonResponse("api/analysis/portfolio/filter-values", new AnalysisPortfolioFilterValuesDto
        {
            Organizations = ["Acme"],
            Projects = ["Website"],
            Scanners = ["semgrep"]
        });
        _handler.SetPaginatedJsonResponse("api/analysis/portfolio", Array.Empty<AnalysisPortfolioRowDto>());

        var cut = Render<AnalysisPortfolio>();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/analysis/portfolio/filter-values", StringComparison.Ordinal)));
        Assert.Empty(cut.FindAll(".analysis-portfolio-filters"));
        Assert.Empty(cut.FindAll("[aria-label='Search']"));
        Assert.Contains("aetheus-grid-fullheight", cut.Find(".analysis-table").ClassList);
    }

    /// <summary>A header filter reaches the portfolio API as a column filter.</summary>
    [Fact]
    public async Task HeaderFilter_ReachesTheApi()
    {
        _handler.SetJsonResponse("api/analysis/portfolio/projects", new List<AnalysisPortfolioProjectDto>());
        _handler.SetJsonResponse("api/analysis/portfolio/filter-values", new AnalysisPortfolioFilterValuesDto());
        _handler.SetPaginatedJsonResponse("api/analysis/portfolio", Array.Empty<AnalysisPortfolioRowDto>());
        var cut = Render<AnalysisPortfolio>();

        await cut.InvokeAsync(() => cut.Instance.LoadDataAsync(new GridLoadArgs
        {
            Skip = 0,
            Top = 25,
            Filters = [new GridFilterDescriptor("Category", "Secrets", OmniDataGridFilterOperator.Equals)]
        }));

        Assert.Contains(_handler.Requests, request =>
            Uri.UnescapeDataString(request.Url).Contains("api/analysis/portfolio?", StringComparison.Ordinal)
            && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Field=Category", StringComparison.Ordinal)
            && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Value=Secrets", StringComparison.Ordinal));
    }

    [Fact]
    public void Portfolio_RendersProjectGradeEvenWhenThereAreNoDetailedReports()
    {
        _handler.SetPaginatedJsonResponse("api/projects",
        [
            new ProjectDto { Id = 1, Name = "Aetheus" }
        ]);
        _handler.SetJsonResponse("api/analysis/portfolio/projects",
        new List<AnalysisPortfolioProjectDto>
        {
            new AnalysisPortfolioProjectDto
            {
                ProjectId = 1,
                ProjectName = "Aetheus",
                OrganizationName = "Aetheus",
                ReportCount = 0,
                Grade = new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.A }
            }
        });
        _handler.SetPaginatedJsonResponse("api/analysis/portfolio", Array.Empty<AnalysisPortfolioRowDto>());

        var cut = Render<AnalysisPortfolio>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("analysis-portfolio-project-card", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Aetheus", cut.Markup, StringComparison.Ordinal);
            // One rendering. The grade is the shared badge, A reading as healthy.
            var grade = cut.Find(".analysis-portfolio-grade");
            Assert.Equal("A", grade.TextContent.Trim());
            Assert.Contains("omni-badge--success", grade.ClassName, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Portfolio_PrimesDetailedReportsOnFirstRender()
    {
        _handler.SetPaginatedJsonResponse("api/projects", Array.Empty<ProjectDto>());
        _handler.SetJsonResponse("api/analysis/portfolio/projects", new List<AnalysisPortfolioProjectDto>());
        _handler.SetPaginatedJsonResponse("api/analysis/portfolio",
        [
            new AnalysisPortfolioRowDto
            {
                AnalysisReportId = 42,
                ProjectId = 7,
                ProjectName = "Atlas",
                OrganizationName = "Aetheus",
                ScannerName = "SonarQube",
                Category = AnalysisCategory.CodeQuality,
                CompletedAt = new DateTime(2026, 8, 11, 9, 0, 0, DateTimeKind.Utc)
            }
        ]);

        var cut = Render<AnalysisPortfolio>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("SonarQube", cut.Markup, StringComparison.Ordinal);
            Assert.Contains(_handler.Requests, request =>
                request.Method == HttpMethod.Get.Method
                && request.Url.Contains("api/analysis/portfolio?page=1&pageSize=25", StringComparison.Ordinal));
        });
    }

    /// <summary>
    /// SITE-FUNC-HOME-CAPABILITY-001: the row opens the run; the project link (and the run link) must
    /// keep their click and key, or the project link lands on the run.
    /// </summary>
    [Fact]
    public void RowLinks_DoNotLetTheirClickOrKeyReachTheRow()
    {
        _handler.SetPaginatedJsonResponse("api/projects", Array.Empty<ProjectDto>());
        _handler.SetJsonResponse("api/analysis/portfolio/projects", new List<AnalysisPortfolioProjectDto>());
        _handler.SetPaginatedJsonResponse("api/analysis/portfolio",
        [
            new AnalysisPortfolioRowDto
            {
                AnalysisReportId = 42,
                PipelineRunId = 714,
                ProjectId = 7,
                ProjectName = "Atlas",
                OrganizationName = "Aetheus",
                ScannerName = "SonarQube",
                Category = AnalysisCategory.CodeQuality,
                CompletedAt = new DateTime(2026, 8, 11, 9, 0, 0, DateTimeKind.Utc)
            }
        ]);

        var cut = Render<AnalysisPortfolio>();

        var projectLink = cut.WaitForElement("tr a[href='projects/7/quality']");
        Aetheus.Front.Tests.TestDoubles.RowEventIsolation.AssertRowDoesNotReceive(projectLink, "onclick", "onkeydown");
        Aetheus.Front.Tests.TestDoubles.RowEventIsolation.AssertRowDoesNotReceive(
            cut.Find("tr a[href='pipelines/runs/714']"), "onclick", "onkeydown");
    }

    /// <summary>The export reads the page the grid asks for, in the grid's sort, capped at 200 rows.</summary>
    [Theory]
    [InlineData(50, 25, GridSortOrder.Descending, "page=3&pageSize=25", "sortDescending=True")]
    [InlineData(null, 1000, GridSortOrder.Ascending, "page=1&pageSize=200", "sortDescending=False")]
    public async Task TheExport_ReadsTheRequestedPageInTheGridsSort(int? skip, int top, GridSortOrder order, string expectedPage, string expectedSort)
    {
        _handler.SetJsonResponse("api/analysis/portfolio/projects", new List<AnalysisPortfolioProjectDto>());
        _handler.SetJsonResponse("api/analysis/portfolio/filter-values", new AnalysisPortfolioFilterValuesDto());
        _handler.SetPaginatedJsonResponse("api/analysis/portfolio", [new AnalysisPortfolioRowDto { ProjectName = "Website" }]);
        var cut = Render<AnalysisPortfolio>();
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Url.Contains("api/analysis/portfolio?", StringComparison.Ordinal)));

        var page = await cut.InvokeAsync(() => cut.Instance.ReadExportAsync(new GridLoadArgs
        {
            Skip = skip,
            Top = top,
            Sorts = [new GridSortDescriptor("ProjectName", order)],
        }));

        Assert.Equal("Website", Assert.Single(page.Items).ProjectName);
        var url = _handler.Requests.Last(request => request.Url.Contains("api/analysis/portfolio?", StringComparison.Ordinal)).Url;
        Assert.Contains(expectedPage, url, StringComparison.Ordinal);
        Assert.Contains("sortBy=ProjectName", url, StringComparison.Ordinal);
        Assert.Contains(expectedSort, url, StringComparison.Ordinal);
    }
}
