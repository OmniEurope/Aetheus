// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Json;

namespace Aetheus.Back.Components.Analysis.DependencyBump;

/// <summary>One package of a manifest section (<c>"lodash": "^4.17.15"</c>), with where its value sits in the bytes of the file.</summary>
/// <param name="Value">The string value, or null when the value is not a string (an object, a number).</param>
/// <param name="Start">Byte offset of the first byte of the value, after its opening quote.</param>
/// <param name="Length">Byte length of the value as written, escapes included.</param>
/// <param name="Escaped">The value is written with escape sequences, so its written form is not its text.</param>
internal sealed record JsonDeclaration(string Section, string Name, string? Value, int Start, int Length, int Line, bool Escaped);

internal sealed record JsonManifestRead(IReadOnlyList<JsonDeclaration> Declarations, bool RepeatedSection);

/// <summary>
/// Reads and changes the string values of a JSON manifest (<c>package.json</c>, <c>composer.json</c>) by editing its
/// bytes: indentation, key order, comments, line endings and every other value stay as written, which a
/// serializer would not keep. It never trusts its own edit: it parses the original and the result and refuses
/// unless the two documents differ in exactly the values asked to change.
/// </summary>
internal static class JsonManifest
{
    private const char PathSeparator = '\u001f';

    private static readonly JsonReaderOptions ReaderOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The entries of the object-valued <paramref name="sections"/> at the top of the document, in file order. Null when the text is not a JSON object.</summary>
    public static JsonManifestRead? Read(string text, IReadOnlyCollection<string> sections)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var bom = HasBom(bytes) ? 3 : 0;
        var scanner = new SectionScanner(bytes, bom, sections);
        try
        {
            var reader = new Utf8JsonReader(bytes.AsSpan(bom), ReaderOptions);
            while (reader.Read())
            {
                if (!scanner.Accept(ref reader)) return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return new JsonManifestRead(scanner.Declarations, scanner.RepeatedSection);
    }

    /// <summary>Follows the tokens of a manifest and collects the entries of the sections asked for: depth 1 holds the section names, depth 2 their entries.</summary>
    private sealed class SectionScanner(byte[] bytes, int bom, IReadOnlyCollection<string> sections)
    {
        private readonly HashSet<string> _seenSections = new(StringComparer.Ordinal);
        private string? _rootKey;
        private string? _openSection;
        private string? _pendingName;
        private bool _started;

        public List<JsonDeclaration> Declarations { get; } = [];

        public bool RepeatedSection { get; private set; }

        /// <summary>False when the document does not start as an object.</summary>
        public bool Accept(ref Utf8JsonReader reader)
        {
            if (!_started)
            {
                _started = true;
                return reader.TokenType == JsonTokenType.StartObject;
            }

            if (reader.CurrentDepth == 1) AcceptRootLevel(ref reader);
            else if (_openSection is not null && reader.CurrentDepth == 2) AcceptEntry(ref reader);
            return true;
        }

        private void AcceptRootLevel(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    _rootKey = reader.GetString();
                    _openSection = null;
                    break;
                case JsonTokenType.StartObject when _rootKey is not null && sections.Contains(_rootKey):
                    _openSection = _rootKey;
                    RepeatedSection |= !_seenSections.Add(_rootKey);
                    break;
                case JsonTokenType.EndObject:
                    _openSection = null;
                    break;
            }
        }

        private void AcceptEntry(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _pendingName = reader.GetString();
                return;
            }

            if (_pendingName is null) return;
            var start = bom + (int)reader.TokenStartIndex;
            var line = LineOf(bytes, start);
            Declarations.Add(reader.TokenType == JsonTokenType.String
                ? new JsonDeclaration(_openSection!, _pendingName, reader.GetString(), start + 1, reader.ValueSpan.Length, line, reader.ValueIsEscaped)
                : new JsonDeclaration(_openSection!, _pendingName, null, start, 0, line, Escaped: false));
            _pendingName = null;
        }

