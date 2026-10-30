// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Analysis;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.Architecture;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

public sealed class PipelineRunGateFindingsTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunGateFindingsTests() => _handler = BunitTestHelper.RegisterServices(this);

    /// <summary>The Gate tab reads its findings from the run findings route; this serves the gate's
    /// findings there, the open ones by default and every one when the decided are asked
    /// for.</summary>
    private void ServeFindings(AnalysisRunGateDto gate)
    {
        AnalysisRunFindingsPageDto Page(IReadOnlyList<AnalysisRunGateFindingDto> items) => new()
        {
            Items = items,
            TotalCount = items.Count,
            Page = 1,
            PageSize = 25,
            OpenCount = gate.FindingCount,
            NewOpenCount = gate.NewFindingCount,
            DecidedCount = gate.DecidedFindingCount
        };
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings",
            Page([.. gate.Findings.Where(finding => finding.Status == AnalysisFindingStatus.Open)]));
        // Asynchronous responses are matched first: a request asking for the decided ones gets them all.
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "includeDecided=true", _ => Task.FromResult(Page(gate.Findings)));
    }

    [Fact]
    public void R1_36_ARuleAlertIsNeitherATileNorAList_AThresholdIsAFinding()
    {
        // The API still carries the policy alerts. The tab does not show them: what a threshold detects is a
        // finding (ADR-053), listed and counted with the others.
        var gate = new AnalysisRunGateDto
        {
            PipelineRunId = 2653,
            FindingCount = 1,
            Findings = [new AnalysisRunGateFindingDto { FindingId = 1, Title = "coverage.line.percent is below its threshold", Status = AnalysisFindingStatus.Open }],
            Violations =
            [
                new AnalysisRunGateViolationDto { PolicyKey = "quality.coverage.line", Outcome = "Warn", TargetKind = "metric", TargetKey = "coverage.line.percent", ObservedValue = "70", Operator = "LessThan", Threshold = "80" },
                new AnalysisRunGateViolationDto { PolicyKey = "system.quality.new-medium.code", Outcome = "Block", TargetKind = "finding", TargetKey = "no-control-regex:dfae5377", ObservedValue = "Medium", Operator = "GreaterThanOrEqual", Threshold = "Medium" }
            ]
        };
        ServeFindings(gate);

        var cut = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));

        var labels = cut.FindAll(".analysis-run-gate-metrics .omni-stat-tile__label").Select(label => label.TextContent.Trim()).ToList();
        Assert.Equal(["AnalysisGateFindings", "AnalysisGateNewFindings"], labels);
        Assert.Empty(cut.FindAll(".analysis-run-gate-violation"));
        Assert.DoesNotContain("quality.coverage.line", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("coverage.line.percent is below its threshold", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R2_052_AGradeWithoutFindings_SaysWhichMeasureMadeIt()
    {
        // Run 2491: a B over "0 findings". The B came from a metric measure, now listed with its value.
        var gate = new AnalysisRunGateDto
        {
            PipelineRunId = 2491,
            Grade = new AnalysisGradeSummaryDto
            {
                OverallGrade = AnalysisGrade.B,
                Completeness = AnalysisGradeCompleteness.Complete,
                LimitingDomain = AnalysisGradeDomain.CodeQuality,
                Domains =
                [
                    new AnalysisGradeDomainDto
                    {
                        Domain = AnalysisGradeDomain.CodeQuality,
                        Grade = AnalysisGrade.B,
                        Required = true,
                        Completeness = AnalysisGradeCompleteness.Complete,
                        Measures = [new AnalysisGradeMeasureDto { Key = "grade.coverage.line", Grade = AnalysisGrade.B, ObservedValue = 72.4, Unit = "percent" }]
                    }
                ]
            }
        };
        ServeFindings(gate);

        var cut = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));

        // No "limiting domain" sentence; the domain that sets the B has a bold title.
        Assert.DoesNotContain("AnalysisLimitingDomain", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("AnalysisCodeQualityAndTests", cut.Find(".analysis-grade-domain-limiting").TextContent.Trim());
        var measure = Assert.Single(cut.FindAll("[data-testid='grade-row-measure']"));
        Assert.Contains("AnalysisTestedLines", measure.TextContent, StringComparison.Ordinal);
        Assert.Contains("72.4 %", measure.TextContent.Replace(',', '.'), StringComparison.Ordinal);
        Assert.Contains("grade-badge--b", measure.InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void R527_DecidedFindings_AreHiddenUntilAskedFor()
    {
        var gate = new AnalysisRunGateDto
        {
            PipelineRunId = 2472,
            FindingCount = 1,
            DecidedFindingCount = 1,
            Findings =
            [
                new AnalysisRunGateFindingDto { FindingId = 1094, Title = "Open finding", Status = AnalysisFindingStatus.Open },
                new AnalysisRunGateFindingDto { FindingId = 870, Title = "CSP accepted", Status = AnalysisFindingStatus.Accepted }
            ]
        };
        ServeFindings(gate);
        var cut = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));

        Assert.Contains("Open finding", cut.Markup);
        Assert.DoesNotContain("CSP accepted", cut.Markup);
        // The identifier the finding is quoted by.
        Assert.Contains("#1094", cut.Markup);

        cut.FindAll("button.omni-switch").Single(toggle => toggle.TextContent.Contains("AnalysisGateShowDecided", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("CSP accepted", cut.Markup));
        Assert.Contains("#870", cut.Markup);
    }

    [Fact]
    public void R502_TheRunsFindings_UseTheOneFindingsList_AndARowOpensItsFinding()
    {
        var gate = new AnalysisRunGateDto
        {
            PipelineRunId = 2472,
            FindingCount = 1,
            Findings =
            [
                new AnalysisRunGateFindingDto
                {
                    FindingId = 1094, RuleId = "F821", Title = "Undefined name", Message = "folder is not defined",
                    Severity = AnalysisSeverity.Medium, Status = AnalysisFindingStatus.Open,
                    FilePath = "tests/security-rules/aetheus-security.py", StartLine = 12, IsNew = true
                }
            ]
        };

        ServeFindings(gate);
        var cut = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));

        // The shared columns: identifier, title over its message, location, "new", the open button.
        var columns = cut.FindComponent<AnalysisFindingColumns>();
        // Read from the server page by page, as the project's Quality list.
        Assert.False(columns.Instance.LocalData);
        Assert.Contains("#1094", cut.Markup);
        // One line per cell, the message behind the title on hover.
        Assert.Contains("folder is not defined", cut.Find(".analysis-finding-grid-title").GetAttribute("title"));
        Assert.Contains("tests/security-rules/aetheus-security.py", cut.Find(".analysis-finding-location").TextContent);
        Assert.Contains("AnalysisGateNewBadge", cut.Find("tbody").TextContent);

        cut.Find("button[title='AnalysisGateOpenFinding']").Click();

        // The finding is opened as this run saw it.
        Assert.EndsWith("/analysis/findings/1094?run=2472", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void R502_NoOtherPage_DefinesItsOwnFindingColumns()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var owners = Directory.EnumerateFiles(front, "*.razor", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file =>
            {
                var markup = File.ReadAllText(file);
                return markup.Contains("TItem=\"AnalysisFindingDto\" Property=", StringComparison.Ordinal)
                    || markup.Contains("TItem=\"AnalysisRunGateFindingDto\"", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["AnalysisFindingColumns.razor"], owners);
    }

    [Fact]
    public void Actions_OpenProjectQuality_AndExportEveryGateFinding()
    {
        var gate = new AnalysisRunGateDto
        {
            PipelineRunId = 1586,
            FindingCount = 2,
            Findings =
            [
                new AnalysisRunGateFindingDto
                {
                    FindingId = 10,
                    RuleId = "rule-one",
                    Title = "First finding",
                    Message = "First evidence",
                    Category = AnalysisCategory.Sast,
                    Severity = AnalysisSeverity.High,
                    Status = AnalysisFindingStatus.Open,
                    FilePath = "src/First.cs",
                    StartLine = 12
                },
                new AnalysisRunGateFindingDto
                {
                    FindingId = 11,
                    RuleId = "rule-two",
                    Title = "Second finding",
                    Message = "Second evidence",
                    Category = AnalysisCategory.CodeQuality,
                    Severity = AnalysisSeverity.Low,
                    Status = AnalysisFindingStatus.Open,
                    FilePath = "src/Second.cs",
                    StartLine = 24
                }
            ]
        };
        ServeFindings(gate);
        var cut = Render<PipelineRunGateFindings>(parameters => parameters
            .Add(component => component.Gate, gate)
            .Add(component => component.ProjectId, 7)
            .Add(component => component.ProjectName, "Aetheus"));

        // Gate tiles show findings counts and display rule alerts only when their detail list
        // exists.
        var tiles = cut.FindAll(".analysis-run-gate-metrics > .omni-stat-tile");
        Assert.Equal(2, tiles.Count);
        Assert.Equal(new[] { "2", "0" }, tiles.Select(tile => tile.QuerySelector(".omni-stat-tile__value")!.TextContent.Trim()));
        Assert.Equal(
            new[] { "AnalysisGateFindings", "AnalysisGateNewFindings" },
            tiles.Select(tile => tile.QuerySelector(".omni-stat-tile__label")!.TextContent.Trim()));
        Assert.Contains("aetheus-grid-fullheight", cut.Find(".analysis-run-findings-grid").ClassList);

        // Export opens a preview containing the complete prompt before download.
        IDictionary<string, object?>? opened = null;
        Type? openedType = null;
        Services.GetRequiredService<OmniDialogService>().OnOpen += (_, type, parameters, _) =>
        {
            openedType = type;
            opened = parameters;
        };
        // STD-BTN detection 11: beside Open project quality, Export sits in the "More actions" menu.
        cut.FindMenuItem("AnalysisExport")!.Click();

        Assert.Equal(typeof(TextExportDialog), openedType);
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "downloadFile");
        var markdown = Assert.IsType<string>(opened![nameof(TextExportDialog.Text)]);
        Assert.Contains("First finding", markdown);
        Assert.Contains("Second finding", markdown);
        Assert.Contains("First evidence", markdown);
        Assert.Contains("Second evidence", markdown);
        // The project's name, the run, what the file holds, then the date.
        Assert.Matches(@"^aetheus-run-1586-findings-\d{4}-\d{2}-\d{2}\.md$", Assert.IsType<string>(opened[nameof(TextExportDialog.FileName)]));

        cut.FindAll("button").Single(button =>
            button.TextContent.Contains("AnalysisOpenProjectQuality", StringComparison.Ordinal)).Click();

        Assert.EndsWith(
            "/projects/7/quality",
            Services.GetRequiredService<NavigationManager>().Uri,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AnalysisGateStatus.Warning, true, false)]
    [InlineData(AnalysisGateStatus.Passed, true, false)]
    [InlineData(AnalysisGateStatus.Blocked, true, true)]
    [InlineData(AnalysisGateStatus.Error, true, true)]
    [InlineData(AnalysisGateStatus.Warning, false, true)]
    public void GateStatusWord_IsDrawnOnlyWhenTheLetterCannotCarryIt(
        AnalysisGateStatus status, bool graded, bool wordExpected)
    {
        var gate = new AnalysisRunGateDto
        {
            PipelineRunId = 1,
            Status = status,
            Grade = graded ? new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.A } : null
        };
        var statusKey = $"AnalysisGateStatus{status}";

        var panel = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));
        var tile = Render<PipelineRunGateOverview>(parameters => parameters.Add(component => component.Gate, gate));

        // A green "A" alone on a blocked or failed gate would read as a pass: the word must stay.
        Assert.Equal(wordExpected, panel.Markup.Contains(statusKey, StringComparison.Ordinal));
        Assert.Equal(wordExpected, tile.Markup.Contains(statusKey, StringComparison.Ordinal));
    }

    /// <summary>The tab lists the findings of the run and the runs it triggered, read from the
    /// server one page at a time; its figures are the gate's (the whole tree), not the
    /// page's.</summary>
    [Fact]
    public void R485_TheGateTab_ReadsOnePageOfTheTreesFindings_FromTheServer()
    {
        var gate = new AnalysisRunGateDto { PipelineRunId = 40, FindingCount = 120, NewFindingCount = 7 };
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings", new AnalysisRunFindingsPageDto
        {
            Items = [new AnalysisRunGateFindingDto { FindingId = 501, Title = "First of 120", Status = AnalysisFindingStatus.Open }],
            TotalCount = 120,
            Page = 1,
            PageSize = 25,
            OpenCount = 120,
            NewOpenCount = 7
        });

        var cut = Render<PipelineRunGateFindings>(parameters => parameters
            .Add(component => component.Gate, gate)
            .Add(component => component.RunIds, [40, 41]));

        cut.WaitForAssertion(() => Assert.Contains("First of 120", cut.Markup));
        var request = Assert.Single(_handler.Requests, item => item.Url.Contains("api/analysis/runs/findings", StringComparison.Ordinal)).Url;
        Assert.Contains("runIds=40", request, StringComparison.Ordinal);
        Assert.Contains("runIds=41", request, StringComparison.Ordinal);
        Assert.DoesNotContain("includeDecided", request, StringComparison.Ordinal);
        Assert.Equal("120", cut.FindAll(".analysis-run-gate-metrics > .omni-stat-tile")[0].QuerySelector(".omni-stat-tile__value")!.TextContent.Trim());
    }

    /// <summary>The decided-findings switch requests those findings from the server.</summary>
    [Fact]
    public void R485_ShowingTheDecidedFindings_AsksTheServerForThem()
    {
        var gate = new AnalysisRunGateDto
        {
            PipelineRunId = 40,
            FindingCount = 1,
            DecidedFindingCount = 1,
            Findings =
            [
                new AnalysisRunGateFindingDto { FindingId = 1, Title = "Still open", Status = AnalysisFindingStatus.Open },
                new AnalysisRunGateFindingDto { FindingId = 2, Title = "Ruled out", Status = AnalysisFindingStatus.FalsePositive }
            ]
        };
        ServeFindings(gate);
        var cut = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));
        cut.WaitForAssertion(() => Assert.Contains("Still open", cut.Markup));
        Assert.DoesNotContain("Ruled out", cut.Markup);

        cut.FindAll("button.omni-switch").Single(toggle => toggle.TextContent.Contains("AnalysisGateShowDecided", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("Ruled out", cut.Markup));
        Assert.Contains(_handler.Requests, item => item.Url.Contains("includeDecided=true", StringComparison.Ordinal));
    }

    /// <summary>The export holds every listed finding, read page after page, not only the page on
    /// screen.</summary>
    [Fact]
    public void R485_TheExport_ReadsEveryListedPage()
    {
        static AnalysisRunGateFindingDto Finding(int id) => new()
        {
            FindingId = id,
            RuleId = $"rule-{id}",
            Title = $"Finding {id}",
            Message = "evidence",
            Status = AnalysisFindingStatus.Open
        };
        var gate = new AnalysisRunGateDto { PipelineRunId = 40, FindingCount = 201 };
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings", new AnalysisRunFindingsPageDto
        {
            Items = [Finding(1)],
            TotalCount = 201,
            OpenCount = 201
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings?page=1&pageSize=200", new AnalysisRunFindingsPageDto
        {
            Items = [.. Enumerable.Range(1, 200).Select(Finding)],
            TotalCount = 201,
            OpenCount = 201
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings?page=2&pageSize=200", new AnalysisRunFindingsPageDto
        {
            Items = [Finding(201)],
            TotalCount = 201,
            OpenCount = 201
        });
        IDictionary<string, object?>? opened = null;
        Services.GetRequiredService<OmniDialogService>().OnOpen += (_, _, parameters, _) => opened = parameters;
        var cut = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));
        cut.WaitForAssertion(() => Assert.Contains("Finding 1", cut.Markup));

        cut.FindAll("button").Single(button => button.TextContent.Contains("AnalysisExport", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.NotNull(opened));
        var markdown = Assert.IsType<string>(opened![nameof(TextExportDialog.Text)]);
        Assert.Contains("Finding 200", markdown, StringComparison.Ordinal);
        Assert.Contains("Finding 201", markdown, StringComparison.Ordinal);
    }

    /// <summary>The ID column's filter is applied by the server: its bounds travel with the page request.</summary>
    [Fact]
    public async Task TheIdColumnFilter_IsSentToTheServer()
    {
        var gate = new AnalysisRunGateDto { PipelineRunId = 40, FindingCount = 3 };
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings", new AnalysisRunFindingsPageDto { OpenCount = 3 });
        var cut = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));
        var load = typeof(PipelineRunGateFindings).GetMethod("LoadAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        await cut.InvokeAsync(() => (Task)load.Invoke(cut.Instance, [new GridLoadArgs
        {
            Skip = 0, Top = 25,
            Filters = [new GridFilterDescriptor("Id", "100", OmniDataGridFilterOperator.GreaterThanOrEquals, OmniDataGridFilterOperator.NotEquals, "105")]
        }])!);

        var request = _handler.Requests.Last(item => item.Url.Contains("api/analysis/runs/findings", StringComparison.Ordinal)).Url;
        Assert.Contains("idFrom=100", request, StringComparison.Ordinal);
        Assert.Contains("idNot=105", request, StringComparison.Ordinal);
        Assert.DoesNotContain("idTo=", request, StringComparison.Ordinal);
    }

    [Fact]
    public void FindingRowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        // The columns come from AnalysisFindingColumns, shared with the project quality page.
        var gate = new AnalysisRunGateDto
        {
            PipelineRunId = 2472,
            FindingCount = 1,
            Findings = [new AnalysisRunGateFindingDto { FindingId = 1094, Title = "Open finding", Status = AnalysisFindingStatus.Open }]
        };
        ServeFindings(gate);

        var cut = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        // The identifier link, the title link and the open button.
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 3);
    }

    [Fact]
    public void R1_38_TheGateGrade_IsTheFirstRowOfTheSingleGradeBlock_ThenCodeAndTests_Security_Architecture()
    {
        var gate = new AnalysisRunGateDto
        {
            PipelineRunId = 2819,
            Grade = new AnalysisGradeSummaryDto
            {
                OverallGrade = AnalysisGrade.B,
                Completeness = AnalysisGradeCompleteness.Complete,
                LimitingDomain = AnalysisGradeDomain.CodeQuality,
                Domains =
                [
                    new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Security, Grade = AnalysisGrade.A, Completeness = AnalysisGradeCompleteness.Complete },
                    new AnalysisGradeDomainDto
                    {
                        Domain = AnalysisGradeDomain.CodeQuality, Grade = AnalysisGrade.B, Required = true, Completeness = AnalysisGradeCompleteness.Complete,
                        Measures =
                        [
                            new AnalysisGradeMeasureDto { Key = "grade.coverage.line", Grade = AnalysisGrade.B, ObservedValue = 88.64, Unit = "percent" },
                            new AnalysisGradeMeasureDto { Key = "grade.complexity.maximum", Grade = AnalysisGrade.A, ObservedValue = 12 }
                        ]
                    },
                    new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Architecture, Grade = AnalysisGrade.A, Completeness = AnalysisGradeCompleteness.Complete }
                ]
            }
        };
        ServeFindings(gate);

        var cut = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));

        // One block, no separate wide frame for the grade.
        Assert.Single(cut.FindAll("[data-testid='analysis-grade-block']"));
        Assert.Empty(cut.FindAll(".analysis-run-gate-headline"));
        var rows = cut.FindAll("[data-testid='analysis-grade-block'] .analysis-grade-measure-label").Select(label => label.TextContent.Trim()).ToList();
        Assert.Equal(
            ["GateGrade", "AnalysisCodeQualityAndTests", "AnalysisTestedLines", "AnalysisMaximumComplexity", "AnalysisApplicationSecurity", "AnalysisCodeArchitecture"],
            rows);
        // Coverage carries its percent; the first row names the measure that sets the B.
        var coverage = cut.Find("[data-testid='grade-row-measure']");
        Assert.Contains("88.64 %", coverage.TextContent.Replace(',', '.'), StringComparison.Ordinal);
        Assert.Contains("AnalysisTestedLines", cut.Find("[data-testid='grade-row-gate']").TextContent, StringComparison.Ordinal);
        // R1-37: the cause reads as a muted second line under the label, not in the value column.
        var cause = cut.Find("[data-testid='grade-row-gate'] .analysis-grade-row-title [data-testid='grade-row-gate-cause']");
        Assert.Contains("GateGradeSetBy", cause.TextContent, StringComparison.Ordinal);
        Assert.Contains("AnalysisTestedLines", cause.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.Find("[data-testid='grade-row-gate'] .analysis-grade-value").TextContent.Trim());
    }

    [Fact]
    public void R1_37_TheGateTab_PutsTheGradeBlockBesideTheTwoTiles_ThenTheFindingsSectionWithItsCountAndSwitch()
    {
        var gate = new AnalysisRunGateDto
        {
            PipelineRunId = 2814,
            FindingCount = 1,
            DecidedFindingCount = 1,
            Grade = new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.A, Completeness = AnalysisGradeCompleteness.Complete },
            Findings =
            [
                new AnalysisRunGateFindingDto { FindingId = 1, Title = "Open finding", Status = AnalysisFindingStatus.Open },
                new AnalysisRunGateFindingDto { FindingId = 2, Title = "Decided finding", Status = AnalysisFindingStatus.Accepted }
            ]
        };
        ServeFindings(gate);

        var cut = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));

        // The grade block and the tile column share one layout row; the tiles are OE tiles in an OmniTileGrid.
        var layout = cut.Find(".analysis-run-gate-layout");
        Assert.NotNull(layout.QuerySelector("[data-testid='analysis-grade-block']"));
        Assert.Equal(2, layout.QuerySelectorAll(".analysis-run-gate-metrics > .omni-stat-tile").Length);
        Assert.NotNull(layout.QuerySelector(".omni-tile-grid.analysis-run-gate-metrics"));
        // The findings section: its title, the count of the list, and the decided switch on the same line.
        var head = cut.Find("[data-testid='gate-findings-head']");
        Assert.Contains("AnalysisGateFindings", head.QuerySelector("h6")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("1", head.QuerySelector("[data-testid='gate-findings-count']")!.TextContent.Trim());
        Assert.Contains("AnalysisGateShowDecided", head.QuerySelector("button.omni-switch")!.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".analysis-run-gate-empty"));

        head.QuerySelector("button.omni-switch")!.Click();

        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find("[data-testid='gate-findings-count']").TextContent.Trim()));
    }

    [Fact]
    public void R1_37_WithoutFindings_TheSectionShowsTheEmptyStateInItsDashedFrame_AndNoSwitch()
    {
        var gate = new AnalysisRunGateDto { PipelineRunId = 2815 };
        ServeFindings(gate);

        var cut = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));

        Assert.Equal("0", cut.Find("[data-testid='gate-findings-count']").TextContent.Trim());
        Assert.Contains("AnalysisNoFindings", cut.Find(".analysis-run-gate-empty").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='gate-findings-head'] button.omni-switch"));
    }

    [Fact]
    public void R1_37_WithoutAGrade_TheTwoTilesSitInARowAtTileSize_NotAStretchedColumn()
    {
        var gate = new AnalysisRunGateDto { PipelineRunId = 2815 };
        ServeFindings(gate);

        var cut = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));

        Assert.Empty(cut.FindAll("[data-testid='analysis-grade-block']"));
        Assert.NotNull(cut.Find(".analysis-run-gate-layout.analysis-run-gate-layout--figures-only"));
        var tiles = cut.Find(".analysis-run-gate-metrics");
        Assert.Contains("omni-tile-grid--columns-4", tiles.ClassName, StringComparison.Ordinal);
        Assert.Equal(2, tiles.QuerySelectorAll(".omni-stat-tile").Length);
    }

    // Recette R-74, run #2920 of w6-candidate: 5 findings in the tile, 2 on the security line, 3 of architecture. Every
    // domain counts in the grade (owner decision of 2026-10-25); the tile splits its total by domain so it adds up with
    // the lines under it.
    private static AnalysisRunGateDto GateWithFindingsInTwoDomains() => new()
    {
        PipelineRunId = 2920,
        FindingCount = 5,
        OpenFindingCounts =
        [
            new AnalysisRunGateCategoryCountDto { Category = AnalysisCategory.Architecture, Count = 3 },
            new AnalysisRunGateCategoryCountDto { Category = AnalysisCategory.Sast, Count = 1 },
            new AnalysisRunGateCategoryCountDto { Category = AnalysisCategory.Secrets, Count = 1 }
        ],
        Grade = new AnalysisGradeSummaryDto
        {
            OverallGrade = AnalysisGrade.B,
            Completeness = AnalysisGradeCompleteness.Complete,
            Domains =
            [
                new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Security, Grade = AnalysisGrade.B, Required = true, Completeness = AnalysisGradeCompleteness.Complete },
                new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Architecture, Grade = AnalysisGrade.B, Required = true, Completeness = AnalysisGradeCompleteness.Complete }
            ]
        }
    };

    [Fact]
    public void R74_TheOpenFindingsOfTheGate_AreCountedPerDomain_AndAddUpToTheTotal()
    {
        var gate = GateWithFindingsInTwoDomains();

        var byDomain = PipelineRunGatePresentation.OpenFindingsByDomain(gate);

        Assert.Equal([(AnalysisGradeDomain.Security, 2), (AnalysisGradeDomain.Architecture, 3)], byDomain);
        Assert.Equal(gate.FindingCount, byDomain.Sum(item => item.Count));
        Assert.Empty(PipelineRunGatePresentation.OpenFindingsByDomain(new AnalysisRunGateDto { PipelineRunId = 1, FindingCount = 0 }));
    }

    [Fact]
    public void R74_TheFindingsTile_SplitsItsTotalByDomain_WhateverTheRequiredDomains()
    {
        var gate = GateWithFindingsInTwoDomains();
        ServeFindings(gate);

        var cut = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));

        var tile = cut.FindAll(".analysis-run-gate-metrics > .omni-stat-tile")[0];
        // The test localizer returns each key as its text: "of which 2 in <security>, 3 in <architecture>".
        Assert.Contains("GateFindingsByDomain 2 GateFindingsInDomain AnalysisApplicationSecurity", tile.TextContent, StringComparison.Ordinal);
        Assert.Contains("3 GateFindingsInDomain AnalysisCodeArchitecture", tile.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("GateFindingsNotCounted", tile.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R74_TheFindingsTile_HasNoSplit_WhenTheGateHoldsNoOpenFinding()
    {
        var gate = new AnalysisRunGateDto { PipelineRunId = 2921 };
        ServeFindings(gate);

        var cut = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));

        var tile = cut.FindAll(".analysis-run-gate-metrics > .omni-stat-tile")[0];
        Assert.DoesNotContain("GateFindingsByDomain", tile.TextContent, StringComparison.Ordinal);
    }
}
