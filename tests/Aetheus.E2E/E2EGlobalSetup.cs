// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Aetheus.E2E;

internal static class E2EAuthSession
{
    public static string? StorageStateJson { get; set; }
}

[SetUpFixture]
public sealed class E2EGlobalSetup
{
    [OneTimeSetUp]
    public async Task ResetDatabaseAndCreateAuthenticatedState()
    {
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });

        var preserveDatabase = string.Equals(
            Environment.GetEnvironmentVariable("E2E_PRESERVE_DATABASE"), "true",
            StringComparison.OrdinalIgnoreCase);
        if (!preserveDatabase)
        {
            // Two consecutive resets prove that the E2E reset is idempotent against the current
            // migration chain. Each reset invalidates the token that authorized it, so re-authenticate.
            for (var resetAttempt = 1; resetAttempt <= 2; resetAttempt++)
            {
                var resetToken = await LoginAsync(http);
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", resetToken.Token);
                var resetResponse = await http.PostAsync($"{PlaywrightConfig.BackendUrl}/api/dev/reset-db", null);
                if (!resetResponse.IsSuccessStatusCode)
                {
                    var responseBody = await resetResponse.Content.ReadAsStringAsync();
                    throw new HttpRequestException(
                        $"E2E database reset attempt {resetAttempt} failed with HTTP {(int)resetResponse.StatusCode} "
                        + $"({resetResponse.ReasonPhrase}). Response: {responseBody}",
                        inner: null,
                        resetResponse.StatusCode);
                }
                http.DefaultRequestHeaders.Authorization = null;
            }
        }

        // A reset invalidates its authorizing token. In preserve mode there was no token yet; in both
        // cases authenticate once and share Playwright's native state with every normal fixture.
        http.DefaultRequestHeaders.Authorization = null;
        var authenticated = await LoginAsync(http);
        var localStorage = new List<object>
        {
            new { name = "aetheus_auth_token", value = authenticated.Token }
        };
        if (!string.IsNullOrWhiteSpace(authenticated.RefreshToken))
            localStorage.Add(new { name = "aetheus_refresh_token", value = authenticated.RefreshToken });

        E2EAuthSession.StorageStateJson = JsonSerializer.Serialize(new
        {
            cookies = Array.Empty<object>(),
            origins = new[]
            {
                new
                {
                    origin = new Uri(PlaywrightConfig.FrontendUrl).GetLeftPart(UriPartial.Authority),
                    localStorage
                }
            }
        });
    }

    private static async Task<(string Token, string? RefreshToken)> LoginAsync(HttpClient http)
    {
        var retryWindow = Stopwatch.StartNew();
        HttpRequestException? lastConnectionError = null;
        while (retryWindow.ElapsedMilliseconds < PlaywrightConfig.AppReadyTimeoutMs)
        {
            try
            {
                using var response = await http.PostAsJsonAsync($"{PlaywrightConfig.BackendUrl}/api/auth/login", new
                {
                    Email = PlaywrightConfig.AdminEmail,
                    Password = PlaywrightConfig.AdminPassword
                });
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                var token = body.GetProperty("token").GetString();
                ArgumentException.ThrowIfNullOrWhiteSpace(token);
                var refreshToken = body.TryGetProperty("refreshToken", out var value) ? value.GetString() : null;
                return (token, refreshToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null)
            {
                lastConnectionError = ex;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        throw new HttpRequestException(
            $"The E2E backend remained unreachable for {PlaywrightConfig.AppReadyTimeoutMs} ms.",
            lastConnectionError);
    }
}
