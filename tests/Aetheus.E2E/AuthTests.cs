// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.E2E;

[Category("E2E")]
[Category("Auth")]
public class AuthTests : E2ETestBase
{
    protected override bool UsePreauthenticatedContext => false;

    [Test]
    public async Task OfflineReload_ShowsRecoveryPage_ThenReturnsToLogin()
    {
        // A service worker only exists in a secure context. Production serves HTTPS and ylaunch
        // https://localhost, but the QA stack reaches the frontend over plain HTTP on a container
        // name, where navigator.serviceWorker is undefined and the recovery page cannot install
        // (candidate 2421 timed out here on V and V-1). There, this test drives its own Chromium that
        // reaches the same frontend under a *.localhost name, which browsers treat as a secure context,
        // so the real offline recovery is still exercised. --unsafely-treat-insecure-origin-as-secure
        // was tried first (candidate 2427): Playwright's headless Chromium ignores it.
        var origin = new Uri(FrontendUrl);
        if (origin.Scheme == Uri.UriSchemeHttps || origin.IsLoopback)
        {
            await AssertOfflineRecoveryAsync(Page, Context, FrontendUrl);
            return;
        }

        const string secureAlias = "aetheus-e2e.localhost";
        await using var browser = await Playwright.Chromium.LaunchAsync(new()
        {
            Args = [$"--host-resolver-rules=MAP {secureAlias} {origin.Host}"]
        });
        await using var context = await browser.NewContextAsync(ContextOptions());
        var page = await context.NewPageAsync();
        var aliasUrl = new UriBuilder(origin) { Host = secureAlias }.Uri.GetLeftPart(UriPartial.Authority);
        await AssertOfflineRecoveryAsync(page, context, aliasUrl);
    }

