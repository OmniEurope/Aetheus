// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text.Json;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>agent-release-manifest.json</c> checks the agent-release scripts made in inline Node, with
/// JavaScript's strict equality: a value of another JSON type than the one compared to never matches
/// (<c>"2"</c> is not protocol <c>2</c>), and a missing property reads as <c>undefined</c>.
/// </summary>
internal static class AgentReleaseManifest
{
    internal const string FileName = "agent-release-manifest.json";

    public static JsonDocument Read(string directory) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, FileName)));

    /// <summary>The property's text as JavaScript's template literal prints it.</summary>
    public static string Text(JsonElement element, string name) =>
        Property(element, name) switch
        {
            { ValueKind: JsonValueKind.String } value => value.GetString()!,
            { ValueKind: JsonValueKind.Undefined } => "undefined",
            var value => value.GetRawText()
        };

    public static bool IsString(JsonElement element, string name, string expected) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value
        && string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    public static bool IsNumber(JsonElement element, string name, double expected) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.GetDouble().Equals(expected);

    /// <summary><c>array.includes(value)</c>, false when the property is not an array.</summary>
    public static bool Includes(JsonElement element, string name, string value) =>
        Property(element, name) is { ValueKind: JsonValueKind.Array } array
        && array.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == value);

    /// <summary>The elements of an array property; none when it is not an array.</summary>
    public static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    /// <summary>Whether the archive the entry names has exactly its <c>sizeBytes</c> and, ignoring
    /// case, its <c>sha256</c>. A missing file throws, as <c>readFileSync</c> did.</summary>
    public static bool ArchiveMatches(string directory, JsonElement archive)
    {
        var bytes = File.ReadAllBytes(Path.Combine(directory, Text(archive, "fileName")));
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return IsNumber(archive, "sizeBytes", bytes.Length)
               && Property(archive, "sha256") is { ValueKind: JsonValueKind.String } declared
               && string.Equals(sha256, declared.GetString()!.ToLowerInvariant(), StringComparison.Ordinal);
    }

    public static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
}
