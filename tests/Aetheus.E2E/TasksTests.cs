// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.E2E;

[Category("E2E")]
[Category("Tasks")]
[RollbackRelevant]
public class TasksTests : E2ETestBase
{
    protected override bool ShareBrowserContextAcrossTests => true;

    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
    }

    [Test]
    public async Task Tasks_PageRendersExpectedGridAndColumns()
    {
        await NavigateToAsync("tasks");
        await Expect(Page.GetByText("Tasks").First).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page).ToHaveTitleAsync(new System.Text.RegularExpressions.Regex("Tasks"));
        await WaitForNoSpinnerAsync();
        await WaitForDataGridAsync();

        var grid = Page.Locator(".omni-data-grid");
        await Expect(grid).ToBeVisibleAsync(new() { Timeout = 10000 });

        // The data grid renders one `.omni-data-grid__title` per column header.
        var headers = Page.Locator(".omni-data-grid__title");
        await Expect(headers.First).ToBeVisibleAsync(new() { Timeout = 10000 });
        var count = await headers.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(6), "Tasks grid should have at least 6 columns");

        var webDiagnostic = Page.Locator(".omni-data-grid__row")
            .Filter(new() { HasText = "Demo web diagnostic" });
        await Expect(webDiagnostic).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(webDiagnostic.GetByText("Cancelled", new() { Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = 5000 });

        var buildDiagnostic = Page.Locator(".omni-data-grid__row")
            .Filter(new() { HasText = "Demo build diagnostic" });
        await Expect(buildDiagnostic).ToBeVisibleAsync(new() { Timeout = 5000 });
        await Expect(buildDiagnostic.GetByText("Cancelled", new() { Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = 5000 });
    }
}