    private async Task AssertOfflineRecoveryAsync(IPage page, IBrowserContext context, string frontendUrl)
    {
        await page.GotoAsync($"{frontendUrl}/login");
        Assert.That(await page.EvaluateAsync<bool>("() => window.isSecureContext"), Is.True,
            "The offline recovery needs a secure context for its service worker.");
        await page.WaitForSelectorAsync("#Email", new() { Timeout = 15000 });
        await page.WaitForFunctionAsync("() => Boolean(navigator.serviceWorker?.['controller'])", null,
            new() { Timeout = 10000 });

        await context.SetOfflineAsync(true);
        await page.ReloadAsync(new() { WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded });
        // A reload cold-boots the page: wait with the boot budget, never the 5s default (nightly 2697).
        await Expect(page.GetByRole(Microsoft.Playwright.AriaRole.Heading, new() { Name = "Connection unavailable" }))
            .ToBeVisibleAsync(new() { Timeout = PlaywrightConfig.AppReadyTimeoutMs });
        await Expect(page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Retry" }))
            .ToBeVisibleAsync();
        await Expect(page.Locator("#app-splash")).ToHaveCountAsync(0);

        await context.SetOfflineAsync(false);
        await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Retry" }).ClickAsync();
        await Expect(page.Locator("#Email")).ToBeVisibleAsync(new() { Timeout = 15000 });
    }

    [TestCase("blazor")]
    [TestCase("dotnet")]
    public async Task MissingRuntimeAsset_ShowsRetryInsteadOfPermanentSplash(string asset)
    {
        AllowBrowserDiagnostic(new System.Text.RegularExpressions.Regex(
            @"404|blazor[.]webassembly|dotnet[.]|Failed to load resource",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        var assetPattern = asset == "dotnet"
            ? @"/_framework/dotnet[.][^/]+[.]js$"
            : @"/_framework/blazor[.]webassembly(?:[.][^/]+)?[.]js$";
        await Page.RouteAsync(new System.Text.RegularExpressions.Regex(assetPattern), route => route.FulfillAsync(new()
        {
            Status = 404,
            ContentType = "text/plain",
            Body = "Missing runtime asset"
        }));

        await Page.GotoAsync($"{FrontendUrl}/login", new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Expect(Page.Locator("#app-start-error")).ToBeVisibleAsync();
        await Expect(Page.Locator("#app-start-retry")).ToHaveTextAsync("Retry");
        await Expect(Page.Locator("#app-splash")).ToHaveCountAsync(0);

        await Page.Locator("#app-start-retry").ClickAsync();
        await Page.WaitForFunctionAsync("() => performance.getEntriesByType('navigation')[0]?.type === 'reload' && document.getElementById('app-start-error')?.getAttribute('hidden') === null");
        await Expect(Page.Locator("#app-start-error")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Login_ValidCredentials_RedirectsToDashboard()
    {
        await Page.GotoAsync($"{FrontendUrl}/login");
        await Page.WaitForSelectorAsync("#Email", new() { Timeout = 15000 });

        await Page.FillAsync("#Email", AdminEmail);
        await Page.FillAsync("#Password", AdminPassword);
        await Page.ClickAsync("button[type='submit']");

        await Page.WaitForURLAsync($"{FrontendUrl}/", new() { Timeout = 10000 });
        await Expect(Page).ToHaveURLAsync($"{FrontendUrl}/");
    }

    [Test]
    public async Task Login_InvalidCredentials_ShowsError()
    {
        // This test deliberately submits invalid credentials; the browser correctly reports the
        // API's 401 as a failed resource. Keep that one expected diagnostic scoped to this test.
        AllowBrowserDiagnostic(new System.Text.RegularExpressions.Regex(
            @"status of 401",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        await Page.GotoAsync($"{FrontendUrl}/login");
        await Page.WaitForSelectorAsync("#Email", new() { Timeout = 15000 });

        await Page.FillAsync("#Email", "wronguser@example.com");
        await Page.FillAsync("#Password", "wrongpassword");
        await Page.ClickAsync("button[type='submit']");

        var alert = Page.Locator(".omni-alert");
        await Expect(alert).ToBeVisibleAsync(new() { Timeout = 5000 });
    }

    [Test]
    public async Task Login_EmptyFields_ShowsValidation()
    {
        await Page.GotoAsync($"{FrontendUrl}/login");
        await Page.WaitForSelectorAsync("#Email", new() { Timeout = 15000 });

        await Page.FillAsync("#Email", "");
        await Page.FillAsync("#Password", "");
        await Page.ClickAsync("button[type='submit']");

        var validationMessages = Page.Locator(".omni-validation-message");
        await Expect(validationMessages.First).ToBeVisibleAsync(new() { Timeout = 5000 });
    }

    [Test]
    public async Task UnauthenticatedUser_RedirectsToLogin()
    {
        await Page.GotoAsync($"{FrontendUrl}/servers");

        // Wait on the redirect itself, not LoadState.NetworkIdle: the WASM app keeps a SignalR WebSocket
        // open so the network is rarely idle (E2ETestBase documents this as the fragile pattern). The auth
        // guard bounces an unauthenticated user to /login - assert directly on that URL transition.
        await Page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/login"), new() { Timeout = 15000 });
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/login"));
    }

    [Test]
    [NewSinceDeployedBaseline("R-389")]
    public async Task Logout_RedirectsToLogin()
    {
        await LoginAsync();

        // Signing out lives in OE's application menu at the end of the bar, which must be opened first.
        await Page.Locator(".omni-app-menu__trigger").ClickAsync();
        // The application menu ends with the version and a sign-out button.
        var logoutRow = Page.Locator(".omni-app-menu__footer .omni-app-menu__sign-out");
        await Expect(logoutRow).ToBeVisibleAsync(new() { Timeout = 5000 });
        await logoutRow.ClickAsync();

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/login"), new() { Timeout = 5000 });
    }

    [Test]
    public async Task Login_PageLoadsWithExpectedTitleAndForm()
    {
        var response = await Page.GotoAsync($"{FrontendUrl}/login");
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Ok, Is.True);
        await Page.WaitForSelectorAsync("#Email", new() { Timeout = 15000 });

        await Expect(Page).ToHaveTitleAsync(new System.Text.RegularExpressions.Regex("Login"));
        await Expect(Page.Locator("#Email")).ToBeVisibleAsync();
        await Expect(Page.Locator("#Password")).ToBeVisibleAsync();
        await Expect(Page.Locator("button[type='submit']")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Login_MobileCard_RemainsInsideViewport()
    {
        await Page.SetViewportSizeAsync(375, 667);
        await Page.GotoAsync($"{FrontendUrl}/login");
        await Page.WaitForSelectorAsync(".login-container .omni-card", new() { Timeout = 15000 });

        var box = await Page.Locator(".login-container .omni-card").BoundingBoxAsync();
        Assert.That(box, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(box!.X, Is.GreaterThanOrEqualTo(0));
            Assert.That(box.X + box.Width, Is.LessThanOrEqualTo(375));
        });
        var hasHorizontalOverflow = await Page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        var overflowDetails = await Page.EvaluateAsync<string>("""
            () => [...document.querySelectorAll('*')]
                .map(element => ({
                    name: `${element.tagName.toLowerCase()}${element.id ? `#${element.id}` : ''}${[...element.classList].map(value => `.${value}`).join('')}`,
                    left: Math.round(element.getBoundingClientRect().left),
                    right: Math.round(element.getBoundingClientRect().right),
                    width: Math.round(element.getBoundingClientRect().width),
                    scrollWidth: element.scrollWidth
                }))
                .filter(item => item.left < 0 || item.right > document.documentElement.clientWidth)
                .slice(0, 10)
                .map(item => `${item.name} left=${item.left} right=${item.right} width=${item.width} scrollWidth=${item.scrollWidth}`)
                .join('; ')
            """);
        Assert.That(hasHorizontalOverflow, Is.False, overflowDetails);
    }
}
