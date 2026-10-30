// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using OtpNet;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// End-to-end exercise of the TOTP 2FA flow (F-010) through the real HTTP pipeline.
/// <para>
/// The lifecycle is: authenticated user → <c>POST totp/setup</c> (receives shared key +
/// recovery codes) → <c>POST totp/verify</c> (verifies a real TOTP code, enabling 2FA) →
/// subsequent <c>POST login</c> returns <c>TotpRequired = true</c> if no code is provided,
/// and succeeds only when a valid TOTP code is included. The InMemory suite mocks the
/// <c>ITotpService</c> entirely; these tests run the real <c>TotpService</c> with real
/// encryption and real BCrypt-hashed recovery codes against PostgreSQL.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TotpIntegrationTests(PostgresFixture fixture)
{
    private const string AdminPassword = "Integr@tion-Test-Admin-Pwd-2026";

    private static async Task<LoginResponse> LoginAsync(HttpClient client, string email, string password, string? totpCode = null)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password,
            TotpCode = totpCode
        });
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default);
        return login!;
    }

    private static void SetBearer(HttpClient client, string token)
        => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    /// <summary>
    /// Generates a valid TOTP code from the base32 shared key returned by the setup endpoint.
    /// Uses the same OtpNet library as the server.
    /// </summary>
    private static string GenerateTotpCode(string base32SharedKey, DateTime? timestamp = null)
    {
        var secretBytes = Base32Encoding.ToBytes(base32SharedKey);
        var totp = new Totp(secretBytes, step: 30, totpSize: 6);
        return timestamp is null ? totp.ComputeTotp() : totp.ComputeTotp(timestamp.Value);
    }

    [Fact]
    public async Task TotpLifecycle_RequiresValidCode_AndAcceptsRecoveryCode()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        // Create a fresh user (don't pollute the admin account with TOTP)
        var adminLogin = await LoginAsync(client, IntegrationAuth.AdminEmail, AdminPassword);
        SetBearer(client, adminLogin.Token);

        var username = $"totp-user-{Guid.NewGuid():N}";
        var password = "Totp-Test-Pwd-2026!";
        var createResp = await client.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            Username = username,
            Email = $"{username}@example.com",
            OrganizationRoles = await IntegrationUsers.InDefaultOrganizationAsync(client, "Reader")
        }, cancellationToken: TestContext.Current.CancellationToken);
        createResp.EnsureSuccessStatusCode();
        // D1: a flagged account only reaches the password change; this one tests TOTP.
        var createdId = (await createResp.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, TestContext.Current.CancellationToken))!.Id;
        await IntegrationUsers.SetPasswordAsync(factory.Services, createdId, password);

        // Login as the new user
        client.DefaultRequestHeaders.Authorization = null;
        var userLogin = await LoginAsync(client, $"{username}@example.com", password);
        SetBearer(client, userLogin.Token);

        // Step 1: Setup TOTP - get the shared key
        var setupResp = await client.PostAsync("/api/auth/totp/setup", null, cancellationToken: TestContext.Current.CancellationToken);
        setupResp.EnsureSuccessStatusCode();
        var setup = await setupResp.Content.ReadFromJsonAsync<TotpSetupResponse>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(setup);
        Assert.False(string.IsNullOrEmpty(setup!.SharedKey));
        Assert.False(string.IsNullOrEmpty(setup.AuthenticatorUri));
        Assert.NotEmpty(setup.RecoveryCodes);

        // Step 2: Verify TOTP with a real code generated from the shared key
        var code = GenerateTotpCode(setup.SharedKey);
        var verifyResp = await client.PostAsJsonAsync("/api/auth/totp/verify", new TotpVerifyRequest
        {
            Code = code
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);

        // Step 3: Login WITHOUT TOTP code - should get TotpRequired = true
        client.DefaultRequestHeaders.Authorization = null;
        var noCodeResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = $"{username}@example.com",
            Password = password
        }, cancellationToken: TestContext.Current.CancellationToken);
        noCodeResp.EnsureSuccessStatusCode();
        var noCodeLogin = await noCodeResp.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(noCodeLogin!.TotpRequired);
        // When TotpRequired is true, the token should be empty (no access granted)
        Assert.True(string.IsNullOrEmpty(noCodeLogin.Token));

        // Step 4: Login with an invalid TOTP code must be rejected.
        var badCodeResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = $"{username}@example.com",
            Password = password,
            TotpCode = "0000000"
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, badCodeResp.StatusCode);

        // A syntactically valid code well outside the ±1-step acceptance window must also fail.
        var expiredCodeResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = $"{username}@example.com",
            Password = password,
            TotpCode = GenerateTotpCode(setup.SharedKey, DateTime.UtcNow.AddMinutes(-2))
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredCodeResp.StatusCode);

        // Step 5: Login WITH valid TOTP code - should succeed.
        var validCode = GenerateTotpCode(setup.SharedKey);
        var withCodeResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = $"{username}@example.com",
            Password = password,
            TotpCode = validCode
        }, cancellationToken: TestContext.Current.CancellationToken);
        withCodeResp.EnsureSuccessStatusCode();
        var withCodeLogin = await withCodeResp.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(withCodeLogin!.TotpRequired);
        Assert.False(string.IsNullOrEmpty(withCodeLogin.Token));

        // Step 6: Login using a recovery code instead of TOTP.
        var recoveryCode = setup.RecoveryCodes[0];
        var recoveryResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = $"{username}@example.com",
            Password = password,
            RecoveryCode = recoveryCode
        }, cancellationToken: TestContext.Current.CancellationToken);
        recoveryResp.EnsureSuccessStatusCode();
        var recoveryLogin = await recoveryResp.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(recoveryLogin!.TotpRequired);
        Assert.False(string.IsNullOrEmpty(recoveryLogin.Token));
    }
}
