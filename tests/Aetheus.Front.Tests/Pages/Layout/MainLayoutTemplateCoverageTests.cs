// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Aetheus.Shared.Components.Organizations;
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Template coverage for MainLayout.razor driven by real renders and user
/// interactions (button/menu-row clicks) with assertions on markup and
/// navigation - complements <see cref="MainLayoutTemplateBranchTests"/>
/// (which covers breadcrumb / role badge / dark-mode / version banner branches).
/// </summary>
public class MainLayoutTemplateCoverageTests : BunitContext
{
    private BunitTestHelper.TestHandler RegisterWithAuth(bool authenticated = true, bool isAdmin = false)
    {
        var handler = BunitTestHelper.RegisterServices(this, authenticated, isAdmin);
        Services.AddScoped(sp => new TaskTrackerService(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<AuthStateProvider>(),
            sp.GetRequiredService<HubConnectionFactory>(),
            NullLogger<TaskTrackerService>.Instance));

        // MainLayout.OnInitializedAsync re-reads the token via the AuthStateProvider's IJSRuntime,
        // which the helper stubs to return null (wiping the token). Re-register the provider with a
        // JS stub returning a valid JWT so the authenticated chrome (user button, org area, menu)
        // actually renders.
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

    // ── Help button rendered for an authenticated user (line 50-51) ───────────

    [Fact]
    public void AuthenticatedUser_RendersHelpAndUserButtons()
    {
        RegisterWithAuth();
        var cut = Render<MainLayout>();

        // The help action button and the application menu, its trigger carrying the user name, live in the
        // auth header.
        Assert.Contains("header-action-btn", cut.Markup);
        Assert.Equal("user", cut.Find(".omni-header .omni-app-menu__trigger-name").TextContent);
    }

    // ── Skip-to-content link is always present once the chrome renders (line 12)

    [Fact]
    public void Renders_SkipToContentLink()
    {
        RegisterWithAuth();
        var cut = Render<MainLayout>();

        Assert.Contains("skip-link", cut.Markup);
        Assert.Contains("#main-content", cut.Markup);
    }

    private const string MenuTrigger = ".omni-app-menu__trigger";
    private const string MenuCard = ".omni-app-menu__card";

    private void OpenMenu(IRenderedComponent<MainLayout> cut)
    {
        cut.Find(MenuTrigger).Click();
        cut.WaitForElement(MenuCard);
    }

    // ── Admin user, orgs loaded, no available orgs → "AllOrganizations" badge ──
    // The org selector now lives in the user menu (relocated from the top bar): the
    // (Auth.IsAdmin && Orgs.IsLoaded) else-if branch renders once the menu is opened.

    [Fact]
    public void AdminUser_OrgsLoadedEmpty_RendersAllOrganizationsBadge()
    {
        var handler = RegisterWithAuth(authenticated: true, isAdmin: true);
        // The layout's own organizations load answers "no organization": the state comes from the page's
        // bootstrap instead of a private setter written beside it, which that load could overtake.
        handler.SetJsonResponse(HttpMethod.Get, "api/organizations/me", new List<MyOrganizationDto>());
        var orgs = Services.GetRequiredService<ActiveOrganizationService>();

        var cut = Render<MainLayout>();
        cut.WaitForState(() => orgs.IsLoaded);
        OpenMenu(cut);

        // The application's own row sits in OE's slot for it, under the identity.
        Assert.Contains("AllOrganizations", cut.Find(".omni-app-menu__extra").TextContent, StringComparison.Ordinal);
    }

    // ── Orgs loaded with available orgs → dropdown in the application menu ─────

    [Fact]
    public void OrgsLoaded_RendersOrgPickerInUserMenu()
    {
        RegisterWithAuth();
        var orgs = Services.GetRequiredService<ActiveOrganizationService>();

        var cut = Render<MainLayout>();

        // Seed the "loaded with orgs" state AFTER the initial render: seeding before the render races
        // with MainLayout.OnInitializedAsync's async org bootstrap, which made this test flaky under
        // coverage instrumentation on CI. Opening the menu re-renders it against the now-set state.
        typeof(ActiveOrganizationService).GetProperty("Available")!
            .SetValue(orgs, new List<MyOrganizationDto>
            {
                new(42, "Acme", "acme", OrganizationRole.Owner)
            });
        typeof(ActiveOrganizationService).GetProperty("IsLoaded")!.SetValue(orgs, true);
        OpenMenu(cut);

        Assert.Single(cut.FindAll(".omni-app-menu__extra .user-menu-org-picker"));
    }

    // ── Language row: the two languages, the current one selected ─────────────

    [Fact]
    public void UserMenu_OffersBothLanguages_TheCurrentOneSelected()
    {
        RegisterWithAuth();
        var cut = Render<MainLayout>();

        OpenMenu(cut);

        var menu = cut.FindComponent<OmniAppMenu>().Instance;
        Assert.Equal(["fr-FR", "en"], menu.Languages!.Select(language => language.Code));
        Assert.Equal(["LanguageFrench", "LanguageEnglish"], menu.Languages!.Select(language => language.Name));
        Assert.Equal(MainLayout.CurrentLanguage, menu.Language);
        Assert.Single(cut.FindAll(".omni-app-menu__language"));
    }

    // ── Settings row click → navigates to /settings ───────────────────────────

    [Fact]
    public void UserMenu_SettingsRowClick_NavigatesToSettings()
    {
        RegisterWithAuth();
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var cut = Render<MainLayout>();

        OpenMenu(cut);
        cut.Find(".omni-app-menu__settings").Click();

        // The menu closes, then raises OnSettings; observe the navigation it produces rather than racing
        // the dispatch under the full parallel suite.
        cut.WaitForAssertion(
            () => Assert.Contains("settings", nav.Uri));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(MenuCard)));
    }

    // ── Sign-out button click → navigates to /login ───────────────────────────

    [Fact]
    public void UserMenu_SignOutClick_NavigatesToLogin()
    {
        RegisterWithAuth();
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var cut = Render<MainLayout>();

        OpenMenu(cut);

        // Use a neutral sign-out button beside the version in the application menu.
        var lastLine = cut.Find(MenuCard).Children.Last();
        Assert.Contains("omni-app-menu__footer", lastLine.ClassList);
        var signOut = lastLine.QuerySelector("button.omni-app-menu__sign-out")!;
        Assert.Contains("omni-button--secondary", signOut.ClassList);
        signOut.Click();

        // The sign-out awaits the realtime stop and two localStorage removals before navigating; observe
        // the user-visible navigation instead of racing that continuation.
        cut.WaitForAssertion(
            () => Assert.Contains("login", nav.Uri));
    }

    // ── The menu is OE's popover: trigger toggles, dismiss closes ─────────────

    [Fact]
    public void UserMenu_TriggerClick_OpensPanelWithMenuCard()
    {
        RegisterWithAuth();
        var cut = Render<MainLayout>();
        Assert.Empty(cut.FindAll(MenuCard));

        OpenMenu(cut);

        Assert.Equal("true", cut.Find(MenuTrigger).GetAttribute("aria-expanded"));
    }

    [Fact]
    public void UserMenu_SecondTriggerClick_ClosesPanel()
    {
        RegisterWithAuth();
        var cut = Render<MainLayout>();
        OpenMenu(cut);

        cut.Find(MenuTrigger).Click();

        cut.WaitForAssertion(
            () => Assert.Empty(cut.FindAll(MenuCard)));
        Assert.Equal("false", cut.Find(MenuTrigger).GetAttribute("aria-expanded"));
    }

    // No full-page backdrop: a click outside (fromKeyboard: false) or Escape (fromKeyboard: true) reaches
    // the popover through omni-focus.js, which calls OnDismissRequestedAsync.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UserMenu_DismissRequest_EscapeOrOutsideClick_ClosesPanel(bool fromKeyboard)
    {
        RegisterWithAuth();
        var cut = Render<MainLayout>();
        OpenMenu(cut);

        var popover = cut.FindComponent<OmniAppMenu>().FindComponent<OmniPopover>();
        await cut.InvokeAsync(() => popover.Instance.OnDismissRequestedAsync(fromKeyboard));

        cut.WaitForAssertion(
            () => Assert.Empty(cut.FindAll(MenuCard)));
    }

    // ── Navigation closes the menu the layout holds open ─────────────────────

    [Fact]
    public void UserMenu_Navigation_ClosesIt()
    {
        RegisterWithAuth();
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var cut = Render<MainLayout>();
        OpenMenu(cut);

        cut.InvokeAsync(() => nav.NavigateTo("/servers"));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(MenuCard)));
    }
}
