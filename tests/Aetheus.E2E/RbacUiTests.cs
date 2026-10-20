// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;

namespace Aetheus.E2E;

[Category("E2E")]
[Category("Rbac")]
[RollbackRelevant]
public class RbacUiTests : E2ETestBase
{
    [Test]
    public async Task AdminMenuAndUserManagement_AreAccessibleForAdmin()
    {
        await LoginAsync();
        await NavigateToAsync("");
        await Page.WaitForSelectorAsync(".omni-panel-menu__link", new() { Timeout = 10000 });
        var settingsLink = Page.Locator("a[href*='settings']").First;
        await Expect(settingsLink).ToBeVisibleAsync();

        await NavigateToAsync("users");
        await Page.WaitForSelectorAsync(".omni-data-grid", new() { Timeout = 10000 });
        await Expect(Page.Locator(".omni-data-grid")).ToBeVisibleAsync();
    }

    [Test]
    public async Task UserWithoutRole_SeesEmptyDashboardRestrictedMenuAndDeniedDirectRoute()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var username = $"no-rights-{suffix}";
        const string password = "NoRights-2026!";

        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
        var adminLogin = await http.PostAsJsonAsync($"{BackendUrl}/api/auth/login",
            new { Username = AdminUser, Password = AdminPassword });
        adminLogin.EnsureSuccessStatusCode();
        var adminBody = await adminLogin.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", adminBody.GetProperty("token").GetString());

        var create = await http.PostAsJsonAsync($"{BackendUrl}/api/users", new
        {
            Username = username,
            Password = password,
            MustChangePassword = false,
            Roles = Array.Empty<string>()
        });
        create.EnsureSuccessStatusCode();

        await LoginAsAsync(username, password);

        await Expect(Page.GetByText("Access denied.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(SidebarNavItem("Servers")).ToHaveCountAsync(0);
        await Expect(SidebarNavItem("Projects")).ToHaveCountAsync(0);
        await Expect(SidebarNavItem("Administration")).ToHaveCountAsync(0);

        await Page.GotoAsync($"{FrontendUrl}/servers");
        await Page.WaitForSelectorAsync("[data-testid='blazor-ready']");
        await Expect(Page.GetByText("Access denied.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Page.Locator(".omni-data-grid")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task AuthorizedNonAdmin_SeesGrantedSectionButNotAdministration()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var username = $"server-reader-{suffix}";
        var roleName = $"ServerReader-{suffix}";
        const string password = "ServerReader-2026!";

        using var http = await CreateAdminClientAsync();
        var createRole = await http.PostAsJsonAsync($"{BackendUrl}/api/roles", new
        {
            Name = roleName,
            Description = "E2E server reader"
        });
        createRole.EnsureSuccessStatusCode();
        var role = await createRole.Content.ReadFromJsonAsync<JsonElement>();
        var roleId = role.GetProperty("id").GetInt32();

        var setPermissions = await http.PutAsJsonAsync($"{BackendUrl}/api/roles/{roleId}/permissions", new
        {
            Permissions = new[] { new { ResourceType = 3, ResourceId = (int?)null, Permission = 0 } }
        });
        setPermissions.EnsureSuccessStatusCode();

        var createUser = await http.PostAsJsonAsync($"{BackendUrl}/api/users", new
        {
            Username = username,
            Password = password,
            MustChangePassword = false,
            Roles = new[] { roleName }
        });
        createUser.EnsureSuccessStatusCode();

        await LoginAsAsync(username, password);

        await Expect(SidebarNavItem("Servers")).ToBeVisibleAsync();
        await Expect(SidebarNavItem("Projects")).ToHaveCountAsync(0);
        await Expect(SidebarNavItem("Administration")).ToHaveCountAsync(0);

        await Page.GotoAsync($"{FrontendUrl}/servers");
        await Page.WaitForSelectorAsync("[data-testid='blazor-ready']");
        await Expect(Page.GetByText("Access denied.", new() { Exact = true })).ToHaveCountAsync(0);
        await Expect(Page.Locator(".omni-data-grid")).ToBeVisibleAsync();

        foreach (var viewport in new[] { (Width: 390, Height: 812), (Width: 812, Height: 390) })
        {
            await Page.SetViewportSizeAsync(viewport.Width, viewport.Height);
            await Page.GotoAsync($"{FrontendUrl}/servers");
            await Page.WaitForSelectorAsync("[data-testid='blazor-ready']");
            var widths = await Page.EvaluateAsync<int[]>(
                "() => [document.documentElement.scrollWidth, window.innerWidth]");
            Assert.That(
                widths[0],
                Is.LessThanOrEqualTo(widths[1]),
                $"The non-admin server-reader view must fit {viewport.Width}x{viewport.Height}.");
            await Expect(Page.GetByText("Access denied.", new() { Exact = true })).ToHaveCountAsync(0);
            await Expect(Page.Locator(".omni-data-grid")).ToBeVisibleAsync();
        }
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
        var adminLogin = await http.PostAsJsonAsync($"{BackendUrl}/api/auth/login",
            new { Username = AdminUser, Password = AdminPassword });
        adminLogin.EnsureSuccessStatusCode();
        var adminBody = await adminLogin.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", adminBody.GetProperty("token").GetString());
        return http;
    }

    private async Task LoginAsAsync(string username, string password)
    {
        // Waits for the initial WASM bootstrap before clearing authentication state so reload
        // cannot cancel assembly downloads.
        await Page.GotoAsync(FrontendUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Page.WaitForSelectorAsync("[data-testid='blazor-ready']", new() { Timeout = PlaywrightConfig.AppReadyTimeoutMs });
        await Page.EvaluateAsync("localStorage.clear()");
        await Page.GotoAsync($"{FrontendUrl}/login", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Page.WaitForSelectorAsync("#Username", new() { Timeout = PlaywrightConfig.AppReadyTimeoutMs });
        await Page.FillAsync("#Username", username);
        await Page.FillAsync("#Password", password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync($"{FrontendUrl}/");
        await Page.WaitForSelectorAsync("[data-testid='blazor-ready']");
    }
}
