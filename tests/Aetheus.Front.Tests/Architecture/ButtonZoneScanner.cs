// kit-model ButtonZoneScanner 1
// SPDX-License-Identifier: EUPL-1.2
//
// Razor zone reader behind the STD-BTN guard "at most one blue button per zone" (docs/code-rules.md).
// Copy it into a test project next to ButtonConventionGuardTests.cs and adapt only the namespace;
// keep the kit-model line so verify-rules.ps1 (STD-KITCOPY) reports the copy when this model moves.
//
// Aetheus copy: the model reads the button of the kit's example app, from a component library Aetheus
// no longer uses. Aetheus renders OE buttons, so the three button-library points are adapted: the
// button tags (OmniButton and OmniSplitButton), the colour attribute (Variant) and the default colour
// in IsMain (an OmniButton defaults to Primary, an OmniSplitButton to Secondary). The walk of Buttons
// is a ZoneWalker class split by what starts at the current character (tag, branch, brace), and the
// branch test is StartsBranch, both unchanged in behaviour, to keep every method under the grade-A
// cyclomatic threshold (15) of the nightly quality grade (recette R2-107; the model's Buttons is at 26).

using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Reads a <c>.razor</c> source as markup for the <c>STD-BTN</c> zone rule (docs/code-rules.md): every
/// button with the element that directly holds it (its zone: a header's actions, a dialog footer, a
/// form's submit row, a toolbar...) and the <c>if</c>/<c>else</c> branches around it, since two buttons
/// in different branches of one <c>if</c> never render together.
/// </summary>
internal static class ButtonZoneScanner
{
    /// <summary>The button tags of this app: OE's button and split button.</summary>
    private static readonly HashSet<string> ButtonTags = new(StringComparer.Ordinal) { "OmniButton", "OmniSplitButton" };

    internal sealed record ZoneButton(string Markup, int Line, int Zone, IReadOnlyList<(int If, int Branch)> Branches);

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

    /// <summary>
    /// One pass over a markup source: the open elements (a button's zone is the innermost one) and the
    /// <c>if</c>/<c>else</c> frames around the current position. Recette R2-107: the walk is split
    /// by what starts at the current character (a tag, a branch, a brace) so no method of it goes over
    /// the grade-A cyclomatic threshold (15) the nightly quality grade applies to every source.
    /// </summary>
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
                    var open = source.IndexOf('{', i);
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
                _frames.Where(f => f.If >= 0).ToList()));
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
}
