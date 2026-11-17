// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.E2E;

[Category("E2E")]
[Category("Alerts")]
public class AlertsTests : E2ETestBase
{
    protected override bool ShareBrowserContextAcrossTests => true;

    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
    }

    [Test]
    public async Task Alerts_NewAlert_CreatesRule()
    {
        await NavigateToAsync("alerts");
        await Page.Locator("button:has-text('Create')").First.ClickAsync();

        var dialog = Page.Locator(".omni-dialog");
        await Expect(dialog).ToBeVisibleAsync(new() { Timeout = 10000 });
        var ruleName = $"E2E alert {DateTime.UtcNow:yyyyMMddHHmmssfff}";
        await dialog.Locator("input").First.FillAsync(ruleName);
        // A rule without a server never fires, so the dialog requires one (F-FRONT-017): pick the first seeded server.
        await dialog.GetByLabel("Server").SelectOptionAsync(new SelectOptionValue { Index = 1 });
        await dialog.Locator("button:has-text('Create')").ClickAsync();

        await Expect(dialog).ToBeHiddenAsync(new() { Timeout = 10000 });
        await Expect(Page.GetByText(ruleName, new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 10000 });
    }
}
