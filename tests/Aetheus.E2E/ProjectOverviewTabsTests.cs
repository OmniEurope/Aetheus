// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>Recette R-41: on the project overview, a click on each tab selects it and writes it in the address,
/// including when the page was opened by a link that names a tab.</summary>
[Category("E2E")]
[Category("Projects")]
public sealed class ProjectOverviewTabsTests : E2ETestBase
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
    }

    [Test]
    [NewSinceDeployedBaseline("R-41")]
    public async Task OverviewTabs_EachClickSelectsItsTab_AndWritesItInTheAddress_AfterALinkNamingATab()
    {
        await NavigateToAsync("projects");
        var projectLink = Page.Locator("a.project-card-name").First;
        await Expect(projectLink).ToBeVisibleAsync(new() { Timeout = 10000 });
        var match = Regex.Match(await projectLink.GetAttributeAsync("href") ?? string.Empty, @"/projects/(\d+)");
        Assert.That(match.Success, Is.True, "A project card must expose the project's address.");
        var projectId = match.Groups[1].Value;

        // The address names a tab that every project has (the expand/contract tab only exists with a database).
        await NavigateToAsync($"projects/{projectId}/overview?tab=readme");
        var tabs = Page.GetByRole(AriaRole.Tab);
        await Expect(tabs.First).ToBeVisibleAsync(new() { Timeout = 10000 });
        var keys = await tabs.EvaluateAllAsync<string[]>("buttons => buttons.map(button => button.dataset.key)");
        Assert.That(keys, Does.Contain("readme").And.Contain("changelog"), "The overview must offer its Readme and Changelog tabs.");
        Assert.That(keys[0], Is.EqualTo("overview"));
        await Expect(Page.Locator("[role='tab'][data-key='readme']")).ToHaveAttributeAsync("aria-selected", "true");

        // Every tab, in order, then each one once more backwards: the selection must follow every click.
        foreach (var key in keys.Concat(keys.Reverse()))
        {
            var tab = Page.Locator($"[role='tab'][data-key='{key}']");
            await tab.ClickAsync();
            await Expect(tab).ToHaveAttributeAsync("aria-selected", "true", new() { Timeout = 10000 });
            var expectedAddress = key == "overview"
                ? new Regex($@"/projects/{projectId}/overview/?(\?(?!.*tab=).*)?$")
                : new Regex($@"/projects/{projectId}/overview\?(.*&)?tab={Regex.Escape(key)}(&.*)?$");
            await Expect(Page).ToHaveURLAsync(expectedAddress, new() { Timeout = 10000 });
            // Still selected once the page has had the time to render again after the address changed.
            await Page.WaitForTimeoutAsync(300);
            await Expect(tab).ToHaveAttributeAsync("aria-selected", "true");
        }
    }
}
