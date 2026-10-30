// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.E2E;

/// <summary> Recette R-46: the page content ends on the same vertical line as the top-right app menu, whether or
/// not the page shows a classic scrollbar (the header spans the window, the content area is one scrollbar
/// narrower, and the header gives that width back). Measured on the dashboard at desktop width.
/// </summary>
[Category("E2E")]
[Category("Dashboard")]
[NonParallelizable]
public sealed class PageGutterLayoutTests : E2ETestBase
{
    protected override bool ShareBrowserContextAcrossTests => true;

    [SetUp]
    public async Task SetUp() => await LoginAsync();

    [Test]
    public async Task ContentRightEdge_AlignsWithTheHeaderMenu_AtDesktopWidth()
    {
        await Page.SetViewportSizeAsync(1280, 900);
        await NavigateToAsync("");
        await Expect(Page.Locator(".omni-header .omni-app-menu__trigger")).ToBeVisibleAsync(new() { Timeout = 15000 });

        var edges = await Page.EvaluateAsync<double[]>("""
            () => {
                const content = document.querySelector('.app-body-wrapper').getBoundingClientRect();
                const menu = document.querySelector('.omni-header .omni-app-menu__trigger').getBoundingClientRect();
                const main = document.getElementById('main-content');
                return [content.right, menu.right, main.offsetWidth - main.clientWidth];
            }
            """);

        // edges[2] is the width of the page scrollbar: 0 where scrollbars overlay the page (headless Chromium
        // hides them), 15 with a classic one. The alignment must hold either way.
        Assert.That(Math.Abs(edges[0] - edges[1]), Is.LessThanOrEqualTo(1),
            $"Content right {edges[0]} and header menu right {edges[1]} differ (page scrollbar {edges[2]}px).");
    }
}
