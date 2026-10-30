// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Front.Components.Shared;

/// <summary>Extract a readable refusal from validation errors, middleware messages, ProblemDetails,
/// JSON strings or plain text so callers can explain failed requests; return null for unexpected
/// bodies instead of throwing.</summary>
internal static class ApiErrorText
{
    /// <summary>The <c>problems</c> of a middleware error body (<see cref="ApiError.Problems"/>), or null
    /// for any other body. <see cref="LaunchProblemText"/> says them in the user's language;
    /// <see cref="Read"/> keeps returning the English message for every body.</summary>
    public static IReadOnlyList<ProblemDto>? ReadProblems(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.TrimStart()[0] != '{') return null;
        try
        {
            var problems = JsonSerializer.Deserialize<ApiError>(payload, JsonOptions.Web)?.Problems;
            return problems is { Count: > 0 } ? problems : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? Read(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        var text = payload.Trim();
        if (text[0] is not ('{' or '[' or '"')) return text;
        try
        {
            using var document = JsonDocument.Parse(text);
            return FromElement(document.RootElement);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static string? FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => NonBlank(element.GetString()),
        JsonValueKind.Array => JoinStrings(element),
        JsonValueKind.Object => FromObject(element),
        _ => null
    };

    private static string? FromObject(JsonElement root)
    {
        var errors = Property(root, "errors");
        if (errors is { ValueKind: JsonValueKind.Array } list && JoinStrings(list) is { } listed)
            return listed;
        if (StringProperty(root, "message") is { } message) return message;
        if (StringProperty(root, "detail") is { } detail) return detail;
        if (errors is { ValueKind: JsonValueKind.Object } byField)
        {
            var fieldErrors = JoinStrings(byField.EnumerateObject().Select(field => field.Value));
            if (fieldErrors is not null) return fieldErrors;
        }
        return StringProperty(root, "title");
    }

    private static JsonElement? Property(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }
        return null;
    }

    private static string? StringProperty(JsonElement root, string name) =>
        Property(root, name) is { ValueKind: JsonValueKind.String } value ? NonBlank(value.GetString()) : null;

    private static string? JoinStrings(JsonElement array) => JoinStrings([array]);

    // Strings directly in the given values, or one level down when a value is itself a list of strings
    // (the per-field arrays of a ProblemDetails "errors" dictionary).
    private static string? JoinStrings(IEnumerable<JsonElement> values)
    {
        var parts = values
            .SelectMany(value => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Repeat(value, 1))
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .OfType<string>()
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
