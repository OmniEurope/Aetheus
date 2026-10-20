// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text;
using System.Text.Json;
using Aetheus.Front.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using NSubstitute;

namespace Aetheus.Front.Tests;

/// <summary>A stalled token renewal must have a deadline and respect cancellation so subsequent
/// requests and reconnection controls remain responsive. The deadline runs on a simulated clock and the
/// callers give up when the test says so: nothing here depends on how fast the machine is.</summary>
public class AuthDelegatingHandlerHungRenewalTests
{
    private readonly FakeTimeProvider _clock = new();

    [Fact]
    public async Task SendAsync_RenewalNeverAnswers_CallerCancellationStillEndsTheRequest()
    {
        var inner = new RefreshHangsHandler(honourCancellation: false);
        var (handler, client) = CreateExpiredSession(inner, renewalTimeout: TimeSpan.FromHours(1));
        using var caller = new CancellationTokenSource();

        var sending = client.GetAsync("http://localhost/hubs/servers/negotiate", caller.Token);
        await inner.RefreshesAsync(1);
        await caller.CancelAsync();

        // Before the fix this awaited the hung renewal forever: the hang guard turns that into a failure.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HangGuard.AwaitAsync(sending, "the cancelled request ending"));
        client.Dispose();
        handler.Dispose();
    }

    [Fact]
    public async Task SendAsync_RenewalNeverAnswers_IsAbandonedAtItsDeadlineAndTheRequestGoesOut()
    {
        var inner = new RefreshHangsHandler(honourCancellation: true);
        var (handler, client) = CreateExpiredSession(inner, renewalTimeout: TimeSpan.FromMilliseconds(200));

        var sending = client.GetAsync("http://localhost/health/live", Xunit.TestContext.Current.CancellationToken);
        await inner.RefreshesAsync(1);
        _clock.Advance(TimeSpan.FromMilliseconds(200));
        var response = await HangGuard.AwaitAsync(sending, "the request after the abandoned renewal");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, inner.RefreshCalls);
        client.Dispose();
        handler.Dispose();
    }

    [Fact]
    public async Task SendAsync_AfterAnAbandonedRenewal_TheNextRequestTriesAgain()
    {
        var inner = new RefreshHangsHandler(honourCancellation: true);
        var (handler, client) = CreateExpiredSession(inner, renewalTimeout: TimeSpan.FromMilliseconds(200));

        for (var request = 1; request <= 2; request++)
        {
            var sending = client.GetAsync("http://localhost/health/live", Xunit.TestContext.Current.CancellationToken);
            await inner.RefreshesAsync(request);
            _clock.Advance(TimeSpan.FromMilliseconds(200));
            await HangGuard.AwaitAsync(sending, $"request {request} after its abandoned renewal");
        }

        // A renewal that timed out must not stay parked as the in-flight one.
        Assert.Equal(2, inner.RefreshCalls);
        client.Dispose();
        handler.Dispose();
    }

    [Fact]
    public async Task SendAsync_OneCallerGivesUp_OthersKeepSharingTheSameRenewal()
    {
        // Leaving early must not clear the in-flight renewal: a second renewal would send the same
        // refresh token again, which the server reads as a replay and answers by revoking the sessions.
        var inner = new RefreshHangsHandler(honourCancellation: true);
        var (handler, client) = CreateExpiredSession(inner, renewalTimeout: TimeSpan.FromSeconds(2));
        using var impatient = new CancellationTokenSource();

        var first = client.GetAsync("http://localhost/api/a", impatient.Token);
        await inner.RefreshesAsync(1);
        await impatient.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HangGuard.AwaitAsync(first, "the impatient request ending"));
        // The second caller joins the renewal still in flight (its handler runs synchronously up to that
        // wait), then the renewal reaches its deadline and both are done with it.
        var second = client.GetAsync("http://localhost/api/b", Xunit.TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(2));
        await HangGuard.AwaitAsync(second, "the patient request");

        Assert.Equal(1, inner.RefreshCalls);
        client.Dispose();
        handler.Dispose();
    }

    private (AuthDelegatingHandler Handler, HttpClient Client) CreateExpiredSession(
        HttpMessageHandler inner, TimeSpan renewalTimeout)
    {
        var auth = new AuthStateProvider(Substitute.For<IJSRuntime>(), NullLogger<AuthStateProvider>.Instance);
        typeof(AuthStateProvider).GetProperty("Token")!.SetValue(auth, CreateJwt(DateTimeOffset.UtcNow.AddMinutes(-40)));
        typeof(AuthStateProvider).GetProperty("RefreshToken")!.SetValue(auth, "live-refresh");
        var handler = new AuthDelegatingHandler(auth, NullLogger<AuthDelegatingHandler>.Instance)
        {
            InnerHandler = inner,
            RenewalTimeout = renewalTimeout,
            Clock = _clock
        };
        // The HttpClient's own 100 s timeout is not under test, and a loaded run must not trip it.
        return (handler, new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
    }

    private static string CreateJwt(DateTimeOffset exp)
    {
        var hdr = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\"}"));
        var pay = JsonSerializer.SerializeToUtf8Bytes(new { sub = "u", exp = exp.ToUnixTimeSeconds() });
        var p64 = Convert.ToBase64String(pay).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{hdr}.{p64}.s";
    }

    /// <summary>
    /// The refresh endpoint never answers; everything else answers 200. A browser fetch honours an
    /// abort, so <paramref name="honourCancellation"/> is the realistic case; a request stuck on a dead
    /// connection before any abort is the other one.
    /// </summary>
    private sealed class RefreshHangsHandler(bool honourCancellation) : HttpMessageHandler
    {
        private readonly StateSignal _refreshed = new();
        private int _refreshCalls;
        public int RefreshCalls => Volatile.Read(ref _refreshCalls);

        /// <summary>Completes once <paramref name="count"/> refreshes reached the endpoint.</summary>
        public Task RefreshesAsync(int count) =>
            _refreshed.WhenAsync(() => RefreshCalls >= count, $"{count} refresh(es) sent");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/auth/token/refresh", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _refreshCalls);
                _refreshed.Pulse();
                await Task.Delay(Timeout.Infinite, honourCancellation ? cancellationToken : CancellationToken.None);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
