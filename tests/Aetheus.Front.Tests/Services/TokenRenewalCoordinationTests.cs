// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;

namespace Aetheus.Front.Tests;

/// <summary>
/// Recette R1-89 and R1-88: the token renewal of a tab is one renewal, whatever handler asks for it, and a
/// refresh is never sent a second time by the API client's retry. Four realtime hubs coming back from one
/// outage each negotiate through their own <see cref="AuthDelegatingHandler"/> (HubConnectionFactory builds
/// one per connection); they must wait on one shared refresh and reconnect with its token.
/// </summary>
public sealed class TokenRenewalCoordinationTests
{
    // Every wait is for an event the test causes; the hang guard only ends a wait that never could.
    private static readonly TimeSpan Guard = TestDoubles.HangGuard.Limit;
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    // The deadlines on the way (the renewal's 30 s, the resilience pipeline's timeouts, the HttpClient's
    // 100 s) are not under test: they run on a clock that does not move, so a loaded run cannot trip them.
    private static readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider StoppedClock = new();

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { Timeout = Timeout.InfiniteTimeSpan };

    [Fact]
    public async Task FourHubNegotiates_AfterAnOutage_ShareOneRefresh_AndAllCarryTheRenewedToken()
    {
        var auth = CreateExpiredSession();
        var server = new FakeAuthServer(holdRefresh: true);
        string[] hubs = ["pipelines", "servers", "alerts", "user"];
        // One handler and one HttpClient per hub, exactly as HubConnectionFactory builds them.
        var clients = hubs.Select(_ => Client(new AuthDelegatingHandler(auth, NullLogger<AuthDelegatingHandler>.Instance)
        {
            InnerHandler = server.CreateConnectionHandler(),
            Clock = StoppedClock
        })).ToArray();

        var negotiates = hubs.Select((hub, i) =>
            clients[i].PostAsync($"http://localhost/hubs/{hub}/negotiate", null, Ct)).ToArray();
        await server.RefreshArrived.Task.WaitAsync(Guard, Ct);
        // The other three joined the renewal in flight while their requests started (a handler runs
        // synchronously up to that wait): a refresh of their own would be counted below, whenever sent.
        server.ReleaseRefresh();
        await Task.WhenAll(negotiates).WaitAsync(Guard, Ct);

        Assert.Equal(1, server.RefreshCalls);
        Assert.Equal(hubs.Length, server.NegotiateTokens.Count);
        Assert.All(server.NegotiateTokens, token => Assert.Equal(server.IssuedToken, token));
        Assert.Equal("rotated-refresh", auth.RefreshToken);
        foreach (var client in clients) client.Dispose();
    }

