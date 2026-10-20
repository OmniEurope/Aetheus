// kit-model ButtonZoneScanner 3
// SPDX-License-Identifier: EUPL-1.2
//
// Razor zone reader behind the STD-BTN guards "at most one blue button per zone" (detection 6) and
// "one visible main action, the rest in a menu" (detection 11, docs/code-rules.md).
// Copy it into a test project next to ButtonConventionGuardTests.cs and adapt only the namespace;
// keep the kit-model line so verify-rules.ps1 (STD-KITCOPY) reports the copy when this model moves.
//
// Recognize OE button tags, Variant colours and their defaults: Primary for OmniButton and
// Secondary for OmniSplitButton. ZoneWalker and StartsBranch keep each method within the cyclomatic
// complexity threshold of 15.

using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Reads a <c>.razor</c> source as markup for the <c>STD-BTN</c> zone rule (docs/code-rules.md): every
/// button with the element that directly holds it (its zone: a header's actions, a dialog footer, a
/// form's submit row, a toolbar...) and the <c>if</c>/<c>else</c> branches around it, since two buttons
/// in different branches of one <c>if</c> never render together. A button in the cell template of a data
/// grid column also carries its grid, whose columns make one row.
/// </summary>
internal static class ButtonZoneScanner
{
    /// <summary>The button tags of this app: OE's button and split button.</summary>
    private static readonly HashSet<string> ButtonTags = new(StringComparer.Ordinal) { "OmniButton", "OmniSplitButton" };

