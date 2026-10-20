// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Shared.Components.AiTasks;

/// <summary>One text completion: an Aetheus-owned system prompt and the task's user prompt.</summary>
public sealed record AiCompletionRequest(
    string Model,
    string SystemPrompt,
    string UserPrompt,
    int MaxOutputTokens,
    AiReasoningEffort Effort);

/// <summary>The answer text, and whether the provider stopped it at the output-token limit.</summary>
public sealed record AiCompletionResult(
    string Text,
    string? StopReason,
    bool HitTokenLimit,
    long? InputTokens,
    long? OutputTokens);

/// <summary>
/// What differs between two provider APIs: the endpoint, the request body and its authentication
/// header, the response shape, and how the provider says it does not accept the effort parameter.
/// Transport, retries and error wording live in <see cref="AiProviderClient"/>, once for all.
/// </summary>
public interface IAiProviderAdapter
{
    AiProvider Provider { get; }

    /// <summary>The completion endpoint under a profile's base URL.</summary>
    Uri Endpoint(Uri baseUrl);

    /// <summary>The request, with the key in the provider's own header. <paramref name="sendEffort"/>
    /// false leaves the effort out, after a provider refused it.</summary>
    HttpRequestMessage CreateRequest(Uri baseUrl, string apiKey, AiCompletionRequest request, bool sendEffort);

    /// <summary>Reads a 2xx body. Throws <see cref="JsonException"/> on a body that is not the
    /// documented shape.</summary>
    AiCompletionResult ParseResponse(string body);

    /// <summary>True when a 400 body names the effort parameter, i.e. the model refuses it.</summary>
    bool RejectsEffort(string errorBody);

    /// <summary>The <c>GET</c> request that lists the models the key can use, with the key in the
    /// provider's own header.</summary>
    HttpRequestMessage CreateModelsRequest(Uri baseUrl, string apiKey);

    /// <summary>Reads the model identifiers of a 2xx list body (<c>data[].id</c>, the shape both APIs
    /// use). Throws <see cref="JsonException"/> on a body that is not that shape.</summary>
    IReadOnlyList<string> ParseModels(string body);
}

/// <summary>JSON helpers the two adapters share.</summary>
internal static class AiProviderJson
{
    /// <summary>The provider's own error message (<c>error.message</c>, the shape both APIs use).</summary>
    public static string? ErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("error", out var error)
                   && error.ValueKind == JsonValueKind.Object
                   && error.TryGetProperty("message", out var message)
                   && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static long? ReadLong(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    /// <summary>The <c>data[].id</c> strings of a model list, the shape of both the Anthropic and the
    /// OpenAI <c>GET /v1/models</c> answer.</summary>
    public static IReadOnlyList<string> ModelIds(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
            throw new JsonException("The model list has no data array.");
        var ids = new List<string>();
        foreach (var model in data.EnumerateArray())
        {
            if (model.ValueKind == JsonValueKind.Object
                && model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } value)
                ids.Add(value);
        }
        return ids;
    }

    public static Uri Combine(Uri baseUrl, string path) =>
        new(baseUrl.AbsoluteUri.TrimEnd('/') + path, UriKind.Absolute);
}
