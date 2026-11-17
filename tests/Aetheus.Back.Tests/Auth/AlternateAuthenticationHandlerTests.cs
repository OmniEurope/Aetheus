// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.PackageRegistry;
using Aetheus.Back.Components.PersonalAccessTokens;
using Aetheus.Back.Components.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.Tests.Auth;

/// <summary>
/// A360-01. The two alternate authentication handlers were at 0 % coverage: no unit test and no
/// integration test named them. They are entry points - a regression here admits an invalid token to
/// the API or to the package registry - so they are exercised through their real
/// <c>AuthenticateAsync</c> pipeline, not through a stub.
///
/// Both handlers are internal, so these tests live in the assembly that grants InternalsVisibleTo and
/// drive them exactly as ASP.NET Core does: build the scheme, attach an <see cref="HttpContext"/>,
/// call the framework entry point, and assert on the resulting ticket.
/// </summary>
public sealed class AlternateAuthenticationHandlerTests
{
    private const string ValidToken = PatConstants.TokenPrefix + "abcd1234567890";
    private readonly IPersonalAccessTokenService _pat = Substitute.For<IPersonalAccessTokenService>();

    private static PatPrincipal Principal(PatScope scope = PatScope.ReadWrite, params string[] roles) =>
        new(42, "alice", roles.Length > 0 ? roles : ["Developer"], scope, 7);

    private async Task<THandler> BuildAsync<THandler>(HttpContext context, string schemeName, params object[] extra)
        where THandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        var options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        var handler = (THandler)Activator.CreateInstance(
            typeof(THandler), [options, NullLoggerFactory.Instance, UrlEncoder.Default, _pat, .. extra])!;
        await handler.InitializeAsync(
            new AuthenticationScheme(schemeName, schemeName, typeof(THandler)), context);
        return handler;
    }

    private Task<PatAuthenticationHandler> PatHandlerAsync(HttpContext context)
        => BuildAsync<PatAuthenticationHandler>(context, PatAuthenticationHandler.SchemeName);

    private Task<PackageRegistryAuthenticationHandler> RegistryHandlerAsync(HttpContext context)
        => BuildAsync<PackageRegistryAuthenticationHandler>(
            context, PackageRegistryAuthenticationHandler.SchemeName,
            new PackageRunTokens(
                new TokenHashKeys(new JwtOptions { SigningKey = "unit-test-signing-key-least-32-bytes!!" }),
                TimeProvider.System),
            Substitute.For<IPackageRegistryRepository>());

    private static HttpContext ContextWith(params (string Header, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        foreach (var (header, value) in headers) context.Request.Headers[header] = value;
        return context;
    }

    private static string BasicHeader(string user, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    // ---------------- PAT handler ----------------

    [Fact]
    public async Task Pat_NoAuthorizationHeader_YieldsNoResult_SoOtherSchemesStillGetAChance()
    {
        var handler = await PatHandlerAsync(ContextWith());

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.False(result.Failure is not null);
        Assert.True(result.None);
    }

    [Fact]
    public async Task Pat_ABearerTokenThatIsNotAPat_YieldsNoResult_NotAFailure()
    {
        // A JWT must fall through to the JWT handler; failing here would break session auth entirely.
        var handler = await PatHandlerAsync(ContextWith(("Authorization", "Bearer eyJhbGciOiJIUzI1NiJ9.x.y")));

        var result = await handler.AuthenticateAsync();

        Assert.True(result.None);
        await _pat.DidNotReceive().ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pat_AnInvalidOrExpiredToken_Fails()
    {
        _pat.ValidateAsync(ValidToken, Arg.Any<CancellationToken>()).Returns((PatPrincipal?)null);
        var handler = await PatHandlerAsync(ContextWith(("Authorization", $"Bearer {ValidToken}")));

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Pat_AValidToken_EmitsTheSameClaimShapeAsAJwtSession()
    {
        _pat.ValidateAsync(ValidToken, Arg.Any<CancellationToken>())
            .Returns(Principal(PatScope.ReadWrite, "Developer", "Operator"));
        var handler = await PatHandlerAsync(ContextWith(("Authorization", $"Bearer {ValidToken}")));

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        var principal = result.Principal!;
        Assert.Equal("alice", principal.FindFirstValue(ClaimTypes.Name));
        Assert.Equal("42", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(PatConstants.ReadWriteScopeValue, principal.FindFirstValue(PatConstants.ScopeClaimType));
        Assert.Equal("7", principal.FindFirstValue(PatConstants.TokenIdClaimType));
        // Roles come from the live user, which is what stops a PAT outranking its owner.
        Assert.True(principal.IsInRole("Developer"));
        Assert.True(principal.IsInRole("Operator"));
    }

    [Fact]
    public async Task Pat_AReadOnlyToken_CarriesTheReadOnlyScope()
    {
        _pat.ValidateAsync(ValidToken, Arg.Any<CancellationToken>()).Returns(Principal(PatScope.ReadOnly));
        var handler = await PatHandlerAsync(ContextWith(("Authorization", $"Bearer {ValidToken}")));

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            PatConstants.ReadOnlyScopeValue,
            result.Principal!.FindFirstValue(PatConstants.ScopeClaimType));
    }

    // ---------------- Package registry handler ----------------

    [Fact]
    public async Task Registry_NoCredentials_YieldsNoResult()
    {
        var handler = await RegistryHandlerAsync(ContextWith());

        Assert.True((await handler.AuthenticateAsync()).None);
    }

    [Fact]
    public async Task Registry_ACredentialThatIsNotAPat_IsRefused_NotIgnored()
    {
        // The registry accepts ONLY Aetheus PATs, so a foreign credential must fail rather than fall
        // through to another scheme that might admit it.
        var handler = await RegistryHandlerAsync(ContextWith(("Authorization", "Bearer some-other-token")));

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Registry_AcceptsThePatFromTheNuGetApiKeyHeader()
    {
        _pat.ValidateAsync(ValidToken, Arg.Any<CancellationToken>()).Returns(Principal());
        var handler = await RegistryHandlerAsync(ContextWith(("X-NuGet-ApiKey", ValidToken)));

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("alice", result.Principal!.FindFirstValue(ClaimTypes.Name));
    }

    [Fact]
    public async Task Registry_AcceptsThePatAsTheBasicPassword_WhichIsHowNuGetSendsIt()
    {
        _pat.ValidateAsync(ValidToken, Arg.Any<CancellationToken>()).Returns(Principal());
        var handler = await RegistryHandlerAsync(
            ContextWith(("Authorization", BasicHeader("alice", ValidToken))));

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Registry_AMalformedBasicHeader_IsRefusedWithoutThrowing()
    {
        var handler = await RegistryHandlerAsync(ContextWith(("Authorization", "Basic !!!not-base64!!!")));

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        await _pat.DidNotReceive().ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Registry_AnInvalidOrExpiredToken_Fails()
    {
        _pat.ValidateAsync(ValidToken, Arg.Any<CancellationToken>()).Returns((PatPrincipal?)null);
        var handler = await RegistryHandlerAsync(ContextWith(("X-NuGet-ApiKey", ValidToken)));

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Registry_AChallenge_AsksForBasic_SoTheNuGetClientPrompts()
    {
        var context = ContextWith();
        var handler = await RegistryHandlerAsync(context);

        await handler.ChallengeAsync(new AuthenticationProperties());

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Contains(
            "Basic realm=\"Aetheus Package Registry\"",
            context.Response.Headers.WWWAuthenticate.ToString(),
            StringComparison.Ordinal);
    }
}
