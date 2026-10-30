// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Encodings.Web;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.PersonalAccessTokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

/// <summary>
/// Tests for the dedicated <see cref="GitBasicAuthenticationHandler"/> scheme used by the
/// Git Smart-HTTP endpoints (git CLI cannot send JWT). Covers happy path, missing /
/// malformed / wrong-scheme Authorization headers and invalid credentials.
/// </summary>
public class GitBasicAuthenticationHandlerTests
{
    private readonly IGitSmartHttpService _serviceMock = Substitute.For<IGitSmartHttpService>();
    private readonly IPersonalAccessTokenService _patServiceMock = Substitute.For<IPersonalAccessTokenService>();

    private const string TestKey = "unit-test-encryption-key-32chars!";

    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:EncryptionKey"] = TestKey })
            .Build();

    private async Task<AuthenticateResult> AuthenticateAsync(string? authHeader, int? routeProjectId = null)
    {
        var options = new TestOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        var handler = new GitBasicAuthenticationHandler(
            options,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            _serviceMock,
            _patServiceMock,
            BuildConfig(),
            TimeProvider.System);

        var context = new DefaultHttpContext();
        if (authHeader is not null)
            context.Request.Headers["Authorization"] = authHeader;
        if (routeProjectId is not null)
            context.Request.RouteValues["projectId"] = routeProjectId.Value.ToString();

        var scheme = new AuthenticationScheme(
            GitBasicAuthenticationHandler.SchemeName,
            displayName: null,
            handlerType: typeof(GitBasicAuthenticationHandler));

        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    [Fact]
    public async Task NoAuthorizationHeader_ReturnsNoResult()
    {
        var result = await AuthenticateAsync(null);
        Assert.True(result.None);
    }

    [Fact]
    public async Task BearerScheme_ReturnsNoResult()
    {
        var result = await AuthenticateAsync("Bearer some.jwt.token");
        Assert.True(result.None);
    }

    [Fact]
    public async Task MalformedBase64_Fails()
    {
        var result = await AuthenticateAsync("Basic !!!not-base64!!!");
        Assert.False(result.Succeeded);
        Assert.Null(result.Principal);
    }

    [Fact]
    public async Task MissingColon_Fails()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("nocolon"));
        var result = await AuthenticateAsync($"Basic {encoded}");
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task InvalidCredentials_Fails()
    {
        _serviceMock.AuthenticateBasicAsync("user@example.com", "wrong", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("user@example.com:wrong"));

        var result = await AuthenticateAsync($"Basic {encoded}");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ValidCredentials_Succeeds()
    {
        _serviceMock.AuthenticateBasicAsync("alice@example.com", "s3cret", Arg.Any<CancellationToken>())
            .Returns("alice");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice@example.com:s3cret"));

        var result = await AuthenticateAsync($"Basic {encoded}");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Principal);
        Assert.Equal("alice", result.Principal!.Identity!.Name);
    }

    [Fact]
    public async Task ReadWritePersonalAccessToken_Succeeds_WithLiveIdentityClaims()
    {
        const string token = "aeth_pat_test-token";
        _patServiceMock.ValidateAsync(token, Arg.Any<CancellationToken>())
            .Returns(new PatPrincipal(42, "alice", ["SuperAdmin"], PatScope.ReadWrite, 7));
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"alice:{token}"));

        var result = await AuthenticateAsync($"Basic {encoded}");

        Assert.True(result.Succeeded);
        Assert.Equal("alice", result.Principal!.Identity!.Name);
        Assert.Equal("42", result.Principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);
        Assert.True(result.Principal.HasClaim(PatConstants.ScopeClaimType, PatConstants.ReadWriteScopeValue));
        Assert.True(result.Principal.IsInRole("SuperAdmin"));
        await _serviceMock.DidNotReceive().AuthenticateBasicAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersonalAccessToken_WithDifferentBasicUsername_Fails()
    {
        const string token = "aeth_pat_test-token";
        _patServiceMock.ValidateAsync(token, Arg.Any<CancellationToken>())
            .Returns(new PatPrincipal(42, "alice", [], PatScope.ReadWrite, 7));
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"mallory:{token}"));

        var result = await AuthenticateAsync($"Basic {encoded}");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RunToken_ValidAndInScope_Succeeds_WithScopeClaim()
    {
        var (user, pass) = GitRunCloneToken.Mint(BuildConfig(), runId: 39, projectId: 1, DateTime.UtcNow, GitRunCloneToken.DefaultTtl);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
        // The run must still be active for the token to authenticate (liveness gate).
        _serviceMock.IsRunActiveAsync(39, Arg.Any<CancellationToken>()).Returns(true);

        var result = await AuthenticateAsync($"Basic {encoded}", routeProjectId: 1);

        Assert.True(result.Succeeded);
        Assert.Equal(user, result.Principal!.Identity!.Name);
        Assert.True(result.Principal!.HasClaim(GitBasicAuthenticationHandler.RunScopeClaim, "1"));
        // A run token must never authenticate via the user-account path.
        await _serviceMock.DidNotReceive().AuthenticateBasicAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunToken_RunNoLongerActive_Fails()
    {
        // A valid, in-scope, unexpired token whose run has already finished must be rejected: the
        // liveness gate makes a leaked token inert as soon as the run ends, before HMAC expiry.
        var (user, pass) = GitRunCloneToken.Mint(BuildConfig(), runId: 39, projectId: 1, DateTime.UtcNow, GitRunCloneToken.DefaultTtl);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
        _serviceMock.IsRunActiveAsync(39, Arg.Any<CancellationToken>()).Returns(false);

        var result = await AuthenticateAsync($"Basic {encoded}", routeProjectId: 1);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RunToken_LivenessLookupThrows_DegradesToHmacOnly_Succeeds()
    {
        // A transient DB fault on the liveness lookup must not 500 a legitimate in-flight clone: the
        // gate degrades to the HMAC-only guarantee the token already passed (signature + expiry + scope).
        var (user, pass) = GitRunCloneToken.Mint(BuildConfig(), runId: 39, projectId: 1, DateTime.UtcNow, GitRunCloneToken.DefaultTtl);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
        _serviceMock.IsRunActiveAsync(39, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("transient DB fault"));

        var result = await AuthenticateAsync($"Basic {encoded}", routeProjectId: 1);

        Assert.True(result.Succeeded);
        Assert.Equal(user, result.Principal!.Identity!.Name);
    }

    [Fact]
    public async Task RunToken_WrongProjectRoute_Fails()
    {
        var (user, pass) = GitRunCloneToken.Mint(BuildConfig(), runId: 39, projectId: 1, DateTime.UtcNow, GitRunCloneToken.DefaultTtl);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

        // Token minted for project 1, but the route asks for project 2.
        var result = await AuthenticateAsync($"Basic {encoded}", routeProjectId: 2);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RunToken_TamperedPassword_Fails()
    {
        var (user, _) = GitRunCloneToken.Mint(BuildConfig(), runId: 39, projectId: 1, DateTime.UtcNow, GitRunCloneToken.DefaultTtl);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:9999999999.forged"));

        var result = await AuthenticateAsync($"Basic {encoded}", routeProjectId: 1);

        Assert.False(result.Succeeded);
    }

    private sealed class TestOptionsMonitor<TOptions>(TOptions options) : IOptionsMonitor<TOptions>
    {
        public TOptions CurrentValue { get; } = options;
        public TOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<TOptions, string> listener) => null;
    }
}