    [Fact]
    public async Task ARejectedRenewal_GivesItsReasonToEveryHandlerThatWaitedOnIt()
    {
        var auth = CreateLiveSession();
        var server = new FakeAuthServer(holdRefresh: true, rejectRefreshWith: RefreshRejectionCodes.Replay);
        var reasons = new List<string?>();
        auth.OnNeedsLogin += () => reasons.Add(auth.LastSessionEndReason);
        var first = Client(new AuthDelegatingHandler(auth, NullLogger<AuthDelegatingHandler>.Instance) { InnerHandler = server.CreateConnectionHandler(), Clock = StoppedClock });
        var second = Client(new AuthDelegatingHandler(auth, NullLogger<AuthDelegatingHandler>.Instance) { InnerHandler = server.CreateConnectionHandler(), Clock = StoppedClock });

        var a = first.GetAsync("http://localhost/api/a", Ct);
        await server.RefreshArrived.Task.WaitAsync(Guard, Ct);
        var b = second.GetAsync("http://localhost/api/b", Ct);
        // b joined the held renewal while its request started, before the refresh is released.
        server.ReleaseRefresh();
        await Task.WhenAll(a, b).WaitAsync(Guard, Ct);

        Assert.Equal(1, server.RefreshCalls);
        Assert.Equal(2, reasons.Count);
        Assert.All(reasons, reason => Assert.Equal(SessionEndReasons.ForRefreshRejection(RefreshRejectionCodes.Replay), reason));
        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public async Task ARefreshWhoseAnswerIsLost_IsNotResentByTheRetry()
    {
        // The server rotated the token, then the proxy answered 502: sending the same token again would
        // get the grace answer (no refresh token) and orphan the tab's chain until the replay five
        // minutes later (R1-88).
        var auth = CreateExpiredSession();
        var server = new FakeAuthServer(holdRefresh: false, refreshStatus: HttpStatusCode.BadGateway);
        using var provider = BuildApiClient(auth, server);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        await client.GetAsync("http://localhost/api/data", Ct).WaitAsync(Guard, Ct);

        Assert.Equal(1, server.RefreshCalls);
    }

    [Fact]
    public async Task AnOrdinaryRequestAnswered502_IsStillRetriedOnce()
    {
        var auth = CreateLiveSession();
        var server = new FakeAuthServer(holdRefresh: false, apiStatus: HttpStatusCode.BadGateway);
        using var provider = BuildApiClient(auth, server);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        await client.GetAsync("http://localhost/api/data", Ct).WaitAsync(Guard, Ct);

        Assert.Equal(0, server.RefreshCalls);
        Assert.Equal(2, server.ApiCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false, false)]
    [InlineData(HttpStatusCode.NotFound, false, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, false)]
    [InlineData(HttpStatusCode.BadGateway, true, false)]
    public void ShouldRetry_ByStatusAndRenewalMarker(HttpStatusCode status, bool renewal, bool expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/auth/token/refresh");
        if (renewal) request.Headers.Add(AuthDelegatingHandler.RenewalMarkerHeader, "1");
        using var response = new HttpResponseMessage(status);

        Assert.Equal(expected, ApiRetryPolicy.ShouldRetry(Polly.Outcome.FromResult(response), request));
    }

    [Fact]
    public void ShouldRetry_ANetworkErrorOnARenewal_IsNotRetried_ButOnAnyOtherRequestIs()
    {
        using var renewal = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/auth/token/refresh");
        renewal.Headers.Add(AuthDelegatingHandler.RenewalMarkerHeader, "1");
        using var ordinary = new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/data");
        var failure = Polly.Outcome.FromException<HttpResponseMessage>(new HttpRequestException("reset"));

        Assert.False(ApiRetryPolicy.ShouldRetry(failure, renewal));
        Assert.True(ApiRetryPolicy.ShouldRetry(failure, ordinary));
    }

    private static ServiceProvider BuildApiClient(AuthStateProvider auth, FakeAuthServer server)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(StoppedClock);
        services.AddHttpClient("api")
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler(() => new AuthDelegatingHandler(auth, NullLogger<AuthDelegatingHandler>.Instance) { Clock = StoppedClock })
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 1;
                options.Retry.Delay = TimeSpan.Zero;
                options.Retry.ShouldHandle = ApiRetryPolicy.ShouldHandle;
            });
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(server.CreateConnectionHandler));
        return services.BuildServiceProvider();
    }

    private static AuthStateProvider CreateExpiredSession()
    {
        var auth = new AuthStateProvider(Substitute.For<IJSRuntime>(), NullLogger<AuthStateProvider>.Instance);
        typeof(AuthStateProvider).GetProperty("Token")!.SetValue(auth, CreateJwt(DateTimeOffset.UtcNow.AddMinutes(-7)));
        typeof(AuthStateProvider).GetProperty("RefreshToken")!.SetValue(auth, "live-refresh");
        return auth;
    }

    private static AuthStateProvider CreateLiveSession()
    {
        var auth = new AuthStateProvider(Substitute.For<IJSRuntime>(), NullLogger<AuthStateProvider>.Instance);
        typeof(AuthStateProvider).GetProperty("Token")!.SetValue(auth, CreateJwt(DateTimeOffset.UtcNow.AddMinutes(30)));
        typeof(AuthStateProvider).GetProperty("RefreshToken")!.SetValue(auth, "live-refresh");
        return auth;
    }

    private static string CreateJwt(DateTimeOffset exp)
    {
        var hdr = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\"}"));
        var pay = JsonSerializer.SerializeToUtf8Bytes(new { sub = "u", exp = exp.ToUnixTimeSeconds() });
        var p64 = Convert.ToBase64String(pay).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{hdr}.{p64}.s";
    }

    /// <summary>The backend seen by every connection of the tab: the refresh endpoint and the rest.</summary>
    private sealed class FakeAuthServer(
        bool holdRefresh,
        HttpStatusCode refreshStatus = HttpStatusCode.OK,
        string? rejectRefreshWith = null,
        HttpStatusCode apiStatus = HttpStatusCode.OK)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _refreshCalls;
        private int _apiCalls;

        public TaskCompletionSource RefreshArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string IssuedToken { get; } = CreateJwt(DateTimeOffset.UtcNow.AddMinutes(30));
        public int RefreshCalls => Volatile.Read(ref _refreshCalls);
        public int ApiCalls => Volatile.Read(ref _apiCalls);
        public System.Collections.Concurrent.ConcurrentBag<string?> NegotiateTokens { get; } = [];

        public void ReleaseRefresh() => _release.TrySetResult();

        public HttpMessageHandler CreateConnectionHandler() => new Connection(this);

        private async Task<HttpResponseMessage> AnswerAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/auth/token/refresh", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _refreshCalls);
                RefreshArrived.TrySetResult();
                if (holdRefresh) await _release.Task.WaitAsync(ct);
                if (rejectRefreshWith is not null)
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = JsonContent.Create(new ApiError { Message = "refused", Code = rejectRefreshWith })
                    };
                if (refreshStatus != HttpStatusCode.OK) return new HttpResponseMessage(refreshStatus);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new LoginResponse { Token = IssuedToken, RefreshToken = "rotated-refresh" })
                };
            }
            if (path.EndsWith("/negotiate", StringComparison.Ordinal))
            {
                NegotiateTokens.Add(request.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            Interlocked.Increment(ref _apiCalls);
            return new HttpResponseMessage(rejectRefreshWith is null ? apiStatus : HttpStatusCode.Unauthorized);
        }

        private sealed class Connection(FakeAuthServer server) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                server.AnswerAsync(request, cancellationToken);
        }
    }
}
