// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Telemetry;

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>
/// The stored form of an OTLP attribute set: a JSON object of at most <see cref="MaxLength"/> characters, the
/// width of the <c>AttributesJson</c> columns. BACK-005: cutting the serialized text at the limit produced
/// invalid JSON that <see cref="AppRoutePerformance"/> drops, so a route vanished from the performance table
/// while its figures were there. An oversized set is now bounded structurally: it stays a valid object, the
/// route and method go first, an over-long value is shortened, an entry that still does not fit is left out,
/// and a marker says the set was bounded. A set that fits is stored exactly as before, so rollup keys built
/// from it do not change.
/// </summary>
internal static class OtlpAttributeJson
{
    public const int MaxLength = 1024;
    internal const string TruncatedKey = "_aetheus_truncated";
    private const int MaxBoundedValueLength = 256;

    private static readonly string[] PriorityKeys =
        [RequestPerformanceMetrics.RouteAttribute, RequestPerformanceMetrics.MethodAttribute];

    public static string? Serialize(IReadOnlyDictionary<string, string> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (attributes.Count == 0) return null;
        var json = JsonSerializer.Serialize(attributes);
        return json.Length <= MaxLength ? json : Bound(attributes);
    }

    private static string Bound(IReadOnlyDictionary<string, string> attributes)
    {
        var bounded = new Dictionary<string, string>(StringComparer.Ordinal) { [TruncatedKey] = bool.TrueString };
        var ordered = PriorityKeys.Where(attributes.ContainsKey)
            .Concat(attributes.Keys.Where(key => !PriorityKeys.Contains(key, StringComparer.Ordinal))
                .Order(StringComparer.Ordinal));
        foreach (var key in ordered)
            TryAdd(bounded, key, attributes[key]);
        return JsonSerializer.Serialize(bounded);
    }

    // Tries the value as it is, then shortened; an entry that fits neither way is left out, and the next
    // (possibly shorter) one still gets its chance.
    private static void TryAdd(Dictionary<string, string> bounded, string key, string value)
    {
        foreach (var candidate in new[] { value, Shorten(value) })
        {
            bounded[key] = candidate;
            if (JsonSerializer.Serialize(bounded).Length <= MaxLength) return;
        }
        bounded.Remove(key);
    }

    private static string Shorten(string value)
    {
        if (value.Length <= MaxBoundedValueLength) return value;
        var cut = MaxBoundedValueLength;
        // Never split a surrogate pair: half a character would not survive a round trip.
        if (char.IsHighSurrogate(value[cut - 1])) cut--;
        return value[..cut];
    }
}
