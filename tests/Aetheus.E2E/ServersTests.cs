// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Playwright;

namespace Aetheus.E2E;

[Category("E2E")]
[Category("Servers")]
public class ServersTests : E2ETestBase
{
    protected override bool ShareBrowserContextAcrossTests => true;

    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
    }

    [Test]
    [RollbackRelevant]
    public async Task Servers_PageRendersExpectedControlsAndColumns()
    {
        await NavigateToAsync("servers");
        await Expect(Page.GetByText("Servers").First).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page).ToHaveTitleAsync(new System.Text.RegularExpressions.Regex("Servers"));
        await WaitForNoSpinnerAsync();
        await WaitForDataGridAsync();

        var grid = Page.Locator(".server-list-grid");
        await Expect(grid).ToBeVisibleAsync(new() { Timeout = 10000 });
        // No filter bar above the list; each column filters from its header.
        await Expect(Page.Locator(".server-list-search")).ToHaveCountAsync(0);
        // The overflow menu "Tools" carries Update all agents, Port check and Retired servers.
        var toolsMenu = Page.Locator("button[title='Tools']");
        await Expect(toolsMenu).ToBeVisibleAsync(new() { Timeout = 10000 });

        // The data grid renders one `.omni-data-grid__title` per column header.
        var headers = Page.Locator(".omni-data-grid__title");
        await Expect(headers.First).ToBeVisibleAsync(new() { Timeout = 10000 });
        var count = await headers.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(5), "Servers grid should have at least 5 columns");

        // A healthy fleet shows no counter in the header (only what needs attention), so no "n total" badge.

        // The Name header filter reaches the API as a column filter and narrows the rows.
        var nameHeader = grid.Locator("th[data-omni-col='Name']");
        await nameHeader.Locator(".omni-data-grid__filter-menu-toggle").ClickAsync();
        var nameResponse = Page.WaitForResponseAsync(response =>
            response.Request.Method == "GET"
            && response.Url.Contains("/api/servers?", StringComparison.OrdinalIgnoreCase)
            && Uri.UnescapeDataString(response.Url).Contains("Filters[0].Field=Name", StringComparison.Ordinal));
        await nameHeader.Locator("input.omni-data-grid__filter").First.FillAsync("web-01");
        await Expect(nameHeader.Locator(".omni-data-grid__filter-apply")).ToHaveCountAsync(0);
        var filteredResponse = await nameResponse.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(filteredResponse.Ok, Is.True, $"Server column filter returned HTTP {filteredResponse.Status}.");
        await Expect(grid.Locator("a.server-name-link").Filter(new() { HasText = "web-01" }))
            .ToBeVisibleAsync(new() { Timeout = 5000 });
        await Expect(grid.Locator("a.server-name-link").Filter(new() { HasText = "build-01" }))
            .ToHaveCountAsync(0);

        // The reset cross beside the header lifts the filter.
        await nameHeader.Locator(".omni-data-grid__filter-reset").ClickAsync();
        await Expect(grid.Locator("a.server-name-link").Filter(new() { HasText = "build-01" }))
            .ToBeVisibleAsync(new() { Timeout = 5000 });

        // The Type column is a checkable list of the server types.
        var typeHeader = grid.Locator("th[data-omni-col='Type']");
        await typeHeader.Locator(".omni-data-grid__filter-menu-toggle").ClickAsync();
        await typeHeader.Locator(".omni-multi-select__option", new() { HasText = "Build" })
            .Locator("input.omni-multi-select__checkbox").CheckAsync();
        await Expect(grid.Locator("a.server-name-link").Filter(new() { HasText = "build-01" }))
            .ToBeVisibleAsync(new() { Timeout = 5000 });
        await Expect(grid.Locator("a.server-name-link").Filter(new() { HasText = "web-01" }))
            .ToHaveCountAsync(0);

        // The server hub refreshes the list; Tools has no manual Refresh action.
        await toolsMenu.ClickAsync();
        await Expect(Page.GetByText("Refresh", new() { Exact = true })).ToHaveCountAsync(0);
    }
}
