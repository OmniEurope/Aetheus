// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// Reads the JSON the exporter reads (<c>boundary.json</c>, the Husky runner) the way <c>JSON.parse</c>
/// does, keeping the property order of <c>Object.entries</c>, and checks the shape the exporter relied on
/// without saying so: a list of strings, a map of strings, a map of lists of strings.
/// </summary>
internal static class PublicDistributionJson
{
    /// <summary><c>JSON.parse</c> that reports a refusal instead of throwing: <see cref="JsJson"/> has no
    /// non-throwing form, so its <see cref="FormatException"/> is the one failure turned into a result.</summary>
    public static bool TryParse(string json, out object? value)
    {
        try
        {
            value = JsJson.Parse(json);
            return true;
        }
        catch (FormatException)
        {
            value = null;
            return false;
        }
    }

    /// <summary>A list of strings; an absent key is empty unless <paramref name="required"/>.</summary>
    public static IReadOnlyList<string> Strings(JsObject root, string key, bool required, List<string> problems)
    {
        var value = root[key];
        if (JsValue.IsNullish(value))
        {
            if (required)
                problems.Add($"'{key}' is missing");
            return [];
        }
        if (value is List<object?> list && list.All(item => item is string))
            return [.. list.Cast<string>()];
        problems.Add($"'{key}' is not a list of strings");
        return [];
    }

    /// <summary>A map of strings in the order the file gives them.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> StringMap(JsObject root, string key, bool required, List<string> problems)
    {
        var value = root[key];
        if (JsValue.IsNullish(value))
        {
            if (required)
                problems.Add($"'{key}' is missing");
            return [];
        }
        if (value is JsObject map && map.Properties.All(entry => entry.Value is string))
            return [.. map.Properties.Select(entry => KeyValuePair.Create(entry.Key, (string)entry.Value!))];
        problems.Add($"'{key}' is not a map of strings");
        return [];
    }

    /// <summary>A map of lists of strings, optional (<c>?? {}</c>), in the order the file gives them.</summary>
    public static IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> StringListMap(JsObject root, string key, List<string> problems)
    {
        var value = root[key];
        if (JsValue.IsNullish(value))
            return [];
        if (value is JsObject map && map.Properties.All(entry => IsStringList(entry.Value)))
            return [.. map.Properties.Select(entry => KeyValuePair.Create(entry.Key, (IReadOnlyList<string>)[.. ((List<object?>)entry.Value!).Cast<string>()]))];
        problems.Add($"'{key}' is not a map of lists of strings");
        return [];
    }

    private static bool IsStringList(object? value) => value is List<object?> list && list.All(item => item is string);
}
