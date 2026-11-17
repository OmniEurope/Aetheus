// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.PersonalAccessTokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.PackageRegistry;

internal sealed class PackageRegistryAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IPersonalAccessTokenService personalAccessTokens,
    PackageRunTokens runTokens,
    IPackageRegistryRepository repository)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PackageRegistry";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken();
        if (token is null)
            return AuthenticateResult.NoResult();
        if (PackageRunTokens.IsRunToken(token))
            return await AuthenticateRunTokenAsync(token).ConfigureAwait(false);
        if (!token.StartsWith(PatConstants.TokenPrefix, StringComparison.Ordinal))
            return AuthenticateResult.Fail(
                "The package registry requires an Aetheus personal access token or a pipeline run token.");

        var principal = await personalAccessTokens.ValidateAsync(token, Context.RequestAborted).ConfigureAwait(false);
        if (principal is null)
            return AuthenticateResult.Fail("Invalid or expired personal access token.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, principal.Username),
            new(ClaimTypes.NameIdentifier, principal.UserId.ToString()),
            new(PatConstants.ScopeClaimType,
                principal.Scope == PatScope.ReadOnly
                    ? PatConstants.ReadOnlyScopeValue
                    : PatConstants.ReadWriteScopeValue),
            new(PatConstants.TokenIdClaimType, principal.TokenId.ToString())
        };
        claims.AddRange(principal.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <summary>A pipeline run's registry credential (<see cref="PackageRunTokens"/>): authentic, unexpired, its run
    /// still <c>Running</c> in that project and its pipeline owner still an active Admin (fail closed: a database fault is a 500,
    /// never an acceptance). The principal carries no role, so it reads what any authenticated caller reads and
    /// publishes only through <see cref="PackageRegistryPolicies.Publish"/>.</summary>
    private async Task<AuthenticateResult> AuthenticateRunTokenAsync(string token)
    {
        if (runTokens.Validate(token) is not var (runId, projectId)
            || !await repository.IsRunAuthorizedAsync(runId, projectId, Context.RequestAborted).ConfigureAwait(false))
            return AuthenticateResult.Fail("Invalid, expired or finished pipeline run token.");

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, PackageRunTokens.Describe(runId)),
            new Claim(PackageRunTokens.RunClaim, runId.ToString(CultureInfo.InvariantCulture)),
            new Claim(PackageRunTokens.ProjectClaim, projectId.ToString(CultureInfo.InvariantCulture))
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Basic realm=\"Aetheus Package Registry\", Bearer";
        return Task.CompletedTask;
    }

    private string? ReadToken()
    {
        var apiKey = Request.Headers["X-NuGet-ApiKey"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(apiKey)
            && (apiKey.Trim().StartsWith(PatConstants.TokenPrefix, StringComparison.Ordinal)
                || PackageRunTokens.IsRunToken(apiKey.Trim())))
            return apiKey.Trim();

        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization["Bearer ".Length..].Trim();
        if (!authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return null;

        var encoded = authorization["Basic ".Length..].Trim();
        if (encoded.Length is < 1 or > 16_384)
            return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var separator = decoded.IndexOf(':');
            return separator >= 0 ? decoded[(separator + 1)..] : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
