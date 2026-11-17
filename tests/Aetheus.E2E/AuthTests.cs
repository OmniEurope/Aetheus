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
        // A service worker (and the WebAssembly runtime's crypto.subtle) only exists in a secure context. The
        // QA stack reaches the frontend over plain HTTP on a container name: the suite's browser is launched
        // with that origin treated as secure (PlaywrightConfig.WithSecureFrontendOrigin), so the real offline
        // recovery is exercised on the same page as every other test (candidate 2421 timed out here on V and V-1).
        // Going offline deliberately fails the browser's own favicon request: that console line is the test's doing.
        AllowBrowserDiagnostic(new System.Text.RegularExpressions.Regex(@"ERR_INTERNET_DISCONNECTED"));
        await Page.GotoAsync($"{FrontendUrl}/login");
        Assert.That(await Page.EvaluateAsync<bool>("() => window.isSecureContext"), Is.True,
            "The offline recovery needs a secure context for its service worker.");
        await Page.WaitForSelectorAsync("#Email", new() { Timeout = 15000 });
        await Page.WaitForFunctionAsync("() => Boolean(navigator.serviceWorker?.['controller'])", null,
            new() { Timeout = 10000 });

        await Context.SetOfflineAsync(true);
        await Page.ReloadAsync(new() { WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded });
        // A reload cold-boots the page: wait with the boot budget, never the 5s default (nightly 2697).
        await Expect(Page.GetByRole(Microsoft.Playwright.AriaRole.Heading, new() { Name = "Connection unavailable" }))
            .ToBeVisibleAsync(new() { Timeout = PlaywrightConfig.AppReadyTimeoutMs });
        await Expect(Page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Retry" }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator("#app-splash")).ToHaveCountAsync(0);

        await Context.SetOfflineAsync(false);
        await Page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Retry" }).ClickAsync();
        await Expect(Page.Locator("#Email")).ToBeVisibleAsync(new() { Timeout = 15000 });
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
