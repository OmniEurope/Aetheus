// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;

namespace Aetheus.Back.Components.Notifications;

/// <summary>The verdict of a channel rule's filter on one event payload.</summary>
internal enum NotificationRuleFilterVerdict
{
    /// <summary>No filter, or every condition of the filter holds: the event goes to the channel.</summary>
    Match,

    /// <summary>The filter is valid and a condition does not hold: the event does not go to the channel.</summary>
    NoMatch,

    /// <summary>The filter is not a JSON object of conditions: the rule is skipped (and logged by the caller).</summary>
    Invalid
}

/// <summary>
/// Evaluates the optional <c>FilterJson</c> of a channel rule against the JSON payload of an event.
///
/// The filter is a JSON object whose keys name top-level properties of the payload (case-insensitive,
/// <c>projectId</c> reads <c>ProjectId</c>). Every key is a condition and all of them must hold:
/// a scalar value (string, number, boolean) must equal the payload property, an array lists the accepted
/// values (any of them), and <c>null</c> accepts a payload where the property is absent or null. Strings
/// compare ignoring case, numbers by value. A property the payload does not carry never matches a
/// non-null condition. An absent, blank or empty filter (<c>{}</c>) matches every event.
/// Example: <c>{"projectId":7,"status":["Failed","Rejected"]}</c>.
/// </summary>
internal static class NotificationRuleFilter
{
    public static NotificationRuleFilterVerdict Evaluate(string? filterJson, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(filterJson)) return NotificationRuleFilterVerdict.Match;

        JsonDocument filterDocument;
        try
        {
            filterDocument = JsonDocument.Parse(filterJson);
        }
        catch (JsonException)
        {
            return NotificationRuleFilterVerdict.Invalid;
        }

        using (filterDocument)
        {
            var filter = filterDocument.RootElement;
            if (filter.ValueKind != JsonValueKind.Object) return NotificationRuleFilterVerdict.Invalid;
            var conditions = filter.EnumerateObject().ToList();
            if (conditions.Any(condition => !IsValidCondition(condition.Value))) return NotificationRuleFilterVerdict.Invalid;
            if (conditions.Count == 0) return NotificationRuleFilterVerdict.Match;

            using var payloadDocument = ParsePayload(payloadJson);
            var payload = payloadDocument is { RootElement.ValueKind: JsonValueKind.Object }
                ? payloadDocument.RootElement
                : (JsonElement?)null;
            foreach (var condition in conditions)
            {
                if (!Holds(condition.Name, condition.Value, payload))
                    return NotificationRuleFilterVerdict.NoMatch;
            }
            return NotificationRuleFilterVerdict.Match;
        }
    }

    private static JsonDocument? ParsePayload(string payloadJson)
    {
        try
        {
            return JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            // A payload that is not JSON carries no property: no non-null condition can hold.
            return null;
        }
    }

    private static bool IsValidCondition(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
        JsonValueKind.Array => value.EnumerateArray().All(item =>
            item.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null),
        _ => false
    };

    private static bool Holds(string name, JsonElement expected, JsonElement? payload)
    {
        var actual = payload is { } root ? Canonical(FindProperty(root, name)) : null;
        return expected.ValueKind == JsonValueKind.Array
            ? expected.EnumerateArray().Any(item => Equal(Canonical(item), actual))
            : Equal(Canonical(expected), actual);
    }

    private static JsonElement? FindProperty(JsonElement payload, string name)
    {
        foreach (var property in payload.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }
        return null;
    }

    private static bool Equal(string? expected, string? actual) =>
        expected is null ? actual is null : string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    // Null stands for an absent, null or non-scalar value.
    private static string? Canonical(JsonElement? element) => element?.ValueKind switch
    {
        JsonValueKind.String => element.Value.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.Value.TryGetDecimal(out var number)
            ? number.ToString("G29", CultureInfo.InvariantCulture)
            : element.Value.GetRawText(),
        _ => null
    };
}
