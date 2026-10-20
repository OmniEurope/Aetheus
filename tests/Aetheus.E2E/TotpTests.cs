// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Playwright;

namespace Aetheus.E2E;

[Category("E2E")]
[Category("Security")]
[RollbackRelevant]
public class TotpTests : E2ETestBase
{
    [SetUp]
    public async Task SetUp() => await LoginAsync();

    [Test]
    public async Task Settings_SecurityTab_ShowsTotpSetup()
    {
        await NavigateToAsync("settings");
        await Page.WaitForSelectorAsync(".omni-tabs", new() { Timeout = 10000 });
        var securityTab = Page.GetByRole(AriaRole.Tab, new() { Name = "Security", Exact = true });
        await Expect(securityTab).ToBeVisibleAsync();
        await securityTab.ClickAsync();
        // The Security tab renders the "Two-Factor Authentication" section (heading + Enable/Disable 2FA).
        // (The previous selector "text=Two-Factor, text=2FA, text=TOTP" was a malformed Playwright union
        // selector - it searched for that whole literal string and never matched.)
        await Expect(Page.GetByText("Two-Factor Authentication").First).ToBeVisibleAsync(new() { Timeout = 5000 });
    }

    [Test]
    public async Task Login_RecoveryCodeMode_CompletesRealBrowserLogin()
    {
        var username = $"totp-browser-{Guid.NewGuid():N}";
        const string password = "Totp-Browser-2026!";
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });

        var adminLogin = await http.PostAsJsonAsync($"{BackendUrl}/api/auth/login", new
        {
            Username = AdminUser,
            Password = AdminPassword
        });
        adminLogin.EnsureSuccessStatusCode();
        var adminBody = await adminLogin.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", adminBody.GetProperty("token").GetString());
        var createUser = await http.PostAsJsonAsync($"{BackendUrl}/api/users", new
        {
            Username = username,
            Password = password,
            MustChangePassword = false,
            Roles = new[] { "Reader" }
        });
        createUser.EnsureSuccessStatusCode();

        http.DefaultRequestHeaders.Authorization = null;
        var userLogin = await http.PostAsJsonAsync($"{BackendUrl}/api/auth/login", new
        {
            Username = username,
            Password = password
        });
        userLogin.EnsureSuccessStatusCode();
        var userBody = await userLogin.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", userBody.GetProperty("token").GetString());
        var setupResponse = await http.PostAsync($"{BackendUrl}/api/auth/totp/setup", null);
        setupResponse.EnsureSuccessStatusCode();
        var setup = await setupResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sharedKey = setup.GetProperty("sharedKey").GetString()!;
        var recoveryCode = setup.GetProperty("recoveryCodes")[0].GetString()!;
        var verify = await http.PostAsJsonAsync($"{BackendUrl}/api/auth/totp/verify", new
        {
            Code = ComputeTotp(sharedKey)
        });
        verify.EnsureSuccessStatusCode();

        await Page.AddInitScriptAsync("localStorage.clear()");
        await Page.GotoAsync($"{FrontendUrl}/login", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Page.FillAsync("#Username", username);
        await Page.FillAsync("#Password", password);
        await Page.ClickAsync("button[type='submit']");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Use a recovery code" }))
            .ToBeVisibleAsync();

        await Page.GetByRole(AriaRole.Button, new() { Name = "Use a recovery code" }).ClickAsync();
        await Expect(Page.Locator("#oe-pages-auth-login-3")).ToBeVisibleAsync();
        await Page.FillAsync("#oe-pages-auth-login-3", recoveryCode);
        await Page.ClickAsync("button[type='submit']");

        await Page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
        await Expect(Page.Locator("[data-testid='blazor-ready']")).ToBeVisibleAsync();
    }

    private static string ComputeTotp(string base32Secret)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = base32Secret.TrimEnd('=').ToUpperInvariant()
            .Select(character => alphabet.IndexOf(character))
            .Aggregate(new List<bool>(), (result, value) =>
            {
                if (value < 0) throw new InvalidDataException("Invalid base32 TOTP secret.");
                for (var bit = 4; bit >= 0; bit--) result.Add((value & (1 << bit)) != 0);
                return result;
            });
        var secret = Enumerable.Range(0, bits.Count / 8)
            .Select(index => (byte)Enumerable.Range(0, 8)
                .Aggregate(0, (value, bit) => (value << 1) | (bits[index * 8 + bit] ? 1 : 0)))
            .ToArray();
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        // CA5350 flags SHA-1, correctly in general. TOTP is the exception: RFC 6238 defines the
        // default algorithm as HMAC-SHA-1, and every authenticator app implements that. Using
        // anything else here would compute codes no real client would accept, so the test would pass
        // against an implementation that is wrong.
#pragma warning disable CA5350 // Do Not Use Weak Cryptographic Algorithms
        var hash = HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }
}
