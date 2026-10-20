// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text;
using static Aetheus.Agent.Core.Tests.AiProviderClientTests;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Recette R3-7: the model list request of each provider adapter and its reading by
/// <see cref="AiProviderClient.ListModelsAsync"/>, against a fake HTTP handler: no network.
/// </summary>
public sealed class AiProviderModelListTests
{
    private const string Key = "fake-provider-key-123";

    [Fact]
    public async Task R3_7_Anthropic_ListsModels_WithItsHeaders_SortedAndDistinct()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK,
            """{"data":[{"id":"claude-opus-5-5","type":"model"},{"id":"claude-haiku-5","type":"model"},{"id":"claude-opus-5-5","type":"model"},{"type":"model"}],"has_more":false}"""));

        var outcome = await Client(handler).ListModelsAsync(
            new AiProviderSettings(AiProvider.Anthropic, new Uri("https://api.anthropic.com"), Key), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(["claude-haiku-5", "claude-opus-5-5"], outcome.Models);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://api.anthropic.com/v1/models?limit=1000", sent.Uri);
        Assert.Equal(Key, sent.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", sent.Headers["anthropic-version"]);
        Assert.False(sent.Headers.ContainsKey("Authorization"));
        Assert.Equal(string.Empty, sent.Body);
    }

    [Fact]
    public async Task R3_7_OpenAiCompatible_ListsModels_UnderItsBaseUrl_WithBearer()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK,
            """{"object":"list","data":[{"id":"gpt-5.5","object":"model"},{"id":"gpt-5.5-mini","object":"model"}]}"""));

        var outcome = await Client(handler).ListModelsAsync(
            new AiProviderSettings(AiProvider.OpenAi, new Uri("https://llm.example.com/v1/"), Key), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(["gpt-5.5", "gpt-5.5-mini"], outcome.Models);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://llm.example.com/v1/models", sent.Uri);
        Assert.Equal($"Bearer {Key}", sent.Headers["Authorization"]);
    }

    [Fact]
    public async Task R3_7_RefusedKey_IsAnErrorWithTheKeyMasked_AndIsNotRetried()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Unauthorized,
            $$$"""{"error":{"type":"authentication_error","message":"invalid x-api-key {{{Key}}}"}}"""));

        var outcome = await Client(handler).ListModelsAsync(
            new AiProviderSettings(AiProvider.Anthropic, new Uri("https://api.anthropic.com"), Key), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("HTTP 401", outcome.Error, StringComparison.Ordinal);
        Assert.Contains("the API key was refused", outcome.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, outcome.Error, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("""{"data":[]}""", "listed no model")]
    [InlineData("""{"models":["x"]}""", "unreadable model list")]
    [InlineData("not json", "unreadable model list")]
    public async Task R3_7_EmptyOrUnreadableList_IsAnError(string body, string expected)
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, body));

        var outcome = await Client(handler).ListModelsAsync(
            new AiProviderSettings(AiProvider.OpenAi, new Uri("https://llm.example.com/v1"), Key), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Null(outcome.Models);
        Assert.Contains(expected, outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R3_7_UnreachableProvider_IsAnError()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));

        var outcome = await Client(handler).ListModelsAsync(
            new AiProviderSettings(AiProvider.OpenAi, new Uri("https://llm.example.com/v1"), Key), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("could not be reached: connection refused", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R3_7_SilentProvider_TimesOut()
    {
        var client = new AiProviderClient(new HttpClient(new HangingHandler()));

        var outcome = await client.ListModelsAsync(
            new AiProviderSettings(AiProvider.Anthropic, new Uri("https://api.anthropic.com"), Key), TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("did not answer within", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R3_7_OverlongIds_AreDropped_AndTheListIsCapped()
    {
        var ids = Enumerable.Range(0, AiProviderClient.MaxListedModels + 20).Select(index => $"m-{index:D4}")
            .Append(new string('x', AiProviderClient.MaxModelIdLength + 1));
        var body = "{\"data\":[" + string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\"}}")) + "]}";
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, body));

        var outcome = await Client(handler).ListModelsAsync(
            new AiProviderSettings(AiProvider.OpenAi, new Uri("https://llm.example.com/v1"), Key), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(AiProviderClient.MaxListedModels, outcome.Models!.Count);
        Assert.All(outcome.Models, id => Assert.True(id.Length <= AiProviderClient.MaxModelIdLength));
    }

    private static AiProviderClient Client(FakeHandler handler) =>
        new(new HttpClient(handler), (_, _) => Task.CompletedTask);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
