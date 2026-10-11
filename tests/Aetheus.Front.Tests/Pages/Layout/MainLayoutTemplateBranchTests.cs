// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Template-branch coverage for MainLayout.razor - exercised by RENDERING the
/// component and asserting on the produced markup / navigation, not by reading
/// fields via reflection. Each test drives a real @if branch of the template.
/// </summary>
public class MainLayoutTemplateBranchTests : BunitContext
{
    private BunitTestHelper.TestHandler RegisterWithTaskTracker(bool authenticated = true, bool isAdmin = false)
    {
        var handler = BunitTestHelper.RegisterServices(this, authenticated, isAdmin);
        handler.SetResponse(HttpMethod.Get, "health/live", System.Net.HttpStatusCode.ServiceUnavailable);
        Services.AddScoped(sp => new TaskTrackerService(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<AuthStateProvider>(),
            sp.GetRequiredService<HubConnectionFactory>(),
            NullLogger<TaskTrackerService>.Instance));

        // MainLayout.OnInitializedAsync calls Auth.InitializeAsync(), which RE-READS the token
        // from localStorage via the AuthStateProvider's own IJSRuntime. The helper wires that
        // provider with an NSubstitute IJSRuntime returning null, so the token is wiped and the
        // authenticated chrome never renders. Re-register the provider with a JS stub that
        // returns a valid JWT, so the real init path keeps the user authenticated.
        if (authenticated)
        {
            var js = Substitute.For<IJSRuntime>();
            js.InvokeAsync<string?>("localStorage.getItem", Arg.Is<object?[]>(a => a.Length == 1 && (string?)a[0] == "aetheus_auth_token"))
                .Returns(ValueTask.FromResult<string?>(MakeJwt(isAdmin)));
            var auth = new AuthStateProvider(js, NullLogger<AuthStateProvider>.Instance);
            Services.AddSingleton(auth);

            // The authenticated branch renders <NavMenu>, which depends on AlertNotificationService.
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = "http://test:5301" })
                .Build();
            var hubFactory = new HubConnectionFactory(
                config, auth, NullLogger<AuthDelegatingHandler>.Instance);
            Services.AddSingleton(new AlertNotificationService(auth, hubFactory));
        }

        return handler;
    }

    private static string MakeJwt(bool isAdmin)
    {
        var header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"HS256\"}"));
        object claims = isAdmin
            ? new { sub = "admin", role = "Admin", unique_name = "admin", exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }
            : new { sub = "user", unique_name = "user", exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() };
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(claims);
        var p64 = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{header}.{p64}.sig";
    }

    // ── Authenticated user → header chrome + user button rendered ─────────────
    // Covers: Auth.IsAuthenticated branch (header actions block, lines 17-116).

    [Fact]
    public void AuthenticatedUser_RendersHeaderChromeAndUserButton()
    {
        RegisterWithTaskTracker(authenticated: true);

        var cut = Render<MainLayout>();

        // Title and the username (auth header) appear only in the authenticated branch.
        Assert.Contains("Aetheus", cut.Markup);
        Assert.Equal("user", cut.Find(".omni-app-menu__trigger-name").TextContent);
        // Sidebar toggle is gated on Auth.IsAuthenticated.
        Assert.Contains("header-bar", cut.Markup);
    }

    // ── Unauthenticated user → redirect to /login, no header actions ──────────
    // Covers: Auth.IsAuthenticated = false (header actions / sidebar skipped).

    [Fact]
    public void UnauthenticatedUser_RedirectsToLogin_NoUserMenu()
    {
        RegisterWithTaskTracker(authenticated: false);
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        var cut = Render<MainLayout>();

        Assert.Contains("login", nav.Uri);
        // The authenticated user button must NOT be rendered for an anonymous user.
        Assert.Empty(cut.FindAll(".omni-app-menu__trigger-name"));
    }

    /// <summary>PLAN-005 lot 8 / D47: a URL pasted without a session keeps its page for after the login.</summary>
    [Fact]
    public void UnauthenticatedUser_OnAPage_IsSentToLoginWithThatPage()
    {
        RegisterWithTaskTracker(authenticated: false);
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("http://localhost/projects/1/pipelines?tab=runs");

        Render<MainLayout>();

        Assert.Equal("http://localhost/login?returnUrl=%2Fprojects%2F1%2Fpipelines%3Ftab%3Druns", nav.Uri);
    }

    [Fact]
    public void BreadcrumbItems_AreNotRenderedByTheShell()
    {
        RegisterWithTaskTracker();
        var breadcrumb = Services.GetRequiredService<BreadcrumbService>();
        var cut = Render<MainLayout>();

        cut.InvokeAsync(() => breadcrumb.Set([
            new BreadcrumbItem("Servers", "/servers"),
            new BreadcrumbItem("web-01", null)
        ]));
        cut.Render();

        // PLAN-003 lot 1: setting breadcrumb items paints nothing here; the page header draws them.
        Assert.DoesNotContain("omni-breadcrumb", cut.Markup);
        Assert.DoesNotContain("web-01", cut.Markup);
        Assert.DoesNotContain("breadcrumb-slot", cut.Markup);
    }

    // ── Application menu (OE's OmniAppMenu, STD-SHELL) → identity, role, rows ──

    [Fact]
    public void UserMenu_WhenOpened_RendersIdentityRoleAndRows()
    {
        RegisterWithTaskTracker(authenticated: true, isAdmin: false);
        var cut = Render<MainLayout>();

        // Closed by default - the menu card is absent.
        Assert.Empty(cut.FindAll(".omni-app-menu__card"));

        // Click the trigger - the real path that opens the menu.
        cut.Find(".omni-app-menu__trigger").Click();
        cut.WaitForElement(".omni-app-menu__card", TimeSpan.FromSeconds(10));

        Assert.Equal("user", cut.Find(".omni-app-menu__identity .omni-app-menu__label").TextContent);
        // Non-admin user → "User" role badge (not "Admin").
        Assert.Equal("User", cut.Find(".omni-app-menu__identity .omni-badge").TextContent.Trim());
        // Theme, settings, the version and signing out are all bound, so all show.
        Assert.Single(cut.FindAll(".omni-app-menu__theme"));
        Assert.Single(cut.FindAll(".omni-app-menu__settings"));
        Assert.Single(cut.FindAll(".omni-app-menu__sign-out"));
        Assert.StartsWith("Aetheus v", cut.Find(".omni-app-menu__version").TextContent, StringComparison.Ordinal);
        // Two languages are offered, so the language row shows.
        Assert.Single(cut.FindAll(".omni-app-menu__language"));
    }

    [Fact]
    public void UserMenu_AdminUser_RendersAdminRoleBadge()
    {
        RegisterWithTaskTracker(authenticated: true, isAdmin: true);
        var cut = Render<MainLayout>();

        cut.Find(".omni-app-menu__trigger").Click();
        cut.WaitForElement(".omni-app-menu__card", TimeSpan.FromSeconds(10));

        Assert.Equal("Admin", cut.Find(".omni-app-menu__identity .omni-badge").TextContent.Trim());
    }

    // ── Mode row: dark by default; picking light stores it and repaints ─────────

    [Fact]
    public async Task ModeRow_DefaultsToDark_AndPickingLightStoresAndRepaints()
    {
        RegisterWithTaskTracker();
        var cut = Render<MainLayout>();

        cut.Find(".omni-app-menu__trigger").Click();
        cut.WaitForElement(".omni-app-menu__card", TimeSpan.FromSeconds(10));
        // OE's order: light, dark, system; the pressed one is the stored mode (dark when nothing is stored).
        var modes = cut.FindAll(".omni-app-menu__mode");
        Assert.Equal(["false", "true", "false"], modes.Select(mode => mode.GetAttribute("aria-checked")));

        // ClickAsync: the handler is async (it stores and repaints), so the dispatch task is awaited.
        await modes[0].ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(OmniAppearance.Light, Services.GetRequiredService<Aetheus.Front.Components.Settings.SiteAppearanceState>().Appearance);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "localStorage.setItem"
            && Equals(i.Arguments[0], StorageKeys.Theme) && Equals(i.Arguments[1], "light"));
        // Recette R2-112: the mode is no longer copied on the document root; the scope carries it.
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "Aetheus.setOmniTheme");
        cut.WaitForAssertion(() => Assert.Equal("true", cut.FindAll(".omni-app-menu__mode")[0].GetAttribute("aria-checked")));
    }

    // ── _newVersionAvailable = true → version alert banner (lines 133-140) ────

    [Fact]
    public void NewVersionAvailable_RendersReloadAlertBanner()
    {
        RegisterWithTaskTracker();
        var cut = Render<MainLayout>();

        // Default: no banner.
        Assert.DoesNotContain("version-alert-bar", cut.Markup);

        // Simulate the version-check loop flagging an update.
        cut.InvokeAsync(() => { cut.Instance._newVersionAvailable = true; });
        cut.Render();

        Assert.Contains("version-alert-bar", cut.Markup);
        Assert.Contains("NewVersionAvailable", cut.Markup);
        Assert.Contains("ReloadNow", cut.Markup);
    }
}
