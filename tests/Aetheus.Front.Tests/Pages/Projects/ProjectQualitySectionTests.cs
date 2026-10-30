// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Projects;

public class ProjectQualitySectionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectQualitySectionTests() => _handler = BunitTestHelper.RegisterServices(this);

    [Fact]
    public void FindingQuery_ValuesContainingTheFormerSeparatorRemainDistinct()
    {
        var first = new FindingQuery(1, 25, "owner|main", "", "", "", null, null, null, "team", null, false);
        var second = new FindingQuery(1, 25, "owner", "", "", "", null, null, "main", "team", null, false);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CoverageTrend_IsNotRenderedOnTheOverview()
    {
        // Fetch trend data for the tests trend without rendering a coverage trend block.
        var date = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        _handler.SetJsonResponse("api/pipelines/projects/1/quality-trend", new ProjectQualityTrendDto
        {
            Coverage = [new CoverageTrendPointDto { RunId = 10, Date = date, LineRate = 0.80, BranchRate = 0.60 }],
            Tests = [new TestTrendPointDto { RunId = 10, Date = date, Passed = 42, Failed = 0, Skipped = 1 }]
        });
        MockAnalysisEndpoints(1);

        var cut = Render<ProjectQualitySection>(p => p.Add(c => c.ProjectId, 1));

        cut.WaitForState(() => !cut.Markup.Contains("aetheus-loader", StringComparison.Ordinal));
        Assert.DoesNotContain("CoverageTrend", cut.Markup);
    }

    // The >=2-point chart path is covered by the browser E2E suite; these bUnit cases focus on the
    // component's empty and single-point states.

    [Fact]
    public void Renders_EmptyState_WhenNoData()
    {
        _handler.SetJsonResponse("api/pipelines/projects/2/quality-trend", new ProjectQualityTrendDto());
        MockAnalysisEndpoints(2);

        var cut = Render<ProjectQualitySection>(p => p.Add(c => c.ProjectId, 2));

        cut.WaitForState(() => cut.Markup.Contains("NoQualityData"));
        Assert.Contains("NoQualityData", cut.Markup);
    }

    [Fact]
    public void Renders_GlobalGradeBeforeDomainDetails()
    {
        _handler.SetJsonResponse("api/pipelines/projects/4/quality-trend", new ProjectQualityTrendDto());
        MockAnalysisEndpoints(4);
        _handler.SetJsonResponse("api/analysis/projects/4/summary", new AnalysisProjectSummaryDto
        {
            Grade = new AnalysisGradeSummaryDto
            {
                OverallGrade = AnalysisGrade.B,
                Completeness = AnalysisGradeCompleteness.Complete,
                LimitingDomain = AnalysisGradeDomain.CodeQuality,
                CommitHash = new string('a', 40),
                EvaluatedAt = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc),
                Domains =
                [
                    new AnalysisGradeDomainDto
                    {
                        Domain = AnalysisGradeDomain.CodeQuality,
                        Grade = AnalysisGrade.B,
                        Required = true,
                        Completeness = AnalysisGradeCompleteness.Complete,
                        EvaluatedAt = new DateTime(2025, 12, 31, 12, 0, 0, DateTimeKind.Utc),
                        Measures =
                        [
                            new AnalysisGradeMeasureDto
                            {
                                Key = "grade.duplication",
                                Domain = AnalysisGradeDomain.CodeQuality,
                                Grade = AnalysisGrade.B,
                                Required = true,
                                Observed = true,
                                ObservedValue = 4,
                                Unit = "percent"
                            }
                        ]
                    }
                ]
            }
        });

        var cut = Render<ProjectQualitySection>(parameters => parameters.Add(
            component => component.ProjectId,
            4));

        cut.WaitForState(() => cut.Markup.Contains("AnalysisGlobalGrade"));
        Assert.Contains("analysis-grade-letter", cut.Markup);
        // The hero is the shared badge, sized by analysis-grade-letter alone.
        Assert.NotEmpty(cut.FindAll(".grade-badge.analysis-grade-letter"));
        Assert.Contains("AnalysisDuplicatedCode", cut.Markup);
        Assert.DoesNotContain(">grade.duplication<", cut.Markup);
        // R-32: the tiles sit on a row of their own, as wide as the card, never squeezed beside the letter.
        Assert.Empty(cut.FindAll(".analysis-grade-primary .analysis-grade-meta-tiles"));
        Assert.NotEmpty(cut.FindAll(".analysis-grade-overview > .analysis-grade-meta-tiles, .analysis-grade-overview .omni-card__body > .analysis-grade-meta-tiles"));
        // Display the first eight commit characters in a tile.
        var commit = cut.FindAll(".analysis-grade-meta-tiles .omni-stat-tile")
            .Single(tile => tile.QuerySelector(".omni-stat-tile__label")!.TextContent.Trim() == "Commit");
        Assert.Equal("aaaaaaaa", commit.QuerySelector(".omni-stat-tile__value")!.TextContent.Trim());
        // Omit limiting-domain prose and the required, complete and freshness line.
        Assert.DoesNotContain("AnalysisLimitingDomain", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("AnalysisFreshness", cut.Markup, StringComparison.Ordinal);
    }

    // Recettes R-73 and R-74: a grade is never "incomplete". A grade computed on part of its measures says so and how many
    // are missing; a project nothing measured yet says there is no measure, never "incomplete".
    [Theory]
    [InlineData(false, "quality-grade-partial")]
    [InlineData(true, "quality-grade-no-measure")]
    public void TheHeadline_SaysPartialOrNoMeasure_NeverIncomplete(bool nothingMeasured, string expectedTestId)
    {
        _handler.SetJsonResponse("api/pipelines/projects/6/quality-trend", new ProjectQualityTrendDto());
        MockAnalysisEndpoints(6);
        _handler.SetJsonResponse("api/analysis/projects/6/summary", new AnalysisProjectSummaryDto
        {
            Grade = new AnalysisGradeSummaryDto
            {
                OverallGrade = nothingMeasured ? null : AnalysisGrade.B,
                Completeness = AnalysisGradeCompleteness.Incomplete,
                Domains =
                [
                    new AnalysisGradeDomainDto
                    {
                        Domain = AnalysisGradeDomain.Architecture,
                        Grade = nothingMeasured ? null : AnalysisGrade.B,
                        Required = true,
                        Completeness = AnalysisGradeCompleteness.Incomplete,
                        Measures =
                        [
                            new AnalysisGradeMeasureDto
                            {
                                Key = "grade.architecture.medium", Domain = AnalysisGradeDomain.Architecture, Required = true,
                                Observed = !nothingMeasured, ObservedValue = nothingMeasured ? null : 2,
                                Grade = nothingMeasured ? null : AnalysisGrade.B, Severity = AnalysisSeverity.Medium
                            },
                            new AnalysisGradeMeasureDto
                            {
                                Key = "grade.coverage.line", Domain = AnalysisGradeDomain.Architecture, Required = true, Observed = false,
                                ProducerStep = "Publish coverage evidence"
                            }
                        ]
                    }
                ]
            }
        });

        var cut = Render<ProjectQualitySection>(parameters => parameters.Add(component => component.ProjectId, 6));

        cut.WaitForState(() => cut.Markup.Contains("AnalysisGlobalGrade"));
        Assert.NotNull(cut.Find($"[data-testid='{expectedTestId}']"));
        Assert.DoesNotContain("Incomplete", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Groups_CodeQuality_And_Tests_With_WorstGrade_And_CrapScore()
    {
        _handler.SetJsonResponse("api/pipelines/projects/5/quality-trend", new ProjectQualityTrendDto
        {
            Complexity =
            [
                new ComplexityTrendPointDto
                {
                    RunId = 51,
                    Date = new DateTime(2026, 8, 5, 8, 0, 0, DateTimeKind.Utc),
                    AvgCyclomatic = 3.2,
                    MaxCyclomatic = 25,
                    CrapAvg = 8.2
                }
            ]
        });
        MockAnalysisEndpoints(5);
        _handler.SetJsonResponse("api/analysis/projects/5/summary", new AnalysisProjectSummaryDto
        {
            Grade = new AnalysisGradeSummaryDto
            {
                OverallGrade = AnalysisGrade.C,
                Completeness = AnalysisGradeCompleteness.Complete,
                LimitingDomain = AnalysisGradeDomain.CodeQuality,
                Domains =
                [
                    new AnalysisGradeDomainDto
                    {
                        Domain = AnalysisGradeDomain.Reliability,
                        Grade = AnalysisGrade.A,
                        Required = true,
                        Completeness = AnalysisGradeCompleteness.Complete,
                        Measures =
                        [
                            new AnalysisGradeMeasureDto
                            {
                                Key = "grade.coverage.line",
                                Domain = AnalysisGradeDomain.Reliability,
                                Grade = AnalysisGrade.A,
                                Observed = true,
                                ObservedValue = 75.9,
                                Unit = "percent"
                            }
                        ]
                    },
                    new AnalysisGradeDomainDto
                    {
                        Domain = AnalysisGradeDomain.CodeQuality,
                        Grade = AnalysisGrade.C,
                        Required = true,
                        Completeness = AnalysisGradeCompleteness.Complete,
                        Measures =
                        [
                            new AnalysisGradeMeasureDto
                            {
                                Key = "grade.complexity.maximum",
                                Domain = AnalysisGradeDomain.CodeQuality,
                                Grade = AnalysisGrade.C,
                                Observed = true,
                                ObservedValue = 25
                            }
                        ]
                    }
                ]
            }
        });

        var cut = Render<ProjectQualitySection>(parameters => parameters.Add(component => component.ProjectId, 5));

        cut.WaitForState(() => cut.Markup.Contains("AnalysisCrapAverage", StringComparison.Ordinal));
        var combined = Assert.Single(cut.FindAll(".analysis-grade-domain"), element =>
            element.TextContent.Contains("AnalysisCodeQualityAndTests", StringComparison.Ordinal));
        Assert.Contains("grade-badge--c", combined.InnerHtml, StringComparison.Ordinal);
        Assert.Contains("AnalysisTestedLines", combined.TextContent);
        Assert.Contains("75", combined.TextContent);
        Assert.Contains("%", combined.TextContent);
        Assert.Contains("AnalysisMaximumComplexity", combined.TextContent);
        Assert.Contains("AnalysisCrapAverage", combined.TextContent);
        Assert.Matches("8[.,]2", combined.TextContent);
        Assert.DoesNotContain(">grade.", combined.InnerHtml);
    }

    [Fact]
    public void Uses_Client_RenderMode_With_Exactly_Two_Top_Tabs()
    {
        _handler.SetJsonResponse("api/pipelines/projects/2/quality-trend", new ProjectQualityTrendDto());
        MockAnalysisEndpoints(2);
        _handler.SetJsonResponse(
            "api/analysis/projects/2/findings?page=1&pageSize=25&severity=Critical&status=Open",
            new PaginatedResult<AnalysisFindingDto>());

        var cut = Render<ProjectQualitySection>(parameters => parameters.Add(component => component.ProjectId, 2));

        cut.WaitForState(() => cut.Markup.Contains("NoQualityData"));
        Assert.True(cut.FindComponent<UrlSyncedTabs>().Instance.RenderAllPanels);
        Assert.Equal(2, cut.FindComponent<UrlSyncedTabs>().FindAll("[role='tab']").Count);
    }

    [Fact]
    public void Overview_Does_Not_Load_The_Findings_Grid()
    {
        _handler.SetJsonResponse("api/pipelines/projects/2/quality-trend", new ProjectQualityTrendDto());
        MockAnalysisEndpoints(2);

        var cut = Render<ProjectQualitySection>(parameters => parameters.Add(component => component.ProjectId, 2));

        cut.WaitForState(() => cut.Markup.Contains("NoQualityData", StringComparison.Ordinal));
        Assert.DoesNotContain(
            _handler.Requests,
            request => request.Url.Contains("api/analysis/projects/2/findings", StringComparison.Ordinal));
    }

    [Fact]
    public void SummaryCards_ShowOnlyTheCountsAboveZero()
    {
        // A count card with nothing in it is not drawn; the others keep their shortcut.
        _handler.SetJsonResponse("api/pipelines/projects/2/quality-trend", new ProjectQualityTrendDto());
        MockAnalysisEndpoints(2, new AnalysisProjectSummaryDto { OpenCount = 3, HighCount = 2 });

        var cut = Render<ProjectQualitySection>(parameters => parameters.Add(component => component.ProjectId, 2));
        cut.WaitForElement("[data-quality-shortcut='open']");

        Assert.Equal(["open", "high"], cut.FindAll("[data-quality-shortcut]").Select(card => card.GetAttribute("data-quality-shortcut")));
        Assert.Single(cut.FindAll(".analysis-summary-static"));
    }

    [Fact]
    public void SummaryShortcut_Opens_Filtered_Findings_And_Updates_The_Url()
    {
        _handler.SetJsonResponse("api/pipelines/projects/2/quality-trend", new ProjectQualityTrendDto());
        MockAnalysisEndpoints(2, new AnalysisProjectSummaryDto { CriticalCount = 1, OpenCount = 1 });
        _handler.SetJsonResponse(
                            "api/analysis/projects/2/findings?page=1&pageSize=25&severity=Critical&status=Open",
            new PaginatedResult<AnalysisFindingDto>());

        var cut = Render<ProjectQualitySection>(parameters => parameters.Add(component => component.ProjectId, 2));
        cut.WaitForElement("[data-quality-shortcut='critical']");

        cut.Find("[data-quality-shortcut='critical']").Click();

        cut.WaitForState(
            () => Services.GetRequiredService<NavigationManager>().Uri.Contains("tab=findings", StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Contains(
            _handler.Requests,
            request => request.Method == "GET"
                       && request.Url.Contains(
                           "api/analysis/projects/2/findings?page=1&pageSize=25&severity=Critical&status=Open",
                           StringComparison.Ordinal)));
    }

    [Fact]
    public void Opening_Findings_Loads_The_Lazy_Rendered_Grid()
    {
        _handler.SetJsonResponse("api/pipelines/projects/2/quality-trend", new ProjectQualityTrendDto());
        MockAnalysisEndpoints(2);
        _handler.SetJsonResponse(
            "api/analysis/projects/2/findings?page=1&pageSize=25&status=Open",
            new PaginatedResult<AnalysisFindingDto>
            {
                Items =
                [
                    new AnalysisFindingDto
                    {
                        Id = 42,
                        ProjectId = 2,
                        RuleId = "live-grid",
                        Title = "Finding loaded on activation"
                    }
                ],
                TotalCount = 1,
                Page = 1,
                PageSize = 25
            });

        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("tab", "findings"));
        var cut = Render<ProjectQualitySection>(parameters => parameters.Add(component => component.ProjectId, 2));

        cut.WaitForState(
            () => cut.Markup.Contains("Finding loaded on activation", StringComparison.Ordinal));
        Assert.Contains("Finding loaded on activation", cut.Markup);
        Assert.Single(_handler.Requests, request => request.Url.Contains(
            "api/analysis/projects/2/findings?page=1&pageSize=25&status=Open",
            StringComparison.Ordinal));
    }

    /// <summary>The Status, Severity and Category cells of the findings list show the translated
    /// name (the text of the Enum_ resource, the one the column's filter lists), the status in a
    /// badge, never the raw enum member ("Open").</summary>
    [Fact]
    public void Findings_Cells_Show_The_Translated_Enum_Names_The_Filter_Lists()
    {
        _handler.SetJsonResponse("api/pipelines/projects/2/quality-trend", new ProjectQualityTrendDto());
        MockAnalysisEndpoints(2);
        _handler.SetJsonResponse(
            "api/analysis/projects/2/findings?page=1&pageSize=25&status=Open",
            new PaginatedResult<AnalysisFindingDto>
            {
                Items =
                [
                    new AnalysisFindingDto
                    {
                        Id = 42, ProjectId = 2, RuleId = "rule", Title = "Translated cells",
                        Status = AnalysisFindingStatus.Open, Severity = AnalysisSeverity.High, Category = AnalysisCategory.Secrets
                    }
                ],
                TotalCount = 1,
                Page = 1,
                PageSize = 25
            });
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("tab", "findings"));

        var cut = Render<ProjectQualitySection>(parameters => parameters.Add(component => component.ProjectId, 2));
        cut.WaitForState(() => cut.Markup.Contains("Translated cells", StringComparison.Ordinal));

        var status = cut.Find("tbody td[data-omni-col='Status']");
        Assert.Equal("Enum_AnalysisFindingStatus_Open", status.QuerySelector(".omni-badge")?.TextContent.Trim());
        var openFilterOption = cut.FindAll("th[data-omni-col='Status'] .omni-multi-select__option")
            .Single(option => option.GetAttribute("aria-selected") == "true");
        Assert.Contains(status.TextContent.Trim(), openFilterOption.TextContent, StringComparison.Ordinal);
        Assert.Equal("Enum_AnalysisSeverity_High", cut.Find("tbody td[data-omni-col='Severity'] .omni-badge").TextContent.Trim());
        Assert.Equal("Enum_AnalysisCategory_Secrets", cut.Find("tbody td[data-omni-col='Category']").TextContent.Trim());
    }

    /// <summary>The default "open only" filter is the Status column's own filter, ticked and marked
    /// active in its header, and lifting it there loads every status.</summary>
    [Fact]
    public void DefaultOpenFilter_IsVisibleInTheStatusHeader_AndLiftingItLoadsEveryStatus()
    {
        _handler.SetJsonResponse("api/pipelines/projects/2/quality-trend", new ProjectQualityTrendDto());
        MockAnalysisEndpoints(2);
        _handler.SetJsonResponse("api/analysis/projects/2/findings?page=1&pageSize=25", new PaginatedResult<AnalysisFindingDto>());
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("tab", "findings"));

        var cut = Render<ProjectQualitySection>(parameters => parameters.Add(component => component.ProjectId, 2));
        var toggle = cut.WaitForElement("th[data-omni-col='Status'] .omni-data-grid__filter-menu-toggle");

        Assert.Contains("omni-data-grid__filter-menu-toggle--active", toggle.ClassName, StringComparison.Ordinal);
        var open = cut.FindAll("th[data-omni-col='Status'] .omni-multi-select__option")
            .Single(option => option.GetAttribute("aria-selected") == "true");
        Assert.Contains("Open", open.TextContent, StringComparison.Ordinal);

        cut.Find("th[data-omni-col='Status'] .omni-data-grid__filter-reset").Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            request.Url.EndsWith("api/analysis/projects/2/findings?page=1&pageSize=25", StringComparison.Ordinal)));
    }

    /// <summary>Ticking a second status sends both, in the list parameter.</summary>
    [Fact]
    public void TickingASecondStatus_SendsBothStatuses()
    {
        _handler.SetJsonResponse("api/pipelines/projects/2/quality-trend", new ProjectQualityTrendDto());
        MockAnalysisEndpoints(2);
        _handler.SetJsonResponse("api/analysis/projects/2/findings?page=1&pageSize=25&statuses=Fixed&statuses=Open", new PaginatedResult<AnalysisFindingDto>());
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("tab", "findings"));

        var cut = Render<ProjectQualitySection>(parameters => parameters.Add(component => component.ProjectId, 2));
        cut.WaitForElement("th[data-omni-col='Status'] .omni-multi-select__checkbox");

        // Members in declaration order: Open, Fixed, Accepted, FalsePositive, Mitigated.
        cut.FindAll("th[data-omni-col='Status'] .omni-multi-select__checkbox")[1].Change(true);

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("findings?page=1&pageSize=25&statuses=Fixed&statuses=Open", StringComparison.Ordinal)));
    }

    [Fact]
    public void Same_Project_Rerender_Does_Not_Reload_Shared_Quality_State()
    {
        _handler.SetJsonResponse("api/pipelines/projects/2/quality-trend", new ProjectQualityTrendDto());
        MockAnalysisEndpoints(2);

        var cut = Render<ProjectQualitySection>(parameters => parameters.Add(component => component.ProjectId, 2));
        cut.WaitForState(
            () => _handler.Requests.Any(request => request.Url.Contains("/summary", StringComparison.Ordinal)));
        var summaryRequests = _handler.Requests.Count(request =>
            request.Url.Contains("api/analysis/projects/2/summary", StringComparison.Ordinal));

        cut.Render(parameters => parameters.Add(component => component.ProjectId, 2));

        Assert.Equal(summaryRequests, _handler.Requests.Count(request =>
            request.Url.Contains("api/analysis/projects/2/summary", StringComparison.Ordinal)));
    }

    /// <summary>R-34: no "Export all" button under the findings grid; the grid's own MD button stays.</summary>
    [Fact]
    public void FindingsTab_HasNoExportAllButtonBelowTheGrid()
    {
        const int projectId = 6;
        _handler.SetJsonResponse($"api/pipelines/projects/{projectId}/quality-trend", new ProjectQualityTrendDto());
        _handler.SetJsonResponse($"api/analysis/projects/{projectId}/summary", new AnalysisProjectSummaryDto());
        _handler.SetJsonResponse(
            $"api/analysis/projects/{projectId}/findings?page=1&pageSize=25&status=Open",
            new PaginatedResult<AnalysisFindingDto> { Items = [Finding(1, "Grid finding")], TotalCount = 1, Page = 1, PageSize = 25 });
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("tab", "findings"));
        var cut = Render<ProjectQualitySection>(parameters => parameters
            .Add(component => component.ProjectId, projectId)
            .AddCascadingValue(new Aetheus.Front.Components.Shared.ProjectSectionMenu()));

        cut.WaitForState(() => cut.FindAll(".analysis-table").Count == 1);

        Assert.Empty(cut.FindAll(".analysis-findings-export"));
        Assert.DoesNotContain("AnalysisExportAllAiPrompt", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>The overview tab adds nothing to the header menu (Follow and Edit only).</summary>
    [Fact]
    public void OverviewTab_AddsNoHeaderMenuAction()
    {
        _handler.SetJsonResponse("api/pipelines/projects/7/quality-trend", new ProjectQualityTrendDto());
        MockAnalysisEndpoints(7);
        var menu = new Aetheus.Front.Components.Shared.ProjectSectionMenu();
        menu.Set([new Aetheus.Front.Components.Shared.ProjectSectionMenuAction("download", "stale", null, false, () => Task.CompletedTask)]);

        var cut = Render<ProjectQualitySection>(parameters => parameters
            .Add(component => component.ProjectId, 7)
            .AddCascadingValue(menu));

        cut.WaitForState(() => menu.Actions.Count == 0);
    }

    /// <summary>The commit, the latest analysis run and the latest release are links.</summary>
    [Fact]
    public void Overview_LinksTheCommitTheLastRunAndTheLatestRelease()
    {
        _handler.SetJsonResponse("api/pipelines/projects/8/quality-trend", new ProjectQualityTrendDto());
        _handler.SetJsonResponse("api/analysis/projects/8/summary", new AnalysisProjectSummaryDto
        {
            ProjectId = 8,
            Grade = new AnalysisGradeSummaryDto { CommitHash = "773f2080aabbccddeeff00112233445566778899" },
            GradeCommitId = 41,
            LastAnalysisRunId = 2464,
            LatestReleaseId = 12,
            LatestReleaseVersion = "1.4.2"
        });

        var cut = Render<ProjectQualitySection>(parameters => parameters.Add(component => component.ProjectId, 8));

        // Each figure is a tile linking to its detail page.
        // (OmniStatTile.Href: the tile root is itself the link, so it carries the address.)
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll(".analysis-grade-meta-tiles .omni-stat-tile").Count));
        foreach (var (value, target) in new[] { ("773f2080", "/git-repositories/commits/41"), ("#2464", "/pipelines/runs/2464"), ("1.4.2", "/releases/12") })
        {
            var tile = cut.FindAll(".analysis-grade-meta-tiles .omni-stat-tile")
                .Single(candidate => candidate.QuerySelector(".omni-stat-tile__value")!.TextContent.Trim() == value);
            Assert.Equal("a", tile.TagName.ToLowerInvariant());
            Assert.Equal(target, tile.GetAttribute("href"));
        }
    }

    private static AnalysisFindingDto Finding(int id, string title) => new()
    {
        Id = id,
        ProjectId = 6,
        RuleId = $"rule-{id}",
        Title = title,
        Message = $"Evidence {id}",
        Category = AnalysisCategory.CodeQuality,
        Severity = AnalysisSeverity.Low,
        Status = AnalysisFindingStatus.Open
    };

    /// <summary>The ID column's filter is applied by the server: an "equals" travels as both bounds.</summary>
    [Fact]
    public async Task TheIdColumnFilter_IsSentToTheServer()
    {
        _handler.SetJsonResponse("api/pipelines/projects/2/quality-trend", new ProjectQualityTrendDto());
        MockAnalysisEndpoints(2);
        _handler.SetJsonResponse("api/analysis/projects/2/findings", new PaginatedResult<AnalysisFindingDto>());
        var cut = Render<ProjectQualitySection>(parameters => parameters.Add(component => component.ProjectId, 2));
        var load = typeof(ProjectQualitySection).GetMethod("LoadFindingsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        await cut.InvokeAsync(() => (Task)load.Invoke(cut.Instance, [new GridLoadArgs
        {
            Skip = 0, Top = 25,
            Filters = [new GridFilterDescriptor("Id", "1094", OmniDataGridFilterOperator.Equals)]
        }])!);

        Assert.Contains(_handler.Requests, request => request.Url.Contains("api/analysis/projects/2/findings", StringComparison.Ordinal)
            && request.Url.Contains("idFrom=1094", StringComparison.Ordinal)
            && request.Url.Contains("idTo=1094", StringComparison.Ordinal));
    }

    private static List<AnalysisFindingDto> Findings(int firstId, int count) =>
    [
        .. Enumerable.Range(firstId, count).Select(id => new AnalysisFindingDto
        {
            Id = id, ProjectId = 2, RuleId = "CA2000", Title = $"Finding {id}",
            Status = AnalysisFindingStatus.Open, Severity = AnalysisSeverity.High, Category = AnalysisCategory.Secrets,
            LatestOccurrence = new AnalysisFindingOccurrenceDto { FilePath = "src/App.cs", StartLine = id, IsNew = id == 1 }
        })
    ];

    /// <summary>The export bar reads every matching finding through paginated API requests with the
    /// grid Status filter. Export cells use translated names and path:line formatting.</summary>
    [Fact]
    public async Task R2124_TheFindingsTable_ExportsEveryFindingAsMarkdown_ReadPageByPage()
    {
        _handler.SetJsonResponse("api/pipelines/projects/2/quality-trend", new ProjectQualityTrendDto());
        MockAnalysisEndpoints(2);
        _handler.SetJsonResponse("api/analysis/projects/2/findings?page=1&pageSize=25&status=Open",
            new PaginatedResult<AnalysisFindingDto> { Items = Findings(1, 25), TotalCount = 201, Page = 1, PageSize = 25 });
        _handler.SetJsonResponse("api/analysis/projects/2/findings?page=1&pageSize=200&status=Open",
            new PaginatedResult<AnalysisFindingDto> { Items = Findings(1, 200), TotalCount = 201, Page = 1, PageSize = 200 });
        _handler.SetJsonResponse("api/analysis/projects/2/findings?page=2&pageSize=200&status=Open",
            new PaginatedResult<AnalysisFindingDto> { Items = Findings(201, 1), TotalCount = 201, Page = 2, PageSize = 200 });
        var module = JSInterop.SetupModule("./_content/OmniEurope.Blazor/omni-document-editor.js");
        module.SetupVoid("download", _ => true).SetVoidResult();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("tab", "findings"));
        var cut = Render<ProjectQualitySection>(parameters => parameters.Add(component => component.ProjectId, 2));
        cut.WaitForState(() => cut.Markup.Contains("Finding 25", StringComparison.Ordinal));

        // Export Markdown only.
        await Assert.Single(cut.FindAll(".omni-data-grid__export-button")).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["download"]));
        var download = module.Invocations["download"].Single();
        var markdown = System.Text.Encoding.UTF8.GetString(Assert.IsType<byte[]>(download.Arguments[2]));
        Assert.Contains(_handler.Requests, request => request.Url.EndsWith("findings?page=2&pageSize=200&status=Open", StringComparison.Ordinal));
        var rows = markdown.Split('\n').Where(line => line.Contains("| Finding ", StringComparison.Ordinal)).ToList();
        Assert.Equal(201, rows.Count);
        Assert.Contains("AnalysisFindings", markdown, StringComparison.Ordinal);
        Assert.Contains("GridExportAddress", markdown, StringComparison.Ordinal);
        // The Status filter of the grid is said in the header, next to where the rows come from.
        Assert.Contains("Enum_AnalysisFindingStatus_Open", markdown.Split("| ID", 2)[0], StringComparison.Ordinal);
        // One row as the screen shows it: id, translated severity and category, rule, title, path:line, status, new.
        Assert.Contains(
            "| 1 | Enum_AnalysisSeverity_High | Enum_AnalysisCategory_Secrets | CA2000 | Finding 1 | src/App.cs:1 | Enum_AnalysisFindingStatus_Open | Yes |",
            markdown, StringComparison.Ordinal);
        Assert.Contains("| Finding 201 | src/App.cs:201 |", markdown, StringComparison.Ordinal);
        Assert.Matches(@"\.md$", Assert.IsType<string>(download.Arguments[0]));
    }

    private void MockAnalysisEndpoints(int projectId, AnalysisProjectSummaryDto? summary = null)
    {
        var prefix = $"api/analysis/projects/{projectId}";
        _handler.SetJsonResponse($"{prefix}/summary", summary ?? new AnalysisProjectSummaryDto());
        _handler.SetJsonResponse($"{prefix}/findings?page=1&pageSize=25&status=Open", new PaginatedResult<AnalysisFindingDto>());
    }
}
