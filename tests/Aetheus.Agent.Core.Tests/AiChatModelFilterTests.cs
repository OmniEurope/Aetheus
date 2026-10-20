// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text;
using static Aetheus.Agent.Core.Tests.AiProviderClientTests;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Recette R3-7, decision 4 of 2026-10-13: an OpenAI or OpenAI-compatible model list keeps only chat
/// models, under the deny rule of <see cref="AiChatModelFilter"/>; the Anthropic list is not filtered.
/// </summary>
public sealed class AiChatModelFilterTests
{
    // Shape and identifiers of a real OpenAI GET /v1/models answer (object, created, owned_by), trimmed.
    private const string OpenAiModelsSample = """
        {"object":"list","data":[
          {"id":"gpt-5.5","object":"model","created":1765000000,"owned_by":"system"},
          {"id":"gpt-5.5-mini","object":"model","created":1765000000,"owned_by":"system"},
          {"id":"gpt-4.1-2025-04-14","object":"model","created":1744316542,"owned_by":"system"},
          {"id":"gpt-4o","object":"model","created":1715367049,"owned_by":"system"},
          {"id":"gpt-4o-search-preview","object":"model","created":1741388720,"owned_by":"system"},
          {"id":"o3-mini","object":"model","created":1737146383,"owned_by":"system"},
          {"id":"chatgpt-4o-latest","object":"model","created":1723515131,"owned_by":"system"},
          {"id":"gpt-3.5-turbo","object":"model","created":1677610602,"owned_by":"openai"},
          {"id":"text-embedding-3-small","object":"model","created":1705948997,"owned_by":"system"},
          {"id":"text-embedding-3-large","object":"model","created":1705953180,"owned_by":"system"},
          {"id":"text-embedding-ada-002","object":"model","created":1671217299,"owned_by":"openai-internal"},
          {"id":"dall-e-3","object":"model","created":1698785189,"owned_by":"system"},
          {"id":"dall-e-2","object":"model","created":1698798177,"owned_by":"system"},
          {"id":"gpt-image-1","object":"model","created":1745517030,"owned_by":"system"},
          {"id":"tts-1","object":"model","created":1681940951,"owned_by":"openai-internal"},
          {"id":"tts-1-hd","object":"model","created":1699046015,"owned_by":"system"},
          {"id":"gpt-4o-mini-tts","object":"model","created":1742403959,"owned_by":"system"},
          {"id":"whisper-1","object":"model","created":1677532384,"owned_by":"openai-internal"},
          {"id":"gpt-4o-transcribe","object":"model","created":1742068463,"owned_by":"system"},
          {"id":"gpt-4o-audio-preview","object":"model","created":1727460443,"owned_by":"system"},
          {"id":"gpt-audio","object":"model","created":1756339249,"owned_by":"system"},
          {"id":"gpt-4o-realtime-preview","object":"model","created":1727659998,"owned_by":"system"},
          {"id":"gpt-realtime","object":"model","created":1756271701,"owned_by":"system"},
          {"id":"omni-moderation-latest","object":"model","created":1731689265,"owned_by":"system"},
          {"id":"text-moderation-stable","object":"model","created":1731689265,"owned_by":"system"},
          {"id":"sora-2","object":"model","created":1759708615,"owned_by":"system"},
          {"id":"babbage-002","object":"model","created":1692634615,"owned_by":"system"},
          {"id":"davinci-002","object":"model","created":1692634301,"owned_by":"system"},
          {"id":"computer-use-preview","object":"model","created":1734655677,"owned_by":"system"}
        ]}
        """;

    [Fact]
    public async Task R3_7_OpenAiList_KeepsOnlyTheChatModels()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(OpenAiModelsSample, Encoding.UTF8, "application/json")
        });

        var outcome = await new AiProviderClient(new HttpClient(handler), (_, _) => Task.CompletedTask).ListModelsAsync(
            new AiProviderSettings(AiProvider.OpenAi, new Uri("https://api.openai.com/v1"), "fake-key-123"),
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["chatgpt-4o-latest", "gpt-3.5-turbo", "gpt-4.1-2025-04-14", "gpt-4o", "gpt-4o-search-preview", "gpt-5.5", "gpt-5.5-mini", "o3-mini"],
            outcome.Models);
    }

    [Theory]
    [InlineData("mistral-large-latest")]
    [InlineData("meta-llama/Llama-3.3-70B-Instruct")]
    [InlineData("Qwen/Qwen2.5-72B-Instruct")]
    [InlineData("llama-3.2-11b-vision-preview")]
    [InlineData("deepseek-chat")]
    public void R3_7_ChatModelsOfCompatibleVendors_AreKept(string id) =>
        Assert.True(AiChatModelFilter.IsChatModel(id));

    [Theory]
    [InlineData("nomic-embed-text")]
    [InlineData("BAAI/bge-reranker-v2-m3")]
    [InlineData("mistral-embed")]
    [InlineData("Whisper-Large-V3")]
    [InlineData("openai/TTS-1")]
    public void R3_7_NonChatModelsOfCompatibleVendors_AreDropped_CaseIgnored(string id) =>
        Assert.False(AiChatModelFilter.IsChatModel(id));

    [Fact]
    public async Task R3_7_AListWithoutAnyChatModel_IsAnError()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[{"id":"text-embedding-3-small"},{"id":"whisper-1"}]}""", Encoding.UTF8, "application/json")
        });

        var outcome = await new AiProviderClient(new HttpClient(handler)).ListModelsAsync(
            new AiProviderSettings(AiProvider.OpenAi, new Uri("https://api.openai.com/v1"), "fake-key-123"),
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Null(outcome.Models);
        Assert.Contains("listed no model usable by an AI task", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R3_7_AnthropicList_IsNotFiltered()
    {
        // An id that the OpenAI rule would drop is still kept from Anthropic: its list is chat models only.
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[{"id":"claude-opus-5-5"},{"id":"claude-embed-test"}]}""", Encoding.UTF8, "application/json")
        });

        var outcome = await new AiProviderClient(new HttpClient(handler)).ListModelsAsync(
            new AiProviderSettings(AiProvider.Anthropic, new Uri("https://api.anthropic.com"), "fake-key-123"),
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(["claude-embed-test", "claude-opus-5-5"], outcome.Models);
    }
}
