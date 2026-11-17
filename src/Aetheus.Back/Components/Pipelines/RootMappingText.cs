// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The lines of a YAML document whose top level is a block mapping, with the few operations the
/// schedule editor needs on a top-level scalar: replace its value or add the entry. Everything else,
/// comments, indentation, quotes, line endings, is left as written. A top-level key is a line indented
/// exactly as the least indented content line: a key nested in a stage, a step or a script block is
/// more indented, so it is never taken for one. It works on text and does not decide whether the
/// result is right: <see cref="PipelineScheduleYamlEditor"/> parses both documents and compares them.
/// </summary>
internal sealed class RootMappingText
{
    private readonly List<(string Text, string Ending)> _lines;
    private readonly int _rootIndent;
    private readonly string _newline;

    private RootMappingText(List<(string Text, string Ending)> lines, int rootIndent, string newline)
    {
        _lines = lines;
        _rootIndent = rootIndent;
        _newline = newline;
    }

    /// <summary>Reads <paramref name="yaml"/>; an error says why its layout is not a single block mapping.</summary>
    public static RootMappingText? Create(string yaml, out string? error)
    {
        var lines = Split(yaml);
        error = null;
        var contentIndents = new List<int>();
        // A leading document marker is not content. A second document never gets here: the parser refuses it.
        contentIndents.AddRange(lines
            .Where(line => !IsDocumentMarker(line.Text) && !IsBlankOrComment(line.Text))
            .Select(line => IndentOf(line.Text)));

        if (contentIndents.Count == 0)
        {
            error = "The pipeline YAML defines no key.";
            return null;
        }

        var newline = lines.Select(line => line.Ending).FirstOrDefault(ending => ending.Length > 0) ?? "\n";
        return new RootMappingText(lines, contentIndents.Min(), newline);
    }

    /// <summary>The lines as written, each with its own line ending.</summary>
    public IReadOnlyList<(string Text, string Ending)> Lines => _lines;

    /// <summary>The indent of a top-level key.</summary>
    public int RootIndent => _rootIndent;

    public override string ToString() => string.Concat(_lines.Select(line => line.Text + line.Ending));

    /// <summary>The number of top-level lines that start the key <paramref name="key"/>.</summary>
    public int CountKey(string key) => FindKeyLines(key).Count;

    /// <summary>Sets the value of the top-level scalar <paramref name="key"/> to <paramref name="value"/>,
    /// in the quote style it was written in, or adds the entry (double quoted when
    /// <paramref name="quoteWhenAdded"/>, plain otherwise) after the first existing key of
    /// <paramref name="afterKeys"/>, at the end of the mapping when none exists. Returns the reason
    /// when the entry is written in a layout this editor does not keep. The key must not be repeated.</summary>
    public string? SetScalar(string key, string value, bool quoteWhenAdded, params string[] afterKeys)
    {
        var keyLines = FindKeyLines(key);
        if (keyLines.Count > 1)
        {
            return $"The key '{key}' is written more than once at the top level.";
        }

        if (keyLines.Count == 1)
        {
            return ReplaceValue(keyLines[0], key, value);
        }

        var written = quoteWhenAdded ? $"\"{value}\"" : value;
        Insert(InsertionIndex(afterKeys), new string(' ', _rootIndent) + $"{key}: {written}");
        return null;
    }

    private List<int> FindKeyLines(string key)
    {
        var prefix = new string(' ', _rootIndent) + key + ":";
        var found = new List<int>();
        for (var i = 0; i < _lines.Count; i++)
        {
            var text = _lines[i].Text;
            if (text.StartsWith(prefix, StringComparison.Ordinal)
                && (text.Length == prefix.Length || text[prefix.Length] is ' ' or '\t'))
            {
                found.Add(i);
            }
        }

        return found;
    }

    private string? ReplaceValue(int lineIndex, string key, string value)
    {
        var text = _lines[lineIndex].Text;
        var afterColon = _rootIndent + key.Length + 1;
        var span = ValueSpan.Locate(text, afterColon, out var error);
        if (span is null)
        {
            return $"The value of '{key}' {error}.";
        }

        if (span.Quote != '"' && span.Quote != '\'' && ContinuesOnNextLines(lineIndex))
        {
            return $"The value of '{key}' continues on the following lines.";
        }

        var before = text[..span.Start];
        if (span.IsEmpty && before.EndsWith(':'))
        {
            before += " ";
        }

        var after = text[span.End..];
        var token = span.Render(value);
        // An empty value followed by a comment keeps one space between the new value and the comment.
        if (span.IsEmpty && after.StartsWith('#'))
        {
            token += " ";
        }

        _lines[lineIndex] = (before + token + after, _lines[lineIndex].Ending);
        return null;
    }

