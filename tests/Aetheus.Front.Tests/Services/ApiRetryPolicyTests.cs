// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Aetheus.Front.Tests;

/// <summary>
/// F-FRONT-003: a POST or PATCH answered 502 may already have been applied (a rerun inserts its run before
/// the proxy loses the answer), so the resilience pipeline sends it again only when it carries an
/// idempotency key; GET, PUT and DELETE keep the retry on 5xx and transient network errors.
/// </summary>
public sealed class ApiRetryPolicyTests
{
    private static readonly TimeSpan Guard = TestDoubles.HangGuard.Limit;
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;
    private static readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider StoppedClock = new();

    [Theory]
    [InlineData("GET", false, true)]
    [InlineData("PUT", false, true)]
    [InlineData("DELETE", false, true)]
    [InlineData("POST", false, false)]
    [InlineData("PATCH", false, false)]
    [InlineData("POST", true, true)]
    [InlineData("PATCH", true, true)]
    public void ShouldRetry_A502_ByMethodAndIdempotencyKey(string method, bool withKey, bool expected)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "http://localhost/api/data");
        if (withKey) request.Options.Set(ApiRetryPolicy.IdempotencyKeyOption, "k1");
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway);

        Assert.Equal(expected, ApiRetryPolicy.ShouldRetry(Polly.Outcome.FromResult(response), request));
    }

    [Fact]
    public void ShouldRetry_ANetworkErrorOnAPostWithoutKey_IsNotRetried()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/data");
        var failure = Polly.Outcome.FromException<HttpResponseMessage>(new HttpRequestException("reset"));

        Assert.False(ApiRetryPolicy.ShouldRetry(failure, request));
    }

    [Theory]
    [InlineData("GET", false, 2)]
    [InlineData("POST", false, 1)]
    [InlineData("POST", true, 2)]
    public async Task ThroughTheResiliencePipeline_A502_IsSentAgainOnlyWhenSafe(string method, bool withKey, int expectedCalls)
    {
        var server = new CountingServer(HttpStatusCode.BadGateway);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(StoppedClock);
        services.AddHttpClient("api")
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => server)
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 1;
                options.Retry.Delay = TimeSpan.Zero;
                options.Retry.ShouldHandle = ApiRetryPolicy.ShouldHandle;
            });
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        using var request = new HttpRequestMessage(new HttpMethod(method), "http://localhost/api/data");
        if (withKey) request.Options.Set(ApiRetryPolicy.IdempotencyKeyOption, "k1");

        using var response = await client.SendAsync(request, Ct).WaitAsync(Guard, Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(expectedCalls, server.Calls);
    }

    [Fact]
    public async Task TheManualRunTrigger_CarriesItsKeyInTheBodyAndInTheRetryMarkerHeader()
    {
        // The back deduplicates on the key of the body; the request option is what lets the pipeline send it again,
        // and no header goes over the wire (it would fail the CORS preflight).
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var pipelines = new PipelinesApi(http);

        await pipelines.TriggerPipelineRunAsync(7, ct: Ct);

        Assert.Equal(HttpMethod.Post, handler.Method);
        var key = Assert.IsType<string>(handler.IdempotencyOption);
        Assert.DoesNotContain(handler.HeaderNames, name => name.Contains("Idempotency", StringComparison.OrdinalIgnoreCase));
        using var body = System.Text.Json.JsonDocument.Parse(handler.Body!);
        Assert.Equal(key, body.RootElement.GetProperty("AETHEUS_RUN_IDEMPOTENCY_KEY").GetString());
    }

    private sealed class CountingServer(HttpStatusCode status) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Body { get; private set; }
        public string? IdempotencyOption { get; private set; }
        public IReadOnlyList<string> HeaderNames { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            IdempotencyOption = request.Options.TryGetValue(ApiRetryPolicy.IdempotencyKeyOption, out var key) ? key : null;
            HeaderNames = [.. request.Headers.Select(h => h.Key), .. request.Content?.Headers.Select(h => h.Key) ?? []];
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
