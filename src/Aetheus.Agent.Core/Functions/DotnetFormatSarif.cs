// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// Port of the former <c>convert-dotnet-format-report.mjs</c> script, the helper
/// <c>run-affected-dotnet-format.sh</c> ran through Node: turns the JSON report of
/// <c>dotnet format --report</c> into a SARIF 2.1.0 log, one <c>note</c> result per file change, with
/// the same rule id, message, fingerprint and location rules, and the same refusals.
///
/// Differences, deliberate: a report that is not JSON fails with a plain cause instead of a Node stack
/// trace; and a <c>DiagnosticId</c> or <c>FormatDescription</c> that is an object is written as its JSON
/// text where JavaScript's <c>String()</c> wrote <c>[object Object]</c> (dotnet format writes strings).
/// </summary>
internal static class DotnetFormatSarif
{
    private static readonly JsonWriterOptions s_writerOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Converts <paramref name="reportJson"/>; paths in the report are made relative to
    /// <paramref name="workingDirectory"/> (the script's <c>process.cwd()</c>). Returns the SARIF text,
    /// newline-terminated as the script wrote it, and the number of results.</summary>
    public static (string Sarif, int Count) Convert(string reportJson, string workingDirectory)
    {
        using var report = Parse(reportJson);
        if (report.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The dotnet format report must be a JSON array.");

        using var buffer = new MemoryStream();
        var count = 0;
        using (var writer = new Utf8JsonWriter(buffer, s_writerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("version", "2.1.0");
            writer.WriteString("$schema", "https://json.schemastore.org/sarif-2.1.0.json");
            writer.WriteStartArray("runs");
            writer.WriteStartObject();
            writer.WriteStartObject("tool");
            writer.WriteStartObject("driver");
            writer.WriteString("name", "dotnet format");
            writer.WriteString("version", "1");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartArray("results");
            foreach (var document in report.RootElement.EnumerateArray())
                count += WriteDocument(writer, document, workingDirectory);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return (System.Text.Encoding.UTF8.GetString(buffer.ToArray()) + "\n", count);
    }

    private static JsonDocument Parse(string reportJson)
    {
        try
        {
            return JsonDocument.Parse(reportJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The dotnet format report is not valid JSON: {ex.Message}", ex);
        }
    }

    private static int WriteDocument(Utf8JsonWriter writer, JsonElement document, string workingDirectory)
    {
        if (document.ValueKind != JsonValueKind.Object
            || !document.TryGetProperty("FileChanges", out var changes)
            || changes.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The dotnet format report contains an invalid document.");

        // FilePath is the full path; FileName is the bare file name, which cannot be opened or told apart
        // from a namesake in another folder.
        var reportedPath = Present(document, "FilePath") ?? Present(document, "FileName");
        if (reportedPath is not { ValueKind: JsonValueKind.String } || reportedPath.Value.GetString() is not { Length: > 0 } path)
            throw new InvalidDataException("A dotnet format finding has no file name.");
        var uri = (Path.IsPathRooted(path) ? Path.GetRelativePath(workingDirectory, path) : path).Replace('\\', '/');

        var count = 0;
        foreach (var change in changes.EnumerateArray())
        {
            // JavaScript's typeof is "object" for an array too, which the script therefore accepted.
            if (change.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                throw new InvalidDataException("The dotnet format report contains an invalid finding.");
            WriteResult(writer, change, uri);
            count++;
        }
        return count;
    }

    private static void WriteResult(Utf8JsonWriter writer, JsonElement change, string uri)
    {
        writer.WriteStartObject();
        writer.WriteString("ruleId", Text(change, "DiagnosticId", "DOTNET_FORMAT"));
        // Layout is never more than a low-severity finding, and one finding is one file under one rule:
        // without the path in its identity, every file sharing a rule collapsed into a single finding.
        writer.WriteString("level", "note");
        writer.WriteStartObject("partialFingerprints");
        writer.WriteString("dotnetFormatFile/v1", uri);
        writer.WriteEndObject();
        writer.WriteStartObject("message");
        writer.WriteString("text", Text(change, "FormatDescription", "dotnet format would change this source file."));
        writer.WriteEndObject();
        writer.WriteStartArray("locations");
        writer.WriteStartObject();
        writer.WriteStartObject("physicalLocation");
        writer.WriteStartObject("artifactLocation");
        writer.WriteString("uri", uri);
        writer.WriteEndObject();
        writer.WriteStartObject("region");
        writer.WriteNumber("startLine", Position(change, "LineNumber"));
        writer.WriteNumber("startColumn", Position(change, "CharNumber"));
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>The property unless it is absent or null, as JavaScript's <c>??</c> reads it.</summary>
    private static JsonElement? Present(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    /// <summary><c>String(value ?? fallback)</c>.</summary>
    private static string Text(JsonElement change, string name, string fallback) =>
        Present(change, name) is not { } value ? fallback
        : value.ValueKind == JsonValueKind.String ? value.GetString()!
        : value.GetRawText();

    /// <summary><c>Math.max(1, Number(value) || 1)</c>: a missing, zero or non-numeric value is 1.</summary>
    private static double Position(JsonElement change, string name)
    {
        var number = Present(change, name) is { } value ? ToNumber(value) : double.NaN;
        return double.IsNaN(number) || number == 0 ? 1 : Math.Max(1, number);
    }

    /// <summary>JavaScript's <c>Number()</c> for the JSON values a report can hold.</summary>
    private static double ToNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        JsonValueKind.String => ParseNumber(value.GetString()!),
        _ => double.NaN
    };

    private static double ParseNumber(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return 0;
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN;
    }
}