    private bool ContinuesOnNextLines(int lineIndex)
    {
        for (var i = lineIndex + 1; i < _lines.Count; i++)
        {
            var text = _lines[i].Text;
            if (string.IsNullOrWhiteSpace(text) || IsBlankOrComment(text))
            {
                continue;
            }

            return IndentOf(text) > _rootIndent;
        }

        return false;
    }

    private int InsertionIndex(string[] afterKeys)
    {
        foreach (var afterKey in afterKeys)
        {
            var keyLines = FindKeyLines(afterKey);
            if (keyLines.Count == 1)
            {
                return EntryEnd(keyLines[0]) + 1;
            }
        }

        var last = _lines.FindLastIndex(line => IndentOf(line.Text) == _rootIndent && !IsBlankOrComment(line.Text)
            && !IsDocumentMarker(line.Text) && !line.Text.TrimStart().StartsWith('-'));
        return EntryEnd(Math.Max(last, 0)) + 1;
    }

    /// <summary>The last line of the entry starting at <paramref name="keyLine"/>: its continuation
    /// lines (more indented, or a sequence written at the key's own indent), without the blank and comment
    /// lines that come after it, which belong to what follows.</summary>
    private int EntryEnd(int keyLine)
    {
        var last = keyLine;
        for (var i = keyLine + 1; i < _lines.Count; i++)
        {
            var text = _lines[i].Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var indent = IndentOf(text);
            var trimmed = text.TrimStart();
            if (indent > _rootIndent || (indent == _rootIndent && IsSequenceItem(trimmed)))
            {
                last = i;
                continue;
            }

            // A comment written at the key's indent or less belongs to the entry that follows.
            if (trimmed[0] != '#')
            {
                break;
            }
        }

        return last;
    }

    private void Insert(int index, string text)
    {
        var previous = _lines[index - 1];
        if (previous.Ending.Length == 0)
        {
            _lines[index - 1] = (previous.Text, _newline);
            _lines.Insert(index, (text, string.Empty));
            return;
        }

        _lines.Insert(index, (text, previous.Ending));
    }

    private static bool IsSequenceItem(string trimmed) => trimmed == "-" || trimmed.StartsWith("- ", StringComparison.Ordinal);

    private static bool IsDocumentMarker(string text) =>
        text.StartsWith('%')
        || ((text.StartsWith("---", StringComparison.Ordinal) || text.StartsWith("...", StringComparison.Ordinal))
            && (text.Length == 3 || char.IsWhiteSpace(text[3])));

    private static bool IsBlankOrComment(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.Length == 0 || trimmed[0] == '#';
    }

    private static int IndentOf(string text) => text.Length - text.TrimStart(' ').Length;

    private static List<(string Text, string Ending)> Split(string yaml)
    {
        var lines = new List<(string Text, string Ending)>();
        var current = new StringBuilder();
        for (var i = 0; i < yaml.Length; i++)
        {
            var c = yaml[i];
            if (c is not ('\r' or '\n'))
            {
                current.Append(c);
                continue;
            }

            var ending = c == '\r' && i + 1 < yaml.Length && yaml[i + 1] == '\n' ? "\r\n" : c.ToString();
            i += ending.Length - 1;
            lines.Add((current.ToString(), ending));
            current.Clear();
        }

        if (current.Length > 0)
        {
            lines.Add((current.ToString(), string.Empty));
        }

        return lines;
    }

