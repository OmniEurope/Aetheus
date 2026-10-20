// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// JSON written the way the ported Node.js scripts wrote it, so a file they produced and a file a
/// function produces carry the same bytes, and a digest taken over one equals the digest taken over the
/// other: <c>JSON.stringify(value)</c> (<see cref="Compact"/>) and <c>JSON.stringify(value, null, 2)</c>
/// (<see cref="Indented"/>), with <c>\n</c> line breaks whatever the host, and only the characters JSON
/// requires escaped (no <c>+</c> for a <c>+</c>, no escaped non-ASCII letter).
///
/// Known difference: a control character is escaped with upper-case hex digits where Node.js writes
/// lower-case ones. None of the values these functions hash (revisions, file names, package ids,
/// migration ids) can hold one.
/// </summary>
internal static class ScriptJson
{
    private static readonly JsonWriterOptions CompactOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false
    };

    private static readonly JsonWriterOptions IndentedOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = true,
        IndentSize = 2,
        IndentCharacter = ' ',
        NewLine = "\n"
    };

    /// <summary><c>JSON.stringify(node)</c>.</summary>
    public static string Compact(JsonNode node) => Write(node, CompactOptions);

    /// <summary><c>JSON.stringify(node, null, 2)</c>.</summary>
    public static string Indented(JsonNode node) => Write(node, IndentedOptions);

    private static string Write(JsonNode node, JsonWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(node);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, options))
            node.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
