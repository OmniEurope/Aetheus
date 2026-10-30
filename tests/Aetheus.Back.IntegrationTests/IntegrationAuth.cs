// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Shared HTTP-login helpers for the integration suites. Before this existed, four suites
/// (DevController reset guard, ResourceAuthorization, SignalRBroadcast, ArtifactDownload) each
/// carried an identical <c>LoginAsAdminAsync</c> copy - drift bait if the login contract ever
/// changes. Centralised here so there is a single place to keep the login round-trip correct
/// (S-TECH-LGN4).
/// </summary>
internal static class IntegrationAuth
{
    /// <summary>The bootstrap-admin password the integration <see cref="AetheusWebApplicationFactory"/> seeds.</summary>
    public const string AdminPassword = AetheusWebApplicationFactory.AdminPassword;

    /// <summary>The sign-in e-mail of the seeded admin: the factory runs in Development, whose
    /// appsettings.Development.json names it (Auth:AdminEmail).</summary>
    public const string AdminEmail = "admin@aetheus.local";

    /// <summary>Logs in by e-mail, as everyone does (a username is never a sign-in), and returns the JWT,
    /// throwing if the login does not succeed.</summary>
    public static async Task<string> LoginByEmailAsync(
        HttpClient client,
        string email,
        string password,
        CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password
        }, ct);
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default, ct);
        return login!.Token;
    }

    /// <summary>Logs in as the seeded bootstrap admin, by its e-mail, and returns the JWT.</summary>
    public static Task<string> LoginAsAdminAsync(HttpClient client, CancellationToken ct)
        => LoginByEmailAsync(client, AdminEmail, AdminPassword, ct);

    /// <summary>Sets (or clears, when <paramref name="token"/> is null) the Bearer header on a client.</summary>
    public static void SetBearer(HttpClient client, string? token)
        => client.DefaultRequestHeaders.Authorization =
            token is null ? null : new AuthenticationHeaderValue("Bearer", token);
}