    /// <summary>Adds <paramref name="items"/> (already rendered YAML scalars) to the top-level list
    /// <paramref name="key"/>: after the last item of a block list, before the closing bracket of a flow list written
    /// on one line, under the key when its value is empty, or as a new block list (placed after the first existing key
    /// of <paramref name="afterKeys"/>, else at the end of the mapping) when the key is absent. Returns the reason when
    /// the entry is written in a layout this editor does not keep. The key must not be repeated.</summary>
    public string? AddToList(string key, IReadOnlyList<string> items, params string[] afterKeys)
    {
        var keyLines = FindKeyLines(key);
        if (keyLines.Count > 1)
        {
            return $"The key '{key}' is written more than once at the top level.";
        }

        if (keyLines.Count == 0)
        {
            var index = InsertionIndex(afterKeys);
            Insert(index, new string(' ', _rootIndent) + key + ":");
            InsertBlockItems(index + 1, new string(' ', _rootIndent + 2) + "- ", items);
            return null;
        }

        var keyLine = keyLines[0];
        var text = _lines[keyLine].Text;
        var afterColon = _rootIndent + key.Length + 1;
        var span = ValueSpan.Locate(text, afterColon, out var error);
        if (span is null)
        {
            // A flow list is the one layout that is kept, on a single line.
            return text.AsSpan(FirstValueIndex(text, afterColon)).StartsWith("[")
                ? AddToFlowList(keyLine, afterColon, key, items)
                : $"The value of '{key}' {error}.";
        }

        if (!span.IsEmpty)
        {
            return $"The value of '{key}' is a single value, a list was expected.";
        }

        var last = LastBlockItem(keyLine, out var prefix);
        if (last is null)
        {
            InsertBlockItems(keyLine + 1, new string(' ', _rootIndent + 2) + "- ", items);
            return null;
        }

        InsertBlockItems(EntryEnd(keyLine) + 1, prefix!, items);
        return null;
    }

    private void InsertBlockItems(int index, string prefix, IReadOnlyList<string> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            Insert(index + i, prefix + items[i]);
        }
    }

    /// <summary>The last item line of the block list under <paramref name="keyLine"/> with the text that opens it
    /// (indent, dash and spaces), or null when no item follows (the value is empty).</summary>
    private int? LastBlockItem(int keyLine, out string? prefix)
    {
        prefix = null;
        var end = EntryEnd(keyLine);
        var itemIndent = -1;
        int? last = null;
        for (var i = keyLine + 1; i <= end; i++)
        {
            var line = _lines[i].Text;
            if (!IsSequenceItem(line.TrimStart(' ')))
            {
                continue;
            }

            var indent = IndentOf(line);
            if (itemIndent < 0 || indent < itemIndent)
            {
                itemIndent = indent;
            }

            if (indent == itemIndent)
            {
                last = i;
            }
        }

        if (last is { } found)
        {
            var line = _lines[found].Text;
            var afterDash = itemIndent + 1;
            while (afterDash < line.Length && line[afterDash] == ' ')
            {
                afterDash++;
            }

            prefix = line[..afterDash];
            if (afterDash == itemIndent + 1)
            {
                prefix += " ";
            }
        }

        return last;
    }

    private string? AddToFlowList(int keyLine, int afterColon, string key, IReadOnlyList<string> items)
    {
        var text = _lines[keyLine].Text;
        var open = FirstValueIndex(text, afterColon);
        var close = FlowListEnd(text, open);
        if (close < 0)
        {
            return $"The value of '{key}' is a flow list that does not end on its line, or holds something nested.";
        }

        // [a, b] and [ a, b ] keep their own spacing: the new items go right after the last one.
        var content = text[(open + 1)..close].TrimEnd(' ');
        var insertAt = open + 1 + content.Length;
        var joined = string.Join(", ", items);
        var added = content.Trim().Length == 0 ? joined : ", " + joined;
        _lines[keyLine] = (text[..insertAt] + added + text[insertAt..], _lines[keyLine].Ending);
        return null;
    }

    private static int FirstValueIndex(string text, int from)
    {
        var index = from;
        while (index < text.Length && text[index] is ' ' or '\t')
        {
            index++;
        }

        return index;
    }

    /// <summary>The index of the bracket closing the flow list opened at <paramref name="open"/>, quotes honoured;
    /// -1 when it is not on the line or the list holds something nested.</summary>
    private static int FlowListEnd(string text, int open)
    {
        char? quote = null;
        for (var i = open + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is { } q)
            {
                if (q == '"' && c == '\\')
                {
                    i++;
                }
                else if (c == q && q == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                }
                else if (c == q)
                {
                    quote = null;
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '[' or '{':
                    return -1;
                case ']':
                    return i;
            }
        }

        return -1;
    }
}
