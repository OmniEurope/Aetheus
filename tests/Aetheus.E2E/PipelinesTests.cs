// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Playwright;

namespace Aetheus.E2E;

[Category("E2E")]
[Category("Pipelines")]
public class PipelinesTests : E2ETestBase
{
    protected override bool ShareBrowserContextAcrossTests => true;

    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
    }

    [Test]
    public async Task Pipelines_PageRendersExpectedControlsAndColumns()
    {
        await NavigateToAsync("pipelines");
        await Expect(Page.GetByText("Pipelines").First).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page).ToHaveTitleAsync(new System.Text.RegularExpressions.Regex("Pipelines"));

        // The list's New button sits in the one-line page header. Scoped to the header because the
        // empty state also renders a "New Pipeline" CTA when the grid is empty.
        var newButton = Page.Locator(".omni-page-header__actions").GetByRole(AriaRole.Button, new() { Name = "New Pipeline" });
        await Expect(newButton).ToBeVisibleAsync(new() { Timeout = 10000 });

        await WaitForNoSpinnerAsync();
        // Two views, Catalog (n) and Runs (n), switched from the page header; parents and leaves
        // share one grid.
        var views = Page.Locator(".omni-page-header__actions .pipeline-view-switch");
        await Expect(views.Locator(".omni-select-bar__item", new() { HasTextRegex = new(@"Catalog \(\d+\)") }))
            .ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(views.Locator(".omni-select-bar__item", new() { HasTextRegex = new(@"Runs \(\d+\)") }))
            .ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page.GetByRole(AriaRole.Tab, new() { NameRegex = new(@"Catalog \(\d+\)") })).ToHaveCountAsync(0);

        var grids = Page.Locator(".pipeline-dependency-grid");
        await Expect(grids).ToHaveCountAsync(1, new() { Timeout = 10000 });
        var grid = grids.First;
        await Expect(grid).ToBeVisibleAsync(new() { Timeout = 10000 });

        var searchBox = Page.Locator(".pipeline-list-search");
        await Expect(searchBox).ToBeVisibleAsync();
        // Triggers filter through a compact multi-select.
        var triggerFilter = Page.GetByRole(AriaRole.Group, new() { Name = "All Triggers" });
        await Expect(triggerFilter).ToBeVisibleAsync(new() { Timeout = 10000 });
        // No Refresh button, the list follows the pipelines and entities hubs.
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Refresh", Exact = true })).ToHaveCountAsync(0);

        await Expect(grid.Locator(".pipeline-name-link").First).ToBeVisibleAsync(new() { Timeout = 10000 });

        // The split view keeps the metadata columns in both grids and exposes dependency details
        // through the expandable parent row instead of the former flat "Referenced pipelines" column.
        await Expect(grid.GetByText("Project", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(grid.GetByText("Pipeline", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(grid.GetByText("Trigger", new() { Exact = true })).ToBeVisibleAsync();

        // OE 1.2.0 names the row toggle with its own localized "Expand row" (GridExpandRow).
        await grid.GetByRole(AriaRole.Button, new() { Name = "Expand row", Exact = true }).First.ClickAsync();
        await Expect(grid.Locator(".pipeline-relation-list-execution")).ToBeVisibleAsync();
        await Expect(grid.Locator(".pipeline-relation-card").First).ToBeVisibleAsync();

        // The catalogue tab carries the pipeline count; the summary strip supplies the remaining
        // figures.
        await Expect(Page.Locator(".pipeline-status-summary .pipeline-status-chip").First).ToBeVisibleAsync(new() { Timeout = 10000 });

        // Search is a real client-side filter over the loaded dependency graph.
        await searchBox.FillAsync("toto-ci");
        await searchBox.PressAsync("Tab");
        await Expect(Page.GetByText("toto-ci", new() { Exact = true }).First)
            .ToBeVisibleAsync(new() { Timeout = 5000 });
        await Expect(grids.GetByText("API CI", new() { Exact = true })).ToHaveCountAsync(0);

        // Scope Clear to the filter row because dropdowns also expose buttons with that accessible
        // name.
        var clearFilters = Page.Locator(".pipeline-catalog-filters")
            .GetByRole(AriaRole.Button, new() { Name = "Clear", Exact = true });
        await clearFilters.ClickAsync();
        await Expect(grids.GetByText("API CI", new() { Exact = true }).First)
            .ToBeVisibleAsync(new() { Timeout = 5000 });

        // Select the seeded Webhook trigger and prove that manual pipelines disappear.
        await triggerFilter.Locator("summary").ClickAsync();
        await triggerFilter.GetByRole(AriaRole.Checkbox, new() { Name = "Webhook" }).CheckAsync();
        await Expect(Page.GetByText("toto-ci", new() { Exact = true }).First)
            .ToBeVisibleAsync(new() { Timeout = 5000 });
        await Expect(grids.GetByText("API CI", new() { Exact = true })).ToHaveCountAsync(0);

        await clearFilters.ClickAsync();
        await Expect(grids.GetByText("API CI", new() { Exact = true }).First)
            .ToBeVisibleAsync(new() { Timeout = 5000 });
    }

    [Test]
    public async Task Pipelines_NewPipeline_NavigatesToSetupWizard()
    {
        await NavigateToAsync("pipelines");
        await Page.Locator(".omni-page-header__actions").GetByRole(AriaRole.Button, new() { Name = "New Pipeline" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(
            new System.Text.RegularExpressions.Regex("/pipelines/setup"), new() { Timeout = 5000 });
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Pipeline setup wizard" }))
            .ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "1 Project" }))
            .ToBeVisibleAsync(new() { Timeout = 5000 });
        await Expect(Page.GetByText("Select the project that will own the generated pipelines."))
            .ToBeVisibleAsync(new() { Timeout = 5000 });
    }

    [Test]
    public async Task PipelineRun_WhenGateEvidenceReturns500_ShowsIncompleteWarningWithoutGrade()
    {
        AllowBrowserDiagnostic(new System.Text.RegularExpressions.Regex(
            "Failed to load resource.*500", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        AllowBrowserDiagnostic(new System.Text.RegularExpressions.Regex(
            "Polly.*Result: '500'",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Singleline));
        await Page.RouteAsync("**/api/analysis/runs/*/result", route => route.FulfillAsync(new()
        {
            Status = 500,
            ContentType = "application/json",
            Body = "{}"
        }));

        await NavigateToAsync("pipelines/runs/1");

        // Assert the incomplete gate separately in the header badge and overview alert.
        await Expect(Page.Locator(".pipeline-run-page-header").GetByText("Analysis gate incomplete", new() { Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page.Locator(".omni-alert").GetByText("Analysis gate incomplete", new() { Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page.GetByText(
                new System.Text.RegularExpressions.Regex(
                    "The displayed gate is incomplete and must not be treated as passed")))
            .ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page.Locator(".analysis-run-gate-grade")).ToHaveCountAsync(0);
    }
}
