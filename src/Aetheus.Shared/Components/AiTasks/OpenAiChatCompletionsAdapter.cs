// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aetheus.Shared.Components.AiTasks;

/// <summary>
/// OpenAI Chat Completions API, verified 2026-10-11 against
/// https://developers.openai.com/api/reference/resources/chat and
/// https://developers.openai.com/api/docs/guides/reasoning: <c>POST {base}/chat/completions</c> with
/// <c>Authorization: Bearer</c>, the limit as <c>max_completion_tokens</c> (<c>max_tokens</c> is
/// deprecated), effort as <c>reasoning_effort</c> (model-dependent values), the answer in
/// <c>choices[0].message.content</c> and <c>finish_reason: "length"</c> at the limit. Any vendor that
/// serves this API works through its own base URL.
/// </summary>
public sealed class OpenAiChatCompletionsAdapter : IAiProviderAdapter
{
    public static readonly OpenAiChatCompletionsAdapter Instance = new();

    public AiProvider Provider => AiProvider.OpenAi;

    public Uri Endpoint(Uri baseUrl) => AiProviderJson.Combine(baseUrl, "/chat/completions");

    public HttpRequestMessage CreateRequest(
        Uri baseUrl, string apiKey, AiCompletionRequest request, bool sendEffort)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_completion_tokens"] = request.MaxOutputTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt })
        };
        var effort = AiEffortTokens.Of(request.Effort);
        if (sendEffort && effort is not null)
            body["reasoning_effort"] = effort;
        var message = new HttpRequestMessage(HttpMethod.Post, Endpoint(baseUrl))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return message;
    }

    public AiCompletionResult ParseResponse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            throw new JsonException("The chat completion response has no choices.");
        var choice = choices[0];
        var text = choice.TryGetProperty("message", out var message)
                   && message.ValueKind == JsonValueKind.Object
                   && message.TryGetProperty("content", out var content)
                   && content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? string.Empty
            : string.Empty;
        var finishReason = choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String
            ? finish.GetString()
            : null;
        root.TryGetProperty("usage", out var usage);
        return new AiCompletionResult(
            text,
            finishReason,
            finishReason == "length",
            AiProviderJson.ReadLong(usage, "prompt_tokens"),
            AiProviderJson.ReadLong(usage, "completion_tokens"));
    }

    public bool RejectsEffort(string errorBody) =>
        (AiProviderJson.ErrorMessage(errorBody) ?? errorBody)
        .Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>GET {base}/models</c> (the base already ends in <c>/v1</c>), the list endpoint of
    /// OpenAI and of the vendors serving the same API.</summary>
    public HttpRequestMessage CreateModelsRequest(Uri baseUrl, string apiKey)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, AiProviderJson.Combine(baseUrl, "/models"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return message;
    }

    /// <summary>The listed identifiers that are chat models (<see cref="AiChatModelFilter"/>): the same
    /// endpoint also lists embedding, image, audio and moderation models no AI task can use.</summary>
    public IReadOnlyList<string> ParseModels(string body) =>
        AiProviderJson.ModelIds(body).Where(AiChatModelFilter.IsChatModel).ToList();
}
