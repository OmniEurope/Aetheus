// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.E2E;

[Category("E2E")]
[Category("Dashboard")]
public class DashboardTests : E2ETestBase
{
    protected override bool ShareBrowserContextAcrossTests => true;

    [Test]
    public async Task InitialLoading_KeepsAllFourGridHeadersUnderOeLoadingBar()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pattern = new System.Text.RegularExpressions.Regex(@"/api/(monitoring/dashboard|appmonitoring/summary)(\?|$)");
        await Page.RouteAsync(pattern, async route =>
        {
            await release.Task;
            await route.ContinueAsync();
        });
        try
        {
            await Page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            // Every tile shows OE's loading bar under its headers, on the table only, no veil.
            var bars = Page.Locator(".dashboard-tile thead > tr.omni-data-grid__progress .omni-loading-bar--active");
            // The reload cold-boots the WASM frontend, which on a loaded QA agent exceeds the default
            // 5s expect budget (nightly 2697: 0 bars after 5s). The dashboard calls stay held above,
            // so the bars cannot clear while we wait: the boot budget only removes the timing guess.
            await Expect(bars).ToHaveCountAsync(4, new() { Timeout = PlaywrightConfig.AppReadyTimeoutMs });
            for (var index = 0; index < 4; index++)
            {
                var grid = bars.Nth(index).Locator("xpath=ancestor::table[1]");
                // The data column headers, not OE's control cells: the runs grid collapses its
                // expand column while no row can expand, so that cell is hidden by design.
                await Expect(grid.Locator("thead th:not([data-omni-control])").First).ToBeVisibleAsync();
            }
            await Expect(Page.Locator(".dashboard-tile .omni-logo-loader")).ToHaveCountAsync(0);
        }
        finally
        {
            release.TrySetResult();
        }
        await WaitForNoSpinnerAsync();
        await Expect(Page.Locator(".dashboard-tile .omni-data-grid__progress")).ToHaveCountAsync(0);
    }

    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
        await NavigateToAsync("");
    }

    [Test]
    public async Task Dashboard_RendersExpectedContentAndNavigation()
    {
        await Expect(Page.GetByText("Dashboard").First).ToBeVisibleAsync(new() { Timeout = 5000 });
        await WaitForNoSpinnerAsync();

        var cards = Page.Locator(".omni-card");
        await Expect(cards.First).ToBeVisibleAsync(new() { Timeout = 10000 });
        var cardCount = await cards.CountAsync();
        Assert.That(cardCount, Is.GreaterThanOrEqualTo(4), "Dashboard should show its 4 tiles");

        var serversCard = Page.Locator(".omni-card").Filter(new() { HasText = "Servers" });
        await Expect(serversCard.First).ToBeVisibleAsync(new() { Timeout = 10000 });

        var runsCard = Page.Locator(".omni-card").Filter(new() { HasText = "Recent Pipeline Runs" });
        await Expect(runsCard.First).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page).ToHaveTitleAsync(new System.Text.RegularExpressions.Regex("Dashboard"));

        var sidebar = Page.Locator(".omni-sidebar");
        await Expect(sidebar).ToBeVisibleAsync();
        var nav = Page.Locator(".omni-panel-menu");
        await Expect(nav).ToBeVisibleAsync(new() { Timeout = 10000 });

        // Only the top-level menu items are always rendered. Pipelines/Tasks/Logs
        // are contextual sub-items that only appear once you drill into the Servers/Projects
        // section, so they are not present on the dashboard. Match labels exactly to avoid
        // strict-mode clashes with wrapping links.
        await Expect(SidebarNavItem("Dashboard").First).ToBeVisibleAsync();
        await Expect(SidebarNavItem("Servers").First).ToBeVisibleAsync();
        await Expect(SidebarNavItem("Projects").First).ToBeVisibleAsync();
        await Expect(SidebarNavItem("Settings").First).ToBeVisibleAsync();

        // "Aetheus" can appear twice in the header (app title + the default
        // organization name in the org picker), so target the app title element.
        var appTitle = Page.Locator(".omni-header .app-title");
        await Expect(appTitle).ToBeVisibleAsync(new() { Timeout = 5000 });
        await Expect(appTitle).ToContainTextAsync("Aetheus");

        // Signed in, the mode is one row of OE's application menu (since OE 1.9.0: three joined icon buttons, light,
        // dark and system), not a standalone header button.
        await Page.Locator(".omni-app-menu__trigger").ClickAsync();
        var themeRow = Page.Locator(".omni-app-menu__mode");
        await Expect(themeRow).ToBeVisibleAsync(new() { Timeout = 5000 });
        await Expect(themeRow.GetByRole(AriaRole.Radio)).ToHaveCountAsync(3);
    }
}
