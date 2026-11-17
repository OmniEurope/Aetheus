// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;

namespace Aetheus.Front.Tests;

/// <summary>
/// F-FRONT-001: a renewal started for one account must not write its tokens over the session another tab
/// has since adopted (a storage event), nor revive a session this tab has since ended.
/// </summary>
public sealed class AuthRenewalSessionRaceTests
{
    private static readonly TimeSpan Guard = TestDoubles.HangGuard.Limit;
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;
    private static readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider StoppedClock = new();

    private static string Jwt(int userId, string name, TimeSpan life)
    {
        static string Part(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = new Dictionary<string, object>
        {
            ["nameid"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["unique_name"] = name,
            ["exp"] = DateTimeOffset.UtcNow.Add(life).ToUnixTimeSeconds()
        };
        return $"{Part(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\"}"))}.{Part(JsonSerializer.SerializeToUtf8Bytes(payload))}.s";
    }

    /// <summary>A browser storage that serves what the tab holds: the stored tokens are the same account's.</summary>
    private static IJSRuntime StorageHolding(string token, string refresh)
    {
        var js = Substitute.For<IJSRuntime>();
        js.InvokeAsync<string?>("localStorage.getItem",
                Arg.Is<object?[]>(a => a.Length == 1 && (string?)a[0] == StorageKeys.AuthToken))
            .Returns(new ValueTask<string?>(token));
        js.InvokeAsync<string?>("localStorage.getItem",
                Arg.Is<object?[]>(a => a.Length == 1 && (string?)a[0] == StorageKeys.RefreshToken))
            .Returns(new ValueTask<string?>(refresh));
        return js;
    }

    /// <summary>Starts a tab on user 1 whose token is about to expire, then a request that triggers its renewal
    /// and waits until the refresh call is in flight (held by the fake server).</summary>
    private static async Task<(AuthStateProvider Auth, IJSRuntime Js, HeldRefreshServer Server, HttpClient Client, Task<HttpResponseMessage> Request)>
        StartRenewalOfUserOneAsync(string renewedToken)
    {
        var old = Jwt(1, "alice", TimeSpan.FromMinutes(2));
        var js = StorageHolding(old, "alice-refresh");
        var auth = new AuthStateProvider(js, NullLogger<AuthStateProvider>.Instance);
        await auth.InitializeAsync();
        Assert.True(auth.ShouldRenew());

        var server = new HeldRefreshServer(renewedToken);
        var client = new HttpClient(new AuthDelegatingHandler(auth, NullLogger<AuthDelegatingHandler>.Instance)
        {
            InnerHandler = server,
            Clock = StoppedClock
        })
        { Timeout = Timeout.InfiniteTimeSpan };

        var request = client.GetAsync("http://localhost/api/data", Ct);
        await server.RefreshArrived.Task.WaitAsync(Guard, Ct);
        return (auth, js, server, client, request);
    }

    [Fact]
    public async Task ARenewalForUserOne_FinishingAfterAnotherTabAdoptedUserTwo_DoesNotOverwriteUserTwo()
    {
        var renewedForAlice = Jwt(1, "alice", TimeSpan.FromMinutes(30));
        var (auth, js, server, client, request) = await StartRenewalOfUserOneAsync(renewedForAlice);
        var bob = Jwt(2, "bob", TimeSpan.FromMinutes(30));

        // Another tab signed in as bob while alice's refresh was on the wire.
        auth.OnStorageChanged(StorageKeys.AuthToken, bob);
        server.ReleaseRefresh();
        await request.WaitAsync(Guard, Ct);

        Assert.Equal(bob, auth.Token);
        Assert.Equal(2, auth.UserId);
        Assert.Equal("bob", auth.Username);
        Assert.Equal("alice-refresh", auth.RefreshToken);
        // Nothing of alice's answer reached the browser storage the other tab owns.
        await js.DidNotReceive().InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "localStorage.setItem", Arg.Any<object?[]?>());
        client.Dispose();
    }

    [Fact]
    public async Task ARenewalForUserOne_FinishingAfterTheTabSignedOut_DoesNotReviveTheSession()
    {
        var (auth, js, server, client, request) = await StartRenewalOfUserOneAsync(Jwt(1, "alice", TimeSpan.FromMinutes(30)));

        await auth.LogoutAsync();
        server.ReleaseRefresh();
        await request.WaitAsync(Guard, Ct);

        Assert.Null(auth.Token);
        Assert.False(auth.IsAuthenticated);
        await js.DidNotReceive().InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "localStorage.setItem", Arg.Any<object?[]?>());
        client.Dispose();
    }

    [Fact]
    public async Task ARenewalForUserOne_FinishingWhileTheSessionIsStillUserOnes_IsApplied()
    {
        var renewedForAlice = Jwt(1, "alice", TimeSpan.FromMinutes(30));
        var (auth, _, server, client, request) = await StartRenewalOfUserOneAsync(renewedForAlice);

        server.ReleaseRefresh();
        await request.WaitAsync(Guard, Ct);

        Assert.Equal(renewedForAlice, auth.Token);
        Assert.Equal("rotated-refresh", auth.RefreshToken);
        client.Dispose();
    }

    /// <summary>The backend: the refresh answer is held until the test releases it.</summary>
    private sealed class HeldRefreshServer(string renewedToken) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RefreshArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseRefresh() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/auth/token/refresh", StringComparison.Ordinal))
            {
                RefreshArrived.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new LoginResponse { Token = renewedToken, RefreshToken = "rotated-refresh" })
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
