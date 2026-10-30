// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Tests MainLayout logic methods via reflection. Uses BunitContext so that bUnit's
/// NavigationManager (which is properly initialized) is available for injection.
/// </summary>
public class MainLayoutRenderTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags PrivPub = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;

    public MainLayoutRenderTests()
    {
        BunitTestHelper.RegisterServices(this);
    }

    private static void SetProperty(MainLayout instance, string name, object value) =>
        typeof(MainLayout).GetProperty(name, Priv)!.SetValue(instance, value);

    private MainLayout CreateInstance(string path = "servers")
    {
        var instance = new MainLayout();

        // Use bUnit's registered NavigationManager (properly initialized)
        var nav = Services.GetRequiredService<NavigationManager>();
        SetProperty(instance, "Nav", nav);

        // Wire JS
        var js = Substitute.For<IJSRuntime>();
        SetProperty(instance, "JS", js);

        // Wire L (localizer)
        SetProperty(instance, "L", new BunitTestHelper.StubLocalizer());

        // Wire Logger
        SetProperty(instance, "Logger", NullLogger<MainLayout>.Instance);

        // Wire AuthStateProvider (unauthenticated stub)
        var authJs = Substitute.For<IJSRuntime>();
        var auth = new AuthStateProvider(authJs, NullLogger<AuthStateProvider>.Instance);
        SetProperty(instance, "Auth", auth);

        // Wire PermissionService
        var perms = new PermissionService();
        SetProperty(instance, "Permissions", perms);

        // Wire HelpService
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var help = new HelpService(http);
        SetProperty(instance, "Help", help);

        // Wire ApiClient
        var testHandler = new BunitTestHelper.TestHandler();
        // The logout tells the server first.
        testHandler.SetResponse(HttpMethod.Post, "api/auth/logout", System.Net.HttpStatusCode.NoContent);
        var apiHttp = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ApiClient(apiHttp);
        SetProperty(instance, "Api", api);

        // Wire Configuration
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:Version"] = "1.0.0" })
            .Build();
        SetProperty(instance, "Configuration", config);

        // Wire ActiveOrganizationService
        var orgs = new ActiveOrganizationService(api, new Aetheus.Front.Tests.TestDoubles.InMemoryUserPreferences());
        SetProperty(instance, "Orgs", orgs);

        // Wire the shared busy veil (R-99)
        SetProperty(instance, "Veil", Services.GetRequiredService<BusyVeil>());

        // Wire ListCacheService (cleared on sign-out / org switch)
        SetProperty(instance, "Cache", new ListCacheService(TimeProvider.System));

        // Wire IHttpClientFactory
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient { BaseAddress = new Uri("http://localhost/") });
        SetProperty(instance, "HttpFactory", httpFactory);

        SetProperty(instance, "RealtimeSession", Services.GetRequiredService<RealtimeSessionLifecycle>());

        return instance;
    }

    [Fact]
    public async Task OnLogout_ClearsPermissions()
    {
        var instance = CreateInstance();

        var perms = (PermissionService)typeof(MainLayout).GetProperty("Permissions", Priv)!.GetValue(instance)!;

        var method = typeof(MainLayout).GetMethod("OnLogout", PrivPub)!;
        await (Task)method.Invoke(instance, [])!;

        Assert.False(perms.IsLoaded);
    }

    [Fact]
    public void NavigateToHelp_WithKnownPage_NavigatesToHelpRoute()
    {
        var instance = CreateInstance();
        var nav = (NavigationManager)typeof(MainLayout).GetProperty("Nav", Priv)!.GetValue(instance)!;

        // bUnit default URI is http://localhost/ (empty path) → resolves to the "dashboard" help key,
        // so NavigateToHelp lands on a /help route rather than merely not throwing.
        var method = typeof(MainLayout).GetMethod("NavigateToHelp", PrivPub)!;
        method.Invoke(instance, []);

        Assert.Contains("help", nav.Uri);
    }

    [Fact]
    public void NavigateToHelp_WithUnknownPage_FallsBackToHelpIndex()
    {
        var instance = CreateInstance();

        // Navigate to something not in the help map
        var nav = (NavigationManager)typeof(MainLayout).GetProperty("Nav", Priv)!.GetValue(instance)!;
        nav.NavigateTo("/some-unknown-page");

        var method = typeof(MainLayout).GetMethod("NavigateToHelp", PrivPub)!;
        method.Invoke(instance, []);

        // An unresolved page key falls back to the /help index (not a /help/{key} article).
        Assert.EndsWith("/help", nav.Uri);
    }

    [Fact]
    public void OnReloadClick_ForceLoadsCurrentUri()
    {
        var instance = CreateInstance();
        var nav = (Bunit.TestDoubles.BunitNavigationManager)typeof(MainLayout).GetProperty("Nav", Priv)!.GetValue(instance)!;
        var uri = nav.Uri;

        var method = typeof(MainLayout).GetMethod("OnReloadClick", PrivPub)!;
        method.Invoke(instance, []);

        // Reload re-navigates to the current URI with forceLoad → a full-page reload of the same route.
        var last = nav.History.Last();
        Assert.Equal(uri, last.Uri);
        Assert.True(last.Options.ForceLoad);
    }

    [Fact]
    public void RedirectIfUnauthenticated_WhenOnLogin_DoesNotNavigateAgain()
    {
        var instance = CreateInstance();
        var nav = (Bunit.TestDoubles.BunitNavigationManager)typeof(MainLayout).GetProperty("Nav", Priv)!.GetValue(instance)!;
        nav.NavigateTo("/login");
        var historyCount = nav.History.Count;

        // Auth is unauthenticated but already on /login - the guard must NOT re-navigate (no loop):
        // the URI stays on login and no extra history entry is pushed.
        var method = typeof(MainLayout).GetMethod("RedirectIfUnauthenticated", Priv)!;
        method.Invoke(instance, []);

        Assert.Contains("login", nav.Uri);
        Assert.Equal(historyCount, nav.History.Count);
    }

    [Fact]
    public void RedirectIfUnauthenticated_WhenNotOnLogin_NavigatesToLogin()
    {
        var instance = CreateInstance();
        var nav = (NavigationManager)typeof(MainLayout).GetProperty("Nav", Priv)!.GetValue(instance)!;

        // bUnit navigation starts at http://localhost/ (empty path) → not "login". An unauthenticated
        // user on a non-login route must be bounced to /login.
        var method = typeof(MainLayout).GetMethod("RedirectIfUnauthenticated", Priv)!;
        method.Invoke(instance, []);

        Assert.Contains("login", nav.Uri);
    }

    [Fact]
    public async Task OnActiveOrgChanged_WithIntValue_SelectsActiveOrg()
    {
        var instance = CreateInstance();
        var orgs = (ActiveOrganizationService)typeof(MainLayout).GetProperty("Orgs", Priv)!.GetValue(instance)!;
        // Seed the available orgs so the int branch can resolve id 1 and switch the active org.
        typeof(ActiveOrganizationService).GetProperty("Available")!
            .SetValue(orgs, new List<Aetheus.Shared.Components.Organizations.MyOrganizationDto>
            {
                new(1, "Acme", "acme", Aetheus.Shared.Components.Shared.OrganizationRole.Owner)
            });

        var method = typeof(MainLayout).GetMethod("OnActiveOrgChanged", PrivPub)!;
        await (Task)method.Invoke(instance, [(object)1])!;

        // The int branch dispatches to SetActiveAsync → the active org becomes the matched id.
        Assert.Equal(1, orgs.Active?.Id);
    }

    [Fact]
    public async Task OnActiveOrgChanged_WithNonIntValue_SelectsNoOrg()
    {
        var instance = CreateInstance();
        var orgs = (ActiveOrganizationService)typeof(MainLayout).GetProperty("Orgs", Priv)!.GetValue(instance)!;
        typeof(ActiveOrganizationService).GetProperty("Available")!
            .SetValue(orgs, new List<Aetheus.Shared.Components.Organizations.MyOrganizationDto>
            {
                new(1, "Acme", "acme", Aetheus.Shared.Components.Shared.OrganizationRole.Owner)
            });

        var method = typeof(MainLayout).GetMethod("OnActiveOrgChanged", PrivPub)!;
        await (Task)method.Invoke(instance, [(object)"not-an-int"])!;

        // A non-int value short-circuits to Task.CompletedTask: no active org is selected.
        Assert.Null(orgs.Active);
    }

    [Fact]
    public void RecoverError_WithNullBoundary_LeavesItNull()
    {
        var instance = CreateInstance();
        // No ErrorBoundary has been captured yet - the `_errorBoundary?.Recover()`
        // null-conditional must short-circuit and leave the field untouched rather than NRE.
        Assert.Null(typeof(MainLayout).GetField("_errorBoundary", Priv)!.GetValue(instance));

        var method = typeof(MainLayout).GetMethod("RecoverError", PrivPub)!;
        var ex = Record.Exception(() => method.Invoke(instance, []));

        Assert.Null(ex);
        Assert.Null(typeof(MainLayout).GetField("_errorBoundary", Priv)!.GetValue(instance));
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var instance = CreateInstance();

        var ex = Record.Exception(() => instance.Dispose());
        Assert.Null(ex);
    }
}
