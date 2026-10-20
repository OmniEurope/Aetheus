// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Services;

/// <summary>Carry definitive session-end reasons and coordinate browser-tab refreshes to avoid
/// replaying the same refresh token.</summary>
public sealed class SessionEndTests
{
    private static readonly CancellationToken Ct = Xunit.TestContext.Current.CancellationToken;

    private static string Jwt(DateTimeOffset exp)
    {
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\"}"));
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { sub = "u", exp = exp.ToUnixTimeSeconds() }))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{header}.{payload}.s";
    }

    private static readonly string Expired = Jwt(DateTimeOffset.UtcNow.AddMinutes(-1));
    private static string Fresh() => Jwt(DateTimeOffset.UtcNow.AddHours(1));

    /// <summary>The browser's localStorage, shared by every tab (every provider) built on it.</summary>
    private sealed class FakeLocalStorage : IJSRuntime
    {
        public Dictionary<string, string> Items { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            switch (identifier)
            {
                case "localStorage.getItem":
                    return ValueTask.FromResult((TValue)(object?)Items.GetValueOrDefault((string)args![0]!)!);
                case "localStorage.setItem":
                    Items[(string)args![0]!] = (string)args[1]!;
                    break;
                case "localStorage.removeItem":
                    Items.Remove((string)args![0]!);
                    break;
            }
            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class FakeServer(HttpStatusCode refreshStatus, object? refreshBody, Action? duringRefresh = null) : HttpMessageHandler
    {
        public List<(string Path, string? Bearer, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var bearer = request.Headers.Authorization?.Parameter;
            Requests.Add((request.RequestUri!.AbsolutePath, bearer, body));
            if (request.RequestUri.AbsolutePath == "/api/auth/token/refresh")
            {
                duringRefresh?.Invoke();
                return new HttpResponseMessage(refreshStatus)
                {
                    Content = refreshBody is null ? null : JsonContent.Create(refreshBody)
                };
            }
            // Like the real API: an expired access token is refused.
            return new HttpResponseMessage(bearer == Expired ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
        }
    }

    private static (AuthStateProvider Auth, HttpClient Client, FakeServer Server) Tab(
        FakeLocalStorage storage, HttpStatusCode refreshStatus = HttpStatusCode.OK, object? refreshBody = null, Action? duringRefresh = null)
    {
        var auth = new AuthStateProvider(storage, NullLogger<AuthStateProvider>.Instance);
        var server = new FakeServer(refreshStatus, refreshBody, duringRefresh);
        var handler = new AuthDelegatingHandler(auth, NullLogger<AuthDelegatingHandler>.Instance) { InnerHandler = server };
        return (auth, new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, server);
    }

    [Theory]
    [InlineData(RefreshRejectionCodes.Replay)]
    [InlineData(RefreshRejectionCodes.Expired)]
    [InlineData(RefreshRejectionCodes.UnknownToken)]
    [InlineData(RefreshRejectionCodes.UserInactive)]
    public async Task ARefusedRefresh_EndsTheSessionWithTheServersReason(string code)
    {
        var storage = new FakeLocalStorage();
        var (auth, client, _) = Tab(storage, HttpStatusCode.Unauthorized, new ApiError { Message = "x", Code = code });
        await auth.LoginAsync(Expired, "r1");
        var needsLogin = false;
        auth.OnNeedsLogin += () => needsLogin = true;

        // The access token expired: the API refuses it, the renewal is refused too, the session ends.
        using var response = await client.GetAsync("api/data", Ct);

        Assert.False(auth.IsAuthenticated);
        Assert.Equal(code, auth.LastSessionEndReason);
        Assert.Empty(storage.Items);
        Assert.True(needsLogin);
    }

    [Fact]
    public async Task ARefusalWithoutAKnownCode_StillEndsTheSession_AsARefusedRenewal()
    {
        var storage = new FakeLocalStorage();
        var (auth, client, _) = Tab(storage, HttpStatusCode.Forbidden);
        await auth.LoginAsync(Expired, "r1");

        using var response = await client.GetAsync("api/data", Ct);

        Assert.Equal(SessionEndReasons.RefreshRejected, auth.LastSessionEndReason);
    }

    /// <summary>D49: a proxy error or an unreachable backend during a deployment is not a reason to sign out.</summary>
    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ATransientRefreshFailure_KeepsTheSession(HttpStatusCode status)
    {
        var storage = new FakeLocalStorage();
        var (auth, client, _) = Tab(storage, status);
        await auth.LoginAsync(Expired, "r1");

        using var response = await client.GetAsync("api/data", Ct);

        Assert.True(auth.IsAuthenticated);
        Assert.Equal("r1", auth.RefreshToken);
        Assert.Null(auth.LastSessionEndReason);
    }

    /// <summary>
    /// D50: tab A renewed while tab B slept. B wakes with an expired token and A's rotated refresh
    /// token already in storage: B takes A's fresh access token and calls no refresh at all.
    /// </summary>
    [Fact]
    public async Task ASecondTab_TakesTheOtherTabsFreshToken_InsteadOfRenewing()
    {
        var storage = new FakeLocalStorage();
        var (tabB, clientB, serverB) = Tab(storage);
        await tabB.LoginAsync(Expired, "r1");
        var tabA = new AuthStateProvider(storage, NullLogger<AuthStateProvider>.Instance);
        var fresh = Fresh();
        await tabA.LoginAsync(fresh, "r2");

        using var response = await clientB.GetAsync("api/data", Ct);

        Assert.DoesNotContain(serverB.Requests, request => request.Path == "/api/auth/token/refresh");
        Assert.Equal(fresh, Assert.Single(serverB.Requests).Bearer);
        Assert.Equal("r2", tabB.RefreshToken);
    }

    /// <summary>D50: when the other tab's access token has expired too, B renews, but with the rotated refresh token.</summary>
    [Fact]
    public async Task ASecondTab_RenewsWithTheRotatedRefreshToken_NeverTheStaleOne()
    {
        var storage = new FakeLocalStorage();
        var fresh = Fresh();
        var (tabB, clientB, serverB) = Tab(storage, HttpStatusCode.OK, new LoginResponse { Token = fresh, RefreshToken = "r3" });
        await tabB.LoginAsync(Expired, "r1");
        var tabA = new AuthStateProvider(storage, NullLogger<AuthStateProvider>.Instance);
        await tabA.LoginAsync(Expired, "r2");

        using var response = await clientB.GetAsync("api/data", Ct);

        var refresh = Assert.Single(serverB.Requests, request => request.Path == "/api/auth/token/refresh");
        Assert.Contains("\"r2\"", refresh.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"r1\"", refresh.Body, StringComparison.Ordinal);
        Assert.Equal("r3", storage.Items[StorageKeys.RefreshToken]);
    }

    /// <summary>D50: a grace-window answer brings no refresh token; the tab adopts the rotated one stored by the other tab.</summary>
    [Fact]
    public async Task AGraceAnswer_AdoptsTheRefreshTokenTheOtherTabStored()
    {
        var storage = new FakeLocalStorage();
        var fresh = Fresh();
        // Tab A rotates r1 into r2 while B's refresh with r1 is in flight: the server answers B from
        // the grace window, with an access token and no refresh token.
        var (tabB, clientB, serverB) = Tab(storage, HttpStatusCode.OK, new LoginResponse { Token = fresh, RefreshToken = null },
            duringRefresh: () => storage.Items[StorageKeys.RefreshToken] = "r2");
        await tabB.LoginAsync(Expired, "r1");

        using var response = await clientB.GetAsync("api/data", Ct);

        Assert.Contains("\"r1\"", Assert.Single(serverB.Requests, request => request.Path == "/api/auth/token/refresh").Body, StringComparison.Ordinal);
        Assert.Equal(fresh, tabB.Token);
        // Kept, r1 would be sent at the next renewal, outside the grace window: a replay.
        Assert.Equal("r2", tabB.RefreshToken);
    }

    /// <summary>D50: the storage event carries the other tab's new values; a removal is not followed.</summary>
    [Fact]
    public async Task TheStorageEvent_AdoptsNewValues_AndIgnoresRemovals()
    {
        var storage = new FakeLocalStorage();
        var tab = new AuthStateProvider(storage, NullLogger<AuthStateProvider>.Instance);
        await tab.LoginAsync(Fresh(), "r1");
        var newer = Fresh();

        tab.OnStorageChanged(StorageKeys.RefreshToken, "r2");
        tab.OnStorageChanged(StorageKeys.AuthToken, newer);
        tab.OnStorageChanged(StorageKeys.AuthToken, null);
        tab.OnStorageChanged(StorageKeys.RefreshToken, null);

        Assert.Equal("r2", tab.RefreshToken);
        Assert.Equal(newer, tab.Token);
    }

    /// <summary>D48: an expired token with nothing to renew it at start-up ends the session with that reason.</summary>
    [Fact]
    public async Task AnExpiredTokenWithoutRefreshTokenAtStartup_EndsTheSessionWithItsReason()
    {
        var storage = new FakeLocalStorage();
        storage.Items[StorageKeys.AuthToken] = Expired;
        var tab = new AuthStateProvider(storage, NullLogger<AuthStateProvider>.Instance);

        await tab.InitializeAsync();

        Assert.False(tab.IsAuthenticated);
        Assert.Equal(SessionEndReasons.NoRefreshTokenAtStartup, tab.LastSessionEndReason);
    }

    /// <summary>D49 at start-up: an expired token with a stored refresh token is kept for the renewal.</summary>
    [Fact]
    public async Task AnExpiredTokenWithARefreshTokenAtStartup_IsKept()
    {
        var storage = new FakeLocalStorage();
        storage.Items[StorageKeys.AuthToken] = Expired;
        storage.Items[StorageKeys.RefreshToken] = "r1";
        var tab = new AuthStateProvider(storage, NullLogger<AuthStateProvider>.Instance);

        await tab.InitializeAsync();

        Assert.True(tab.IsAuthenticated);
        Assert.True(tab.ShouldRenew());
        Assert.Null(tab.LastSessionEndReason);
    }

    [Fact]
    public async Task ASignInClearsThePreviousSessionEndReason()
    {
        var storage = new FakeLocalStorage();
        var tab = new AuthStateProvider(storage, NullLogger<AuthStateProvider>.Instance);
        await tab.EndSessionAsync(RefreshRejectionCodes.Replay);

        await tab.LoginAsync(Fresh(), "r1");

        Assert.Null(tab.LastSessionEndReason);
    }
}
