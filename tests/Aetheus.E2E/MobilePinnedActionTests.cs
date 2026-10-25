// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>
/// Recette R-30, R-31, R-40: on a phone the page's one main action (Run on a pipeline, Re-run on a run)
/// stays visible without a chevron, right-aligned on its own row (OmniPageHeader.PinnedActionsContent),
/// the "..." menu stays on line 1, and the variants of the action are entries of that menu. Measured at
/// 375 px because the former split button could be folded away, leaving a zero-width control.
/// </summary>
[Category("E2E")]
[Category("Pipelines")]
[NonParallelizable]
public sealed class MobilePinnedActionTests : E2ETestBase
{
    protected override bool ShareBrowserContextAcrossTests => true;

    // The French titles are the long ones ("#1 Exécution du pipeline"): the measure that showed the "..."
    // menu pushed under the title (R-31) needs them; in English the title fits beside the menu.
    public override BrowserNewContextOptions ContextOptions()
    {
        var options = base.ContextOptions();
        options.Locale = "fr-FR";
        return options;
    }

    private const int PhoneWidth = 375;
    private const int PhoneHeight = 812;

    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
    }

    private async Task AssertPinnedAndMenuAsync(string buttonName, string menuEntry)
    {
        var header = Page.Locator(".omni-page-header").First;
        var pinned = header.Locator(".omni-page-header__pinned").GetByRole(AriaRole.Button, new() { Name = buttonName, Exact = true });
        await Expect(pinned).ToBeVisibleAsync(new() { Timeout = 15000 });

        var button = await pinned.BoundingBoxAsync();
        var trigger = header.Locator(".omni-page-header__menu .omni-overflow-menu__trigger");
        await Expect(trigger).ToBeVisibleAsync();
        var menu = await trigger.BoundingBoxAsync();
        var title = await header.Locator(".omni-page-header__title").BoundingBoxAsync();
        Assert.Multiple(() =>
        {
            Assert.That(button, Is.Not.Null);
            Assert.That(menu, Is.Not.Null);
            Assert.That(button!.Width, Is.GreaterThan(24), "The pinned button must have a real width, not w0.");
            Assert.That(button.X + button.Width, Is.LessThanOrEqualTo(PhoneWidth + 1), "The pinned button stays inside the phone.");
            Assert.That(button.X + button.Width, Is.GreaterThan(PhoneWidth - 48), "The pinned button is aligned to the inline end.");
            Assert.That(title, Is.Not.Null);
            // R-31: the title gives way to the menu, which stays on the title line at the inline end
            // (it fell under the title, on the left, when application CSS kept the title at its full width).
            Assert.That(menu!.Y, Is.LessThan(title!.Y + title.Height), "The menu stays on line 1, beside the title.");
            Assert.That(menu.X + menu.Width, Is.GreaterThan(PhoneWidth - 48), "The menu is aligned to the inline end.");
            Assert.That(menu.Y, Is.LessThan(button.Y), "The pinned button is on a row under the menu.");
        });
        Assert.That(await header.Locator(".omni-split-button").CountAsync(), Is.Zero, "The pinned action has no chevron.");

        await trigger.ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Menuitem, new() { Name = menuEntry }).First).ToBeVisibleAsync();
    }

    [Test]
    public async Task R40_PipelinePage_RunIsPinnedWithoutChevron_AndItsOptionsAreInTheMenu()
    {
        await Page.SetViewportSizeAsync(PhoneWidth, PhoneHeight);
        await NavigateToAsync("/pipelines/1");
        await WaitForNoSpinnerAsync();

        await AssertPinnedAndMenuAsync("Exécuter", "Options");
    }

    [Test]
    public async Task R30_R31_R40_RunPage_RerunIsPinnedWithoutChevron_AndItsVariantsAreInTheMenu()
    {
        // Run 1 of the demo dataset, served as a finished run so that Re-run is offered; the rest stays real.
        await Page.RouteAsync("**/api/pipelines/runs/1", async route =>
        {
            var response = await route.FetchAsync();
            var run = System.Text.Json.Nodes.JsonNode.Parse(await response.TextAsync())!.AsObject();
            run["status"] = "Success";
            run["completedAt"] = DateTime.UtcNow;
            await route.FulfillAsync(new() { Response = response, Body = run.ToJsonString() });
        });

        await Page.SetViewportSizeAsync(PhoneWidth, PhoneHeight);
        await NavigateToAsync("/pipelines/runs/1");
        await WaitForNoSpinnerAsync();

        await AssertPinnedAndMenuAsync("Relancer", "Relancer (même commit)");
    }
}
