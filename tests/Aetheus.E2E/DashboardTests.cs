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
}
