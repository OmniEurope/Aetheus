// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aetheus.Shared.Components.AiTasks;

/// <summary>
/// Anthropic Messages API, verified 2026-10-11 against
/// https://platform.claude.com/docs/en/build-with-claude/effort: <c>POST /v1/messages</c>, headers
/// <c>x-api-key</c> and <c>anthropic-version: 2023-06-01</c>, effort as <c>output_config.effort</c>
/// (no beta header), the answer in the <c>text</c> blocks of <c>content</c>, and
/// <c>stop_reason: "max_tokens"</c> when <c>max_tokens</c> (thinking included) ran out.
/// </summary>
public sealed class AnthropicMessagesAdapter : IAiProviderAdapter
{
    public const string ApiVersion = "2023-06-01";

    public static readonly AnthropicMessagesAdapter Instance = new();

    public AiProvider Provider => AiProvider.Anthropic;

    public Uri Endpoint(Uri baseUrl) => AiProviderJson.Combine(baseUrl, "/v1/messages");

    public HttpRequestMessage CreateRequest(
        Uri baseUrl, string apiKey, AiCompletionRequest request, bool sendEffort)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxOutputTokens,
            ["system"] = request.SystemPrompt,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = request.UserPrompt
            })
        };
        var effort = AiProviderCatalog.EffortToken(request.Effort);
        if (sendEffort && effort is not null)
            body["output_config"] = new JsonObject { ["effort"] = effort };
        var message = new HttpRequestMessage(HttpMethod.Post, Endpoint(baseUrl))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", ApiVersion);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return message;
    }

    public AiCompletionResult ParseResponse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            throw new JsonException("The Anthropic response has no content array.");
        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() == "text"
                && block.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
                text.Append(value.GetString());
        }
        var stopReason = root.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String
            ? stop.GetString()
            : null;
        root.TryGetProperty("usage", out var usage);
        return new AiCompletionResult(
            text.ToString(),
            stopReason,
            stopReason == "max_tokens",
            AiProviderJson.ReadLong(usage, "input_tokens"),
            AiProviderJson.ReadLong(usage, "output_tokens"));
    }

    public bool RejectsEffort(string errorBody) =>
        (AiProviderJson.ErrorMessage(errorBody) ?? errorBody) is { } message
        && (message.Contains("output_config", StringComparison.OrdinalIgnoreCase)
            || message.Contains("effort", StringComparison.OrdinalIgnoreCase));

    /// <summary><c>GET /v1/models</c> with the largest page Anthropic accepts, so one request reads the
    /// whole list (documented Models API, https://docs.anthropic.com/en/api/models-list; not exercised
    /// against the live API by the tests).</summary>
    public HttpRequestMessage CreateModelsRequest(Uri baseUrl, string apiKey)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, AiProviderJson.Combine(baseUrl, "/v1/models?limit=1000"));
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", ApiVersion);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return message;
    }

    public IReadOnlyList<string> ParseModels(string body) => AiProviderJson.ModelIds(body);
}