        private static int LineOf(byte[] bytes, int offset)
        {
            var line = 1;
            for (var i = 0; i < offset && i < bytes.Length; i++)
                if (bytes[i] == (byte)'\n') line++;
            return line;
        }
    }

    /// <summary>
    /// The text with each declaration's value replaced, or null (with the reason) when the result does not verify: it must parse
    /// and differ from the original in the replaced values only.
    /// </summary>
    public static string? Replace(string text, IReadOnlyList<(JsonDeclaration Declaration, string NewValue)> edits, out string? error)
    {
        error = null;
        var bytes = Encoding.UTF8.GetBytes(text);
        var edited = new List<byte>(bytes.Length + 16);
        var cursor = 0;
        foreach (var (declaration, newValue) in edits.OrderBy(edit => edit.Declaration.Start))
        {
            if (declaration.Value is null || declaration.Escaped || newValue.Any(c => c is '"' or '\\' || char.IsControl(c)))
            {
                error = "A value that is not a plain string cannot be replaced in place.";
                return null;
            }

            if (declaration.Start < cursor || Encoding.UTF8.GetString(bytes, declaration.Start, declaration.Length) != declaration.Value)
            {
                error = "The text of the file does not hold the value where it was read.";
                return null;
            }

            edited.AddRange(bytes.AsSpan(cursor, declaration.Start - cursor).ToArray());
            edited.AddRange(Encoding.UTF8.GetBytes(newValue));
            cursor = declaration.Start + declaration.Length;
        }

        edited.AddRange(bytes.AsSpan(cursor).ToArray());
        var result = Encoding.UTF8.GetString([.. edited]);
        error = Verify(text, result, edits);
        return error is null ? result : null;
    }

    private static string? Verify(string original, string edited, IReadOnlyList<(JsonDeclaration Declaration, string NewValue)> edits)
    {
        try
        {
            // The byte order mark is not part of the document.
            using var before = JsonDocument.Parse(original.TrimStart('﻿'), DocumentOptions);
            using var after = JsonDocument.Parse(edited.TrimStart('﻿'), DocumentOptions);
            var differences = new List<(string Path, string Old, string New)>();
            Compare(before.RootElement, after.RootElement, [], differences);
            var expected = edits
                .Select(edit => (Path: edit.Declaration.Section + PathSeparator + edit.Declaration.Name, Old: edit.Declaration.Value!, New: edit.NewValue))
                .Where(edit => edit.Old != edit.New)
                .OrderBy(edit => edit.Path, StringComparer.Ordinal)
                .ToList();
            return differences.OrderBy(difference => difference.Path, StringComparer.Ordinal).SequenceEqual(expected)
                ? null
                : "The edited document differs from the original by more than the version values asked for.";
        }
        catch (JsonException)
        {
            return "The edited document does not parse.";
        }
    }

    private static void Compare(JsonElement before, JsonElement after, List<string> path, List<(string Path, string Old, string New)> differences)
    {
        if (before.ValueKind != after.ValueKind)
        {
            differences.Add((Join(path), "<" + before.ValueKind + ">", "<" + after.ValueKind + ">"));
            return;
        }

        switch (before.ValueKind)
        {
            case JsonValueKind.Object:
                var left = before.EnumerateObject().ToList();
                var right = after.EnumerateObject().ToList();
                if (left.Count != right.Count)
                {
                    differences.Add((Join(path), "<members>", "<members>"));
                    return;
                }

                for (var i = 0; i < left.Count; i++)
                {
                    if (left[i].Name != right[i].Name)
                    {
                        differences.Add((Join(path), "<names>", "<names>"));
                        return;
                    }

                    Compare(left[i].Value, right[i].Value, [.. path, left[i].Name], differences);
                }

                break;
            case JsonValueKind.Array:
                var leftItems = before.EnumerateArray().ToList();
                var rightItems = after.EnumerateArray().ToList();
                if (leftItems.Count != rightItems.Count)
                {
                    differences.Add((Join(path), "<items>", "<items>"));
                    return;
                }

                for (var i = 0; i < leftItems.Count; i++) Compare(leftItems[i], rightItems[i], [.. path, i.ToString(System.Globalization.CultureInfo.InvariantCulture)], differences);
                break;
            case JsonValueKind.String:
                if (before.GetString() != after.GetString()) differences.Add((Join(path), before.GetString()!, after.GetString()!));
                break;
            default:
                if (before.GetRawText() != after.GetRawText()) differences.Add((Join(path), before.GetRawText(), after.GetRawText()));
                break;
        }
    }

    private static string Join(List<string> path) => string.Join(PathSeparator, path);

    private static bool HasBom(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
}
