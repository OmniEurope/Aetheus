// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text;
using System.Text.Json;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The provider adapters (request shape, headers, response parsing) and the client that sends them
/// (retries, effort fallback, error wording, key masking), against a fake HTTP handler: no network.
/// </summary>
public sealed class AiProviderClientTests
{
    private const string Key = "fake-provider-key";
    private static readonly AiCompletionRequest Request =
        new("claude-sonnet-5-5", "system text", "user text", 2048, AiReasoningEffort.Low);

    [Fact]
    public async Task Anthropic_SendsMessagesShape_WithEffortAndKeyHeader()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, AnthropicAnswer("Hello", "end_turn")));
        var outcome = await Client(handler).CompleteAsync(Anthropic(), Request, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("Hello", outcome.Result!.Text);
        Assert.Equal(12, outcome.Result.InputTokens);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://api.anthropic.com/v1/messages", sent.Uri);
        Assert.Equal(Key, sent.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", sent.Headers["anthropic-version"]);
        Assert.False(sent.Headers.ContainsKey("Authorization"));
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("claude-sonnet-5-5", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(2048, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("system text", body.RootElement.GetProperty("system").GetString());
        Assert.Equal("user", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("user text", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("low", body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task OpenAi_SendsChatCompletionsShape_WithReasoningEffortAndBearer()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"Hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2}}"""));
        var settings = new AiProviderSettings(AiProvider.OpenAi, new Uri("https://llm.example.com/v1"), Key);

        var outcome = await Client(handler).CompleteAsync(
            settings, Request with { Model = "gpt-5.5", Effort = AiReasoningEffort.High }, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("Hi", outcome.Result!.Text);
        Assert.False(outcome.Result.HitTokenLimit);
        Assert.Equal(2, outcome.Result.OutputTokens);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://llm.example.com/v1/chat/completions", sent.Uri);
        Assert.Equal($"Bearer {Key}", sent.Headers["Authorization"]);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("gpt-5.5", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(2048, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("user text", body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task DefaultEffort_SendsNoEffortParameter()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, AnthropicAnswer("x", "end_turn")));
        await Client(handler).CompleteAsync(
            Anthropic(), Request with { Effort = AiReasoningEffort.Default }, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.DoesNotContain("output_config", Assert.Single(handler.Requests).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokenLimit_IsReportedAsHitTokenLimit()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, AnthropicAnswer("partial", "max_tokens")));
        var outcome = await Client(handler).CompleteAsync(Anthropic(), Request, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(outcome.Result!.HitTokenLimit);
    }

    [Fact]
    public async Task RateLimit_IsRetriedWithRetryAfter_ThenSucceeds()
    {
        var calls = 0;
        var handler = new FakeHandler(_ =>
        {
            calls++;
            if (calls < 3)
            {
                var limited = Json(HttpStatusCode.TooManyRequests, """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""");
                limited.Headers.TryAddWithoutValidation("retry-after", "7");
                return limited;
            }
            return Json(HttpStatusCode.OK, AnthropicAnswer("done", "end_turn"));
        });
        var waits = new List<TimeSpan>();

        var outcome = await new AiProviderClient(new HttpClient(handler), (wait, _) => { waits.Add(wait); return Task.CompletedTask; })
            .CompleteAsync(Anthropic(), Request, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(3, outcome.Attempts);
        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7)], waits);
        Assert.Equal(2, outcome.Warnings.Count);
    }

    [Fact]
    public async Task ServerErrors_UseExponentialBackoff_AndStopAfterMaxAttempts()
    {
        var handler = new FakeHandler(_ => Json((HttpStatusCode)529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""));
        var waits = new List<TimeSpan>();

        var outcome = await new AiProviderClient(new HttpClient(handler), (wait, _) => { waits.Add(wait); return Task.CompletedTask; })
            .CompleteAsync(Anthropic(), Request, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiProviderClient.MaxAttempts, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], waits);
        Assert.Contains("HTTP 529", outcome.Error, StringComparison.Ordinal);
        Assert.Contains("overloaded", outcome.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unauthorized_IsNotRetried_AndTheKeyIsMaskedOutOfTheError()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Unauthorized,
            "{\"error\":{\"message\":\"Incorrect API key provided: " + Key + "\"}}"));

        var outcome = await Client(handler).CompleteAsync(Anthropic(), Request, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Single(handler.Requests);
        Assert.Contains("HTTP 401", outcome.Error, StringComparison.Ordinal);
        Assert.Contains("API key was refused", outcome.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, outcome.Error, StringComparison.Ordinal);
        Assert.Contains("***", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotFound_NamesTheBaseUrlAndModel()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.NotFound, """{"error":{"message":"model: nope"}}"""));
        var outcome = await Client(handler).CompleteAsync(Anthropic(), Request, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Contains("endpoint or model not found", outcome.Error, StringComparison.Ordinal);
        Assert.Contains("model: nope", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EffortRefusedByTheModel_IsSentAgainWithoutIt_WithAWarning()
    {
        var handler = new FakeHandler(request => request.Body.Contains("reasoning_effort", StringComparison.Ordinal)
            ? Json(HttpStatusCode.BadRequest, """{"error":{"message":"Unrecognized request argument supplied: reasoning_effort"}}""")
            : Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}"""));
        var settings = new AiProviderSettings(AiProvider.OpenAi, new Uri("https://api.openai.com/v1"), Key);

        var outcome = await Client(handler).CompleteAsync(settings, Request, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain("reasoning_effort", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains(outcome.Warnings, warning => warning.Contains("refused the reasoning effort", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OtherBadRequest_IsNotRetried()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest, """{"error":{"message":"max_tokens is too large"}}"""));
        var outcome = await Client(handler).CompleteAsync(Anthropic(), Request, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Single(handler.Requests);
        Assert.Contains("max_tokens is too large", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadableBody_IsAnError_NotASuccess()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"unexpected":true}"""));
        var outcome = await Client(handler).CompleteAsync(Anthropic(), Request, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("unreadable", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_BoundsTheWholeExchange_RetriesIncluded()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var outcome = await new AiProviderClient(new HttpClient(handler), (wait, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct))
            .CompleteAsync(Anthropic(), Request, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.TimedOut);
        Assert.Contains("did not answer within", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportFailure_IsRetried_ThenReported()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
        var outcome = await Client(handler).CompleteAsync(Anthropic(), Request, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiProviderClient.MaxAttempts, handler.Requests.Count);
        Assert.Contains("could not be reached: connection refused", outcome.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.anthropic.com", true)]
    [InlineData("https://llm.example.com/v1", true)]
    [InlineData("http://api.anthropic.com", false)]
    [InlineData("https://user:pass@api.anthropic.com", false)]
    [InlineData("https://api.anthropic.com/?x=1", false)]
    [InlineData("api.anthropic.com", false)]
    [InlineData("", false)]
    public void BaseUrl_MustBeHttpsWithoutCredentialsOrQuery(string value, bool valid) =>
        Assert.Equal(valid, AiProviderCatalog.IsValidBaseUrl(value, out _));

    [Fact]
    public void ProviderTokens_RoundTrip_AndUnknownIsRefused()
    {
        foreach (var provider in Enum.GetValues<AiProvider>())
        {
            Assert.True(AiProviderCatalog.TryParse(AiProviderCatalog.Token(provider), out var parsed));
            Assert.Equal(provider, parsed);
            Assert.True(AiProviderCatalog.IsValidBaseUrl(AiProviderCatalog.DefaultBaseUrl(provider), out _));
            Assert.Equal(provider, AiProviderCatalog.Adapter(provider).Provider);
        }
        Assert.False(AiProviderCatalog.TryParse("", out _));
        Assert.False(AiProviderCatalog.TryParse("claude-cli", out _));
    }

    private static AiProviderSettings Anthropic() =>
        new(AiProvider.Anthropic, new Uri("https://api.anthropic.com"), Key);

    private static AiProviderClient Client(FakeHandler handler) =>
        new(new HttpClient(handler), (_, _) => Task.CompletedTask);

    private static string AnthropicAnswer(string text, string stopReason) =>
        "{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"...\"},{\"type\":\"text\",\"text\":\"" + text + "\"}],\"stop_reason\":\"" + stopReason + "\",\"usage\":{\"input_tokens\":12,\"output_tokens\":3}}";

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal sealed record SentRequest(string Uri, Dictionary<string, string> Headers, string Body);

    internal sealed class FakeHandler(Func<SentRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var sent = new SentRequest(request.RequestUri!.AbsoluteUri, headers, body);
            Requests.Add(sent);
            return respond(sent);
        }
    }
}
