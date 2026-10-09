// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Reflection;
using Aetheus.Front.Layout;
using Aetheus.Front.Tests.Services;
using Aetheus.Shared.Components.Organizations;
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Method-level coverage for MainLayout.razor.cs - targets the methods that currently have
/// zero coverage: LoadPermissionsAsync, LoadOrganizationsAsync, CheckForNewVersionAsync,
/// ToggleLanguage, OnNeedsLogin, OnSettingsMenuClick, OnLogoutMenuClick.
///
/// Uses rendering (the layout is renderable via BunitContext) plus reflection-based invocation
/// for methods that are private or hard to trigger through UI events alone.
/// </summary>
public class MainLayoutMethodCoverageTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type LayoutType = typeof(MainLayout);
    private readonly BunitTestHelper.TestHandler _handler;

    public MainLayoutMethodCoverageTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    // The start-up loads now go through MainLayoutBootstrapLoader, the one the layout calls itself.
    private Task LoadPermissionsAsync() => MainLayoutBootstrapLoader.LoadPermissionsAsync(
        Services.GetRequiredService<ApiClient>(), Services.GetRequiredService<PermissionService>(),
        Services.GetRequiredService<AuthStateProvider>(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    private Task LoadOrganizationsAsync() => MainLayoutBootstrapLoader.LoadOrganizationsAsync(
        Services.GetRequiredService<ActiveOrganizationService>(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    // ── LoadPermissionsAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task LoadPermissionsAsync_SuccessfulResponse_SetsPermissions()
    {
        var summary = new UserPermissionSummaryDto
        {
            UserId = 1,
            Username = "admin",
            Roles = ["Admin"],
            EffectivePermissions = Enum.GetValues<ResourceType>()
                .SelectMany(rt => new[]
                {
                    new EffectivePermissionDto { ResourceType = rt, ResourceId = null, Permission = Permission.Read },
                    new EffectivePermissionDto { ResourceType = rt, ResourceId = null, Permission = Permission.Write }
                })
                .ToList()
        };
        _handler.SetJsonResponse("api/users/me/permissions", summary);

        Render<MainLayout>();
        _handler.Requests.Clear();
        await LoadPermissionsAsync();

        // LoadPermissionsAsync fetches the user's effective permissions and feeds them to PermissionService.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/users/me/permissions"));
        Assert.True(Services.GetRequiredService<PermissionService>().IsLoaded);
    }

    [Fact]
    public async Task LoadPermissionsAsync_HttpError_StillCallsTheApi()
    {
        _handler.SetResponse("api/users/me/permissions", HttpStatusCode.InternalServerError);

        Render<MainLayout>();
        _handler.Requests.Clear();

        // Should swallow the exception (logs a warning and returns) after attempting the fetch.
        await LoadPermissionsAsync();
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/users/me/permissions"));
    }

    [Fact]
    public async Task LoadPermissionsAsync_NullResponse_DoesNotSetPermissions()
    {
        // Return a JSON `null` body so GetMyPermissionsAsync genuinely returns null (a 204/{} body
        // would deserialize to a non-null DTO and wrongly trip SetPermissions).
        _handler.SetJsonResponse<UserPermissionSummaryDto?>("api/users/me/permissions", null);

        Render<MainLayout>();
        _handler.Requests.Clear();
        // A null permissions payload must NOT re-trigger SetPermissions (which raises OnPermissionsChanged).
        var permissions = Services.GetRequiredService<PermissionService>();
        var changedFired = false;
        permissions.OnPermissionsChanged += () => changedFired = true;

        await LoadPermissionsAsync();

        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/users/me/permissions"));
        Assert.False(changedFired);
    }

    // ── LoadOrganizationsAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task LoadOrganizationsAsync_SuccessfulResponse_CompletesWithoutThrow()
    {
        // GetMyOrganizationsAsync endpoint
        _handler.SetJsonResponse("api/organizations/me", new List<object>());

        Render<MainLayout>();
        _handler.Requests.Clear();
        await LoadOrganizationsAsync();

        // LoadOrganizationsAsync delegates to ActiveOrganizationService.RefreshAsync → GET api/organizations/me.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/organizations/me"));
    }

    [Fact]
    public async Task LoadOrganizationsAsync_HttpError_StillCallsTheApi()
    {
        _handler.SetResponse("api/organizations/me", HttpStatusCode.InternalServerError);

        Render<MainLayout>();
        _handler.Requests.Clear();
        // Swallows the 500 after attempting the fetch.
        await LoadOrganizationsAsync();
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/organizations/me"));
    }

    // ── CheckForNewVersionAsync ────────────────────────────────────────────────

    [Fact]
    public async Task CheckForNewVersionAsync_SameVersion_ReturnsFalse()
    {
        // The method calls GetStringAsync on appsettings.json. We stub the HttpClient
        // the factory creates (BunitTestHelper already wires a mock factory returning a
        // client backed by handler). We stub "appsettings.json" in the handler so the
        // client returns matching version content.
        var cut = Render<MainLayout>();

        // Read the current _version field (defaults to "dev")
        var currentVersion = (string)LayoutType.GetField("_version", Priv)!.GetValue(cut.Instance)!;

        // Build an HttpClient that will return a JSON with the same version
        var appSettingsJson = $"{{\"App\":{{\"Version\":\"{currentVersion}\"}}}}";
        var fakeClient = new HttpClient(new StaticResponseHandler(appSettingsJson))
        {
            BaseAddress = new Uri("http://test/")
        };

        var method = LayoutType.GetMethod("CheckForNewVersionAsync", Priv)!;
        var result = await (Task<bool>)method.Invoke(cut.Instance, [fakeClient, CancellationToken.None])!;

        Assert.False(result);
        Assert.False(cut.Instance._newVersionAvailable);
    }

    [Fact]
    public async Task CheckForNewVersionAsync_DifferentVersion_SetsNewVersionAvailableTrue()
    {
        var cut = Render<MainLayout>();

        // Force a known _version value
        LayoutType.GetField("_version", Priv)!.SetValue(cut.Instance, "1.0.0");

        var appSettingsJson = "{\"App\":{\"Version\":\"2.0.0\"}}";
        var fakeClient = new HttpClient(new StaticResponseHandler(appSettingsJson))
        {
            BaseAddress = new Uri("http://test/")
        };

        var method = LayoutType.GetMethod("CheckForNewVersionAsync", Priv)!;
        // InvokeAsync so InvokeAsync(StateHasChanged) inside the method stays on the renderer thread
        var result = await cut.InvokeAsync(async () =>
            await (Task<bool>)method.Invoke(cut.Instance, [fakeClient, CancellationToken.None])!);

        Assert.True(result);
        Assert.True(cut.Instance._newVersionAvailable);
    }

    [Fact]
    public async Task CheckForNewVersionAsync_DifferentVersion_AnnouncesTheBackendWasReplacedOnce()
    {
        // Run 2458: the reload banner is the one moment the front knows a blue-green switch happened, and
        // the open sockets are still on the previous colour. The realtime owners must be told once.
        var cut = Render<MainLayout>();
        var announced = 0;
        Services.GetRequiredService<HubConnectionFactory>().BackendReplaced += () => announced++;
        LayoutType.GetField("_version", Priv)!.SetValue(cut.Instance, "1.0.0");
        var fakeClient = new HttpClient(new StaticResponseHandler("{\"App\":{\"Version\":\"2.0.0\"}}"))
        {
            BaseAddress = new Uri("http://test/")
        };

        var method = LayoutType.GetMethod("CheckForNewVersionAsync", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task<bool>)method.Invoke(cut.Instance, [fakeClient, CancellationToken.None])!);

        Assert.Equal(1, announced);
    }

    [Fact]
    public async Task CheckForNewVersionAsync_SameVersion_AnnouncesNothing()
    {
        var cut = Render<MainLayout>();
        var announced = 0;
        Services.GetRequiredService<HubConnectionFactory>().BackendReplaced += () => announced++;
        LayoutType.GetField("_version", Priv)!.SetValue(cut.Instance, "1.0.0");
        var fakeClient = new HttpClient(new StaticResponseHandler("{\"App\":{\"Version\":\"1.0.0\"}}"))
        {
            BaseAddress = new Uri("http://test/")
        };

        var method = LayoutType.GetMethod("CheckForNewVersionAsync", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task<bool>)method.Invoke(cut.Instance, [fakeClient, CancellationToken.None])!);

        Assert.Equal(0, announced);
    }

    [Fact]
    public async Task CheckForNewVersionAsync_MissingAppSection_ReturnsFalse()
    {
        var cut = Render<MainLayout>();
        LayoutType.GetField("_version", Priv)!.SetValue(cut.Instance, "1.0.0");

        // JSON without "App" section
        var appSettingsJson = "{\"OtherSection\":{\"Key\":\"value\"}}";
        var fakeClient = new HttpClient(new StaticResponseHandler(appSettingsJson))
        {
            BaseAddress = new Uri("http://test/")
        };

        var method = LayoutType.GetMethod("CheckForNewVersionAsync", Priv)!;
        var result = await (Task<bool>)method.Invoke(cut.Instance, [fakeClient, CancellationToken.None])!;

        Assert.False(result);
    }

    [Fact]
    public async Task CheckForNewVersionAsync_EmptyRemoteVersion_ReturnsFalse()
    {
        var cut = Render<MainLayout>();
        LayoutType.GetField("_version", Priv)!.SetValue(cut.Instance, "1.0.0");

        // Version key exists but is empty
        var appSettingsJson = "{\"App\":{\"Version\":\"\"}}";
        var fakeClient = new HttpClient(new StaticResponseHandler(appSettingsJson))
        {
            BaseAddress = new Uri("http://test/")
        };

        var method = LayoutType.GetMethod("CheckForNewVersionAsync", Priv)!;
        var result = await (Task<bool>)method.Invoke(cut.Instance, [fakeClient, CancellationToken.None])!;

        Assert.False(result);
    }

    // ── OnLanguageChanged (the application menu's language row) ──────────────

    [Fact]
    public async Task OnLanguageChanged_StoresTheLanguageAndReloadsInIt()
    {
        var cut = Render<MainLayout>();
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var other = MainLayout.CurrentLanguage == "en" ? "fr-FR" : "en";

        await cut.InvokeAsync(() => cut.Instance.OnLanguageChanged(other));

        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "localStorage.setItem"
            && Equals(i.Arguments[0], StorageKeys.Lang) && Equals(i.Arguments[1], other));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "Aetheus.setLang" && Equals(i.Arguments[0], other));
        Assert.Contains(nav.History, entry => entry.Options.ForceLoad);
    }

    [Fact]
    public async Task OnLanguageChanged_SameLanguage_ChangesNothing()
    {
        var cut = Render<MainLayout>();

        await cut.InvokeAsync(() => cut.Instance.OnLanguageChanged(MainLayout.CurrentLanguage));

        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "Aetheus.setLang");
    }

    // ── OnNeedsLogin ──────────────────────────────────────────────────────────

    [Fact]
    public async Task OnNeedsLogin_WhenNotOnLoginPage_Triggers()
    {
        UseCountingHubFactory();
        var cut = Render<MainLayout>();
        var factory = (CountingHubConnectionFactory)Services.GetRequiredService<HubConnectionFactory>();
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var permissions = Services.GetRequiredService<PermissionService>();
        var cache = Services.GetRequiredService<ListCacheService>();
        permissions.SetPermissions(
            [new EffectivePermissionDto { ResourceType = ResourceType.Project, Permission = Permission.Read }],
            isAdmin: false);
        cache.TryStore(cache.Begin(), "dashboard:overview", new DashboardOverviewDto { TotalServers = 1 });
        // PLAN-005 lot 8 / D47: a session that expires in the middle of a page keeps that page.
        nav.NavigateTo("http://localhost/servers/3?tab=docker");
        // PLAN-005 lot 9 / D48: the session ended for a reason, which the layout reports.
        await cut.InvokeAsync(() => Services.GetRequiredService<AuthStateProvider>().EndSessionAsync(Aetheus.Shared.Components.Auth.RefreshRejectionCodes.Replay));

        var method = LayoutType.GetMethod("OnNeedsLogin", Priv)!;
        // Await the dispatch so the assertion runs after OnNeedsLogin's internal InvokeAsync completes.
        await cut.InvokeAsync(() =>
        {
            method.Invoke(cut.Instance, []);
            return Task.CompletedTask;
        });

        // Not on the login page, so the 401 funnel redirects to /login.
        Assert.Equal("http://localhost/login?returnUrl=%2Fservers%2F3%3Ftab%3Ddocker", nav.Uri);
        cut.WaitForAssertion(() => Assert.Equal(1, factory.StopAllCount));
        Assert.False(permissions.IsLoaded);
        Assert.False(cache.TryGet<DashboardOverviewDto>("dashboard:overview", out _));
        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request =>
            request.Method == "POST" && request.Url.EndsWith("api/auth/session-ended", StringComparison.Ordinal)
            && request.Body!.Contains("refresh_replay", StringComparison.Ordinal)));
    }

    // ── OnSettingsMenuClick ───────────────────────────────────────────────────

    [Fact]
    public async Task OnSettingsMenuClick_NavigatesToSettings()
    {
        var cut = Render<MainLayout>();
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        var method = LayoutType.GetMethod("OnSettingsMenuClick", Priv)!;
        await cut.InvokeAsync(() =>
        {
            method.Invoke(cut.Instance, []);
            return Task.CompletedTask;
        });

        // OmniAppMenu closes itself before raising OnSettings. Assert on the navigation history (not the
        // final Uri): MainLayout's unauthenticated-redirect guard can bounce the final location to /login
        // after the settings navigation, but the /settings navigation still happened.
        Assert.Contains(nav.History, h => h.Uri.EndsWith("/settings"));
    }

    private void UseCountingHubFactory() =>
        Services.AddSingleton<HubConnectionFactory>(services => new CountingHubConnectionFactory(
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<AuthStateProvider>()));

    // ── OnActiveOrgChanged ────────────────────────────────────────────────────

    [Fact]
    public async Task OnActiveOrgChanged_WithIntValue_CallsSetActive()
    {
        var cut = Render<MainLayout>();
        // Seed the available orgs so SetActiveAsync(42) can resolve a match and switch the active org.
        var orgs = Services.GetRequiredService<ActiveOrganizationService>();
        typeof(ActiveOrganizationService).GetProperty("Available")!
            .SetValue(orgs, new List<MyOrganizationDto>
            {
                new(42, "Acme", "acme", OrganizationRole.Owner)
            });

        // OnActiveOrgChanged is internal; accessible via InternalsVisibleTo
        await cut.InvokeAsync(async () => await cut.Instance.OnActiveOrgChanged(42));

        // The int branch dispatches to SetActiveAsync, which switches the active org to the matched id.
        Assert.Equal(42, orgs.Active?.Id);
    }

    [Fact]
    public async Task OnActiveOrgChanged_WithNonIntValue_ReturnsCompletedTask()
    {
        var cut = Render<MainLayout>();
        var orgs = Services.GetRequiredService<ActiveOrganizationService>();
        typeof(ActiveOrganizationService).GetProperty("Available")!
            .SetValue(orgs, new List<MyOrganizationDto>
            {
                new(42, "Acme", "acme", OrganizationRole.Owner)
            });

        await cut.InvokeAsync(async () => await cut.Instance.OnActiveOrgChanged("not-an-int"));

        // A non-int value short-circuits to Task.CompletedTask: no active org is selected.
        Assert.Null(orgs.Active);
    }

    // ── StaticResponseHandler helper ─────────────────────────────────────────

    /// <summary>Minimal HttpMessageHandler that always returns the given string body as 200 OK.</summary>
    private sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
