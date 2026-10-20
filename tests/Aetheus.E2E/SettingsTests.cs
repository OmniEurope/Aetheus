// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.E2E;

[Category("E2E")]
[Category("Settings")]
public class SettingsTests : E2ETestBase
{
    protected override bool ShareBrowserContextAcrossTests => true;

    // The Secrets / Registration Tokens / General tabs these tests assert against
    // now live on the admin "Platform Settings" page (/admin/settings). The
    // /settings route is the per-user profile page (Profile/Appearance/...).
    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
    }

    [Test]
    public async Task Settings_PageRendersAndSwitchesAllTabs()
    {
        await NavigateToAsync("admin/settings");
        await Expect(Page.GetByText("Platform Settings").First).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page).ToHaveTitleAsync(new System.Text.RegularExpressions.Regex("Settings"));
        var tabs = Page.Locator(".omni-tabs");
        await Expect(tabs).ToBeVisibleAsync(new() { Timeout = 10000 });

        await Expect(Page.GetByText("General").First).ToBeVisibleAsync();
        await Expect(Page.GetByText("Secrets").First).ToBeVisibleAsync();
        await Expect(Page.GetByText("Registration Tokens").First).ToBeVisibleAsync();
        await WaitForBlazorAsync();

        var generalCard = Page.Locator(".omni-card").First;
        await Expect(generalCard).ToBeVisibleAsync(new() { Timeout = 10000 });

        await Page.GetByText("Secrets").First.ClickAsync();
        await WaitForBlazorAsync();

        // The Secrets tab exposes an "Add" button to create a new secret.
        var addButton = Page.GetByRole(AriaRole.Button, new() { Name = "Add" });
        await Expect(addButton.First).ToBeVisibleAsync(new() { Timeout = 10000 });

        await Page.GetByText("Registration Tokens").First.ClickAsync();
        await WaitForBlazorAsync();

        var generateButton = Page.GetByRole(AriaRole.Button, new() { Name = "Generate", Exact = true });
        await Expect(generateButton.First).ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    [Test]
    [RollbackRelevant]
    public async Task Settings_RegistrationTokensTab_GenerateToken()
    {
        await NavigateToAsync("admin/settings");
        await Page.GetByText("Registration Tokens").First.ClickAsync();
        await WaitForBlazorAsync();

        var generateButton = Page.GetByRole(AriaRole.Button, new() { Name = "Generate", Exact = true });
        await Expect(generateButton.First).ToBeVisibleAsync(new() { Timeout = 10000 });
        var rows = Page.Locator(".omni-data-grid__row");
        var initialRowCount = await rows.CountAsync();
        await generateButton.First.ClickAsync();

        await WaitForBlazorAsync();

        // Should have at least one token in the grid after generating.
        // The grid root renders as `.omni-data-grid` and each data row
        // as `.omni-data-grid__row`.
        var tokenGrid = Page.Locator(".omni-data-grid");
        await Expect(tokenGrid.First).ToBeVisibleAsync(new() { Timeout = 10000 });

        await Expect(rows).ToHaveCountAsync(initialRowCount + 1, new() { Timeout = 10000 });

        // Tokens are returned newest first. Reveal the newly created row and prove the server supplied
        // a real value rather than accepting a pre-existing row as evidence of a successful mutation.
        var newestRow = rows.First;
        await newestRow.GetByRole(AriaRole.Button, new() { Name = "Show Token" }).ClickAsync();
        var tokenValue = (await newestRow.Locator("code.token-code").TextContentAsync())?.Trim();
        Assert.That(tokenValue, Is.Not.Null.And.Not.Empty.And.Not.EqualTo("----------------"));
    }

    [Test]
    public async Task Settings_QualityGates_ExplainsOptionalYamlAndFitsMobileViewport()
    {
        await Page.SetViewportSizeAsync(375, 812);
        await NavigateToAsync("admin/settings");
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Quality gates" }).ClickAsync();

        var card = Page.Locator(".quality-gate-activation-card");
        await Expect(card).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(card.GetByText("Optional", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(card.GetByText("YAML", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(card.GetByRole(AriaRole.Heading, new()
        {
            Name = "Activate a gate only where it matters"
        })).ToBeVisibleAsync();
        await Expect(card.Locator("code")).ToContainTextAsync("type: analysis-gate");
        await Expect(card.Locator("code")).ToContainTextAsync("analysis_preset: recommended");
        await Expect(card.Locator("code")).ToContainTextAsync("analysis_rules:");
        await Expect(card.GetByRole(AriaRole.Button, new() { Name = "Copy", Exact = true }))
            .ToBeVisibleAsync();

        var bounds = await card.BoundingBoxAsync();
        Assert.That(bounds, Is.Not.Null);
        Assert.That(bounds!.X, Is.GreaterThanOrEqualTo(0));
        Assert.That(bounds.X + bounds.Width, Is.LessThanOrEqualTo(375));
    }
}