    /// <param name="Row">The grid whose row shows the button (its nearest <c>*DataGrid</c> element, reached
    /// through a <c>*DataGridColumn</c> and no template other than <c>Template</c>), 0 off a grid row.</param>
    internal sealed record ZoneButton(string Markup, int Line, int Zone, IReadOnlyList<(int If, int Branch)> Branches, int Row);

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
    };

    private static readonly Regex Comment = new(@"@\*.*?\*@", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex Tag = new(
        @"\G<(?<close>/)?(?<name>[A-Za-z][\w.:-]*)(?<attrs>(?:""[^""]*""|[^>""])*)>",
        RegexOptions.CultureInvariant);

    private static readonly Regex Branch = new(
        @"\G(?:@?else\s+if\s*\(|@?if\s*\(|@?else\s*\{)", RegexOptions.CultureInvariant);

    private static readonly Regex StyleAttr = new(
        @"^<\w+\b(?:""[^""]*""|[^>""])*?\bVariant\s*=\s*""(?<v>[^""]*)""", RegexOptions.CultureInvariant);

    /// <summary>The buttons of <paramref name="source"/>, in document order.</summary>
    internal static List<ZoneButton> Buttons(string source)
    {
        // Comments become spaces, their line breaks kept, so indices and line numbers stay true.
        source = Comment.Replace(source, m => Regex.Replace(m.Value, @"[^\n]", " "));
        return new ZoneWalker(source).Walk();
    }

    /// <summary>Tracks open elements and conditional frames in one source pass to identify
    /// simultaneously rendered button zones.</summary>
    private sealed class ZoneWalker(string source)
    {
        private readonly List<ZoneButton> _result = [];
        private readonly Stack<(string Name, int Id)> _elements = new();
        private readonly Stack<(int If, int Branch)> _frames = new();
        private readonly Dictionary<int, (int If, int Branch)> _lastClosed = [];
        private int _nextElement = 1;
        private int _nextIf;

        internal List<ZoneButton> Walk()
        {
            var i = 0;
            while (i < source.Length)
            {
                if (source[i] == '<')
                {
                    i = ReadTag(i);
                    continue;
                }

                if (StartsBranch(source, i, out var branch))
                {
                    var open = BranchBrace(source, branch);
                    if (open < 0)
                    {
                        break;
                    }

                    OpenBranch(branch);
                    i = open + 1;
                    continue;
                }

                ReadBrace(source[i]);
                i++;
            }

            return _result;
        }

        /// <summary>Reads the tag at <paramref name="i"/>; returns where the walk goes on.</summary>
        private int ReadTag(int i)
        {
            var tag = Tag.Match(source, i);
            if (!tag.Success)
            {
                return i + 1;
            }

            var name = tag.Groups["name"].Value;
            if (tag.Groups["close"].Success)
            {
                CloseElement(name);
                return i + tag.Length;
            }

            var selfClosing = tag.Groups["attrs"].Value.TrimEnd().EndsWith('/');
            if (ButtonTags.Contains(name))
            {
                return AddButton(i, tag.Length, name, selfClosing);
            }

            if (!selfClosing && !VoidElements.Contains(name))
            {
                _elements.Push((name, _nextElement++));
            }

            return i + tag.Length;
        }

        private void CloseElement(string name)
        {
            if (_elements.Any(e => e.Name == name))
            {
                while (_elements.Pop().Name != name)
                {
                }
            }
        }

        private int AddButton(int i, int tagLength, string name, bool selfClosing)
        {
            var end = i + tagLength;
            if (!selfClosing)
            {
                var close = source.IndexOf($"</{name}>", end, StringComparison.Ordinal);
                end = close < 0 ? end : close + name.Length + 3;
            }

            var line = source.AsSpan(0, i).Count('\n') + 1;
            _result.Add(new ZoneButton(source[i..end], line,
                _elements.Count > 0 ? _elements.Peek().Id : 0,
                _frames.Where(f => f.If >= 0).ToList(),
                RowOf(_elements)));
            return end;
        }

        private void OpenBranch(Match branch)
        {
            var isElse = branch.Value.Contains("else", StringComparison.Ordinal);
            var depth = _frames.Count;
            var frame = isElse && _lastClosed.TryGetValue(depth, out var previous)
                ? (previous.If, previous.Branch + 1)
                : (_nextIf++, 0);
            _frames.Push(frame);
        }

        private void ReadBrace(char c)
        {
            if (c == '{')
            {
                _frames.Push((-1, 0));
            }
            else if (c == '}' && _frames.Count > 0)
            {
                var closed = _frames.Pop();
                if (closed.If >= 0)
                {
                    _lastClosed[_frames.Count] = closed;
                }
            }
        }
    }

    /// <summary>True when an <c>if</c>, <c>else if</c> or <c>else</c> block starts at <paramref name="i"/>.</summary>
    private static bool StartsBranch(string source, int i, out Match branch)
    {
        var c = source[i];
        branch = (c == '@' || c == 'i' || c == 'e') && (i == 0 || !char.IsLetterOrDigit(source[i - 1]))
            ? Branch.Match(source, i)
            : Match.Empty;
        return branch.Success;
    }

    /// <summary>
    /// The index of the brace opening the branch <paramref name="branch"/> starts, or -1. An <c>if</c>
    /// condition is skipped first, its parentheses balanced and its string and character literals read
    /// whole, so a brace inside it (a property pattern <c>x is { } y</c>) is not taken for the branch.
    /// </summary>
    private static int BranchBrace(string source, Match branch)
    {
        if (!branch.Value.EndsWith('('))
        {
            return source.IndexOf('{', branch.Index);
        }

        var i = branch.Index + branch.Length;
        for (var depth = 1; depth > 0; i++)
        {
            if (i >= source.Length)
            {
                return -1;
            }

            var c = source[i];
            if (c is '"' or '\'')
            {
                i = LiteralEnd(source, i);
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
        }

        return source.IndexOf('{', i);
    }

    /// <summary>The index of the quote closing the literal opened at <paramref name="start"/>, escapes skipped.</summary>
    private static int LiteralEnd(string source, int start)
    {
        for (var i = start + 1; i < source.Length; i++)
        {
            if (source[i] == '\\')
            {
                i++;
            }
            else if (source[i] == source[start])
            {
                return i;
            }
        }

        return source.Length;
    }

    /// <summary>
    /// The grid whose row shows a button held by <paramref name="elements"/> (innermost first), or 0. An
    /// edit, header, footer or filter template never renders beside the row's display cells, so a button
    /// in one is off the row.
    /// </summary>
    private static int RowOf(Stack<(string Name, int Id)> elements)
    {
        string? template = null;
        var inCell = false;
        foreach (var (name, id) in elements)
        {
            if (inCell)
            {
                if (name.EndsWith("DataGrid", StringComparison.Ordinal))
                {
                    return id;
                }
            }
            else if (name.EndsWith("DataGridColumn", StringComparison.Ordinal))
            {
                if (template is not null && template != "Template")
                {
                    return 0;
                }

                inCell = true;
            }
            else if (name.EndsWith("Template", StringComparison.Ordinal))
            {
                template ??= name;
            }
        }

        return 0;
    }

    /// <summary>True when the two buttons sit in different branches of one <c>if</c>.</summary>
    internal static bool Exclusive(ZoneButton a, ZoneButton b) =>
        a.Branches.Any(x => b.Branches.Any(y => y.If == x.If && y.Branch != x.Branch));

    /// <summary>
    /// True when the button renders blue (the main action) whatever its state: <c>OmniButtonVariant.Primary</c>,
    /// or no <c>Variant</c> at all on an <c>OmniButton</c> (OE's default is Primary; an <c>OmniSplitButton</c>
    /// defaults to Secondary). A variant computed per state is a toggle, not a main action.
    /// </summary>
    internal static bool IsMain(ZoneButton button)
    {
        var style = StyleAttr.Match(button.Markup);
        return style.Success
            ? style.Groups["v"].Value == "OmniButtonVariant.Primary"
            : !button.Markup.StartsWith("<OmniSplitButton", StringComparison.Ordinal);
    }

    /// <summary>Every pair of main (blue) buttons of one zone that can render together.</summary>
    internal static IEnumerable<(ZoneButton First, ZoneButton Second)> SecondMainActions(string source)
    {
        foreach (var zone in Buttons(source).Where(IsMain).GroupBy(b => b.Zone))
        {
            var blue = zone.ToList();
            for (var a = 0; a < blue.Count; a++)
            {
                for (var b = a + 1; b < blue.Count; b++)
                {
                    if (!Exclusive(blue[a], blue[b]))
                    {
                        yield return (blue[a], blue[b]);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The largest set of <paramref name="buttons"/> that can render together: no two of them sit in
    /// different branches of one <c>if</c>. A zone holds a handful of buttons, so the search is exhaustive.
    /// </summary>
    internal static List<ZoneButton> RenderedTogether(IReadOnlyList<ZoneButton> buttons)
    {
        var best = new List<ZoneButton>();
        var current = new List<ZoneButton>();
        Extend(0);
        return best;

        void Extend(int next)
        {
            if (current.Count + (buttons.Count - next) <= best.Count)
            {
                return;
            }

            if (next == buttons.Count)
            {
                best = [.. current];
                return;
            }

            if (current.TrueForAll(b => !Exclusive(b, buttons[next])))
            {
                current.Add(buttons[next]);
                Extend(next + 1);
                current.RemoveAt(current.Count - 1);
            }

            Extend(next + 1);
        }
    }
}
