// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>Recette R3-29: an address pasted into the browser opens the application on that page; the
/// browser's Back then climbs to the page's logical parent inside the same document (the Blazor router,
/// no reload, no splash) instead of leaving the application.</summary>
[Category("E2E")]
[Category("Servers")]
public sealed class DeepLinkBackTests : E2ETestBase
{
    private const string MarkerScript = "() => { window.__r329Document = 'kept'; }";
    private const string MarkerRead = "() => window.__r329Document ?? null";

    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
    }

    [Test]
    public async Task BrowserBack_AfterAPastedServerSectionAddress_ClimbsToTheServerThenTheListWithoutReload()
    {
        await NavigateToAsync("servers");
        var server = Page.Locator("a.server-name-link").Filter(new() { HasText = "web-01" });
        await Expect(server).ToBeVisibleAsync(new() { Timeout = 10000 });
        var match = Regex.Match(await server.GetAttributeAsync("href") ?? string.Empty, @"/servers/(\d+)/");
        Assert.That(match.Success, Is.True, "The seeded web-01 server must expose its detail URL.");
        var serverId = match.Groups[1].Value;

        // The pasted address: a new document opened directly on the Services section.
        await NavigateToAsync($"servers/{serverId}/services");
        await Expect(Page).ToHaveURLAsync(new Regex($@"/servers/{serverId}/services$"));
        await Page.WaitForFunctionAsync("() => history.state?.['aetheusParentEntry'] === true", null, new() { Timeout = 10000 });
        await Page.EvaluateAsync(MarkerScript);

        await Page.GoBackAsync(new() { WaitUntil = WaitUntilState.Commit });
        await Expect(Page).ToHaveURLAsync(new Regex($@"/servers/{serverId}/overview$"), new() { Timeout = 10000 });
        await WaitForNoSpinnerAsync();
        Assert.That(await Page.EvaluateAsync<string?>(MarkerRead), Is.EqualTo("kept"),
            "Back from the pasted address reloaded the document instead of routing to the server.");
        await Expect(Page.Locator("#app-splash")).ToHaveCountAsync(0);
        await Expect(Page.Locator("[data-testid='" + PlaywrightConfig.BlazorReadyTestId + "']")).ToBeAttachedAsync();

        // The parent entry is followed by the page that was pasted: Forward returns to it, still in place.
        await Page.GoForwardAsync(new() { WaitUntil = WaitUntilState.Commit });
        await Expect(Page).ToHaveURLAsync(new Regex($@"/servers/{serverId}/services$"), new() { Timeout = 10000 });
        Assert.That(await Page.EvaluateAsync<string?>(MarkerRead), Is.EqualTo("kept"),
            "Forward to the pasted page reloaded the document.");

        // The application's own back button on the server overview leads to the list, in place too.
        await Page.GoBackAsync(new() { WaitUntil = WaitUntilState.Commit });
        await Expect(Page).ToHaveURLAsync(new Regex($@"/servers/{serverId}/overview$"), new() { Timeout = 10000 });
        await WaitForNoSpinnerAsync();
        await Page.Locator(".omni-page-header__back").First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(@"/servers$"), new() { Timeout = 10000 });
        await Expect(Page.Locator("a.server-name-link").First).ToBeVisibleAsync(new() { Timeout = 10000 });
        Assert.That(await Page.EvaluateAsync<string?>(MarkerRead), Is.EqualTo("kept"),
            "The header's back arrow on the server reloaded the document instead of routing to the list.");
    }

    [Test]
    public async Task Reload_OfAPastedAddress_DoesNotSeedASecondParentEntry()
    {
        await NavigateToAsync("alerts");
        await Page.WaitForFunctionAsync("() => history.state?.['aetheusParentEntry'] === true", null, new() { Timeout = 10000 });
        var lengthBefore = await Page.EvaluateAsync<int>("() => history.length");

        await Page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await WaitForBlazorAsync();
        // The shell has started and had its chance to seed: the splash is gone.
        await Expect(Page.Locator("#app-splash")).ToHaveCountAsync(0, new() { Timeout = 15000 });

        Assert.That(await Page.EvaluateAsync<int>("() => history.length"), Is.EqualTo(lengthBefore),
            "A reload must not add another parent entry to the history.");
    }
}
