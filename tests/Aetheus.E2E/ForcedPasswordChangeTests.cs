// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>Recette R-38: a user whose password must be changed signs in and gets the change form, not a
/// session loader that never ends (the authenticated session only starts after the change).</summary>
[Category("E2E")]
[Category("Auth")]
[RollbackRelevant]
public class ForcedPasswordChangeTests : E2ETestBase
{
    [Test]
    public async Task UserWhoMustChangePassword_SeesTheChangeFormAfterSignIn()
    {
        var username = $"must-change-{Guid.NewGuid().ToString("N")[..8]}";
        const string password = "MustChange-2026!";

        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
        var adminLogin = await http.PostAsJsonAsync($"{BackendUrl}/api/auth/login",
            new { Email = AdminEmail, Password = AdminPassword });
        adminLogin.EnsureSuccessStatusCode();
        var adminBody = await adminLogin.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", adminBody.GetProperty("token").GetString());
        await CreateUserWithPasswordAsync(http, new
        {
            Username = username,
            Email = $"{username}@example.com",
            Roles = Array.Empty<string>()
        }, $"{username}@example.com", password, mustChangePassword: true);

        await Page.GotoAsync(FrontendUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Page.WaitForSelectorAsync("[data-testid='blazor-ready']", new() { Timeout = PlaywrightConfig.AppReadyTimeoutMs });
        await Page.EvaluateAsync("localStorage.clear()");
        await Page.GotoAsync($"{FrontendUrl}/login", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Page.WaitForSelectorAsync("#Email", new() { Timeout = PlaywrightConfig.AppReadyTimeoutMs });
        await Page.FillAsync("#Email", $"{username}@example.com");
        await Page.FillAsync("#Password", password);
        await Page.ClickAsync("button[type='submit']");

        await Page.WaitForURLAsync($"{FrontendUrl}/account/change-password");
        await Expect(Page.Locator("#NewPassword")).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(Page.Locator(".session-bootstrap-loader")).ToHaveCountAsync(0);
    }
}
