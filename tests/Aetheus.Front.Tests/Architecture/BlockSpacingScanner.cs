// SPDX-License-Identifier: EUPL-1.2
//
// Razor reader behind the block-spacing guard (docs/contracts/ui-patterns.md, "Spacing between blocks").
// It finds block components (card, alert, fieldset, grid, tabs...) that are siblings in one container
// and render one right after the other with nothing separating them: the container is not a gapped
// stack, and neither block carries a margin on the facing side.

using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Reads a <c>.razor</c> source as a tree of elements, remembering the <c>if</c>/<c>else</c> branches and
/// loops each element sits in, and reports the sibling block pairs that nothing separates.
/// </summary>
internal static class BlockSpacingScanner
{
    /// <summary>
    /// What counts as a block: a component that fills the width of its container and carries its own
    /// frame or heading. Text, badges, buttons, form fields and tiles inside a row are content, not blocks.
    /// </summary>
    internal static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "OmniCard", "OmniAlert", "OmniFieldset", "OmniDataGrid", "OmniTabs", "OmniSettingsSection",
        "OmniDescriptionList", "OmniChart", "OmniTimeline", "OmniCodeBlock", "OmniEmptyState",
    };

    /// <summary>Stack gaps that visibly separate two siblings (None and XSmall do not).</summary>
    private static readonly HashSet<string> VisibleGaps = new(StringComparer.Ordinal)
    {
        "Medium", "Large", "ExtraLarge",
    };

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
    };

    private static readonly Regex Comment = new(@"@\*.*?\*@", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex Tag = new(
        @"\G<(?<close>/)?(?<name>[A-Za-z][\w.:-]*)(?<attrs>(?:""[^""]*""|[^>""])*)>",
        RegexOptions.CultureInvariant);

    private static readonly Regex Keyword = new(
        @"\G@?(?<kw>else\s+if|if|foreach|for|while)\s*\(", RegexOptions.CultureInvariant);

    private static readonly Regex Else = new(@"\G@?else\s*\{", RegexOptions.CultureInvariant);

    private static readonly Regex ClassAttribute = new(
        @"\b[Cc]lass\s*=\s*""(?<v>[^""]*)""", RegexOptions.CultureInvariant);

    private static readonly Regex GapAttribute = new(
        @"\bGap\s*=\s*""(?<v>[^""]*)""", RegexOptions.CultureInvariant);

    private static readonly Regex SpacingUtility = new(
        @"^omni-u-(?:mt|mb|my)-(?:md|lg|xl|2xl)$", RegexOptions.CultureInvariant);

    private static readonly Regex VerticalMarginUtility = new(
        @"^omni-u-(?:mt|mb|my)-(?:xs|sm|md|lg|xl|2xl)$", RegexOptions.CultureInvariant);

    private static readonly Regex TextElement = new(
        @"\bElement\s*=\s*""(?<v>[^""]*)""", RegexOptions.CultureInvariant);

    private static readonly Regex InlineMargin = new(
        @"\bstyle\s*=\s*""[^""]*\bmargin", RegexOptions.CultureInvariant);

    /// <summary>One block pair, with where it is and why it is not separated.</summary>
    internal sealed record Violation(int Line, string First, string Second, string Parent);

    /// <summary>The class names a stylesheet turns into spacing: a gap on a container, a margin on an element.</summary>
    internal sealed record CssSpacing(IReadOnlySet<string> GapClasses, IReadOnlySet<string> MarginClasses)
    {
        internal static CssSpacing None { get; } = new(new HashSet<string>(), new HashSet<string>());
    }

    private sealed record Frame(int Id, string Kind, int Branch);

    private sealed record Child(string Name, string Attrs, int Line, IReadOnlyList<Frame> Frames);

    private sealed class Node(string name, string attrs, int frameDepth)
    {
        internal string Name { get; } = name;
        internal string Attrs { get; } = attrs;
        internal int FrameDepth { get; } = frameDepth;
        internal List<Child> Children { get; } = [];
    }

    /// <summary>The sibling block pairs of <paramref name="source"/> that nothing separates.</summary>
    internal static List<Violation> Scan(string source, CssSpacing css)
    {
        source = Comment.Replace(source, m => Regex.Replace(m.Value, @"[^\n]", " "));
        var violations = new List<Violation>();
        foreach (var node in new TreeReader(source).Read())
        {
            violations.AddRange(PairsOf(node, css));
        }

        return [.. violations.DistinctBy(v => (v.Line, v.Parent))];
    }

    /// <summary>The number of block components in <paramref name="source"/>, to prove a scan is not empty.</summary>
    internal static int CountBlocks(string source) =>
        new TreeReader(Comment.Replace(source, " ")).Read().Sum(n => n.Children.Count(c => BlockTags.Contains(c.Name)));

    /// <summary>
    /// Inline text with a vertical margin inside a container that lays its children out in flow
    /// (R-25, R-33): a <c>span</c>, <c>strong</c>, <c>em</c> or <c>small</c> (an <c>OmniText</c> that is
    /// not a paragraph) ignores <c>margin-top</c>/<c>margin-bottom</c>, so the spacing it asks for never
    /// appears and the title touches the table below it. Only the parents known to flow their children
    /// are judged (an <c>OmniCard</c>, a class-less <c>div</c>/<c>section</c>/<c>fieldset</c>); a flex or
    /// grid parent turns its children into blocks, and an element that is block already
    /// (<c>omni-u-block</c>, <c>Truncate</c>, a class the stylesheet gives a display) is fine.
    /// </summary>
    internal static List<Violation> ScanInlineMargins(string source, IReadOnlySet<string> displayClasses)
    {
        var violations = new List<Violation>();
        foreach (var node in new TreeReader(Comment.Replace(source, m => Regex.Replace(m.Value, @"[^\n]", " "))).Read())
        {
            if (!FlowsItsChildren(node))
            {
                continue;
            }

            violations.AddRange(node.Children
                .Where(child => IsInlineText(child) && HasVerticalMargin(child) && !IsBlockAlready(child, displayClasses))
                .Select(child => new Violation(child.Line, child.Name, "a vertical margin", node.Name)));
        }

        return violations;
    }

    /// <summary>
    /// The strict form of the rule (STD-SPACE, recette R-14): a block never carries an outer vertical margin,
    /// whatever its neighbours are. Blocks are separated only by the gap of their container, so a margin
    /// utility (<c>omni-u-mt-*</c>, <c>omni-u-mb-*</c>, <c>omni-u-my-*</c>), a class the stylesheet gives a
    /// vertical margin, or an inline margin on a block is a second source of space that the container cannot
    /// keep uniform.
    /// </summary>
    internal static List<Violation> ScanBlockMargins(string source, CssSpacing css)
    {
        var violations = new List<Violation>();
        foreach (var node in new TreeReader(Comment.Replace(source, m => Regex.Replace(m.Value, @"[^\n]", " "))).Read())
        {
            violations.AddRange(node.Children
                .Where(child => BlockTags.Contains(child.Name) && CarriesOuterMargin(child, css))
                .Select(child => new Violation(child.Line, child.Name, "an outer margin", node.Name)));
        }

        return [.. violations.DistinctBy(v => (v.Line, v.First))];
    }

    private static bool CarriesOuterMargin(Child element, CssSpacing css) =>
        InlineMargin.IsMatch(element.Attrs)
        || Classes(element.Attrs).Any(c => VerticalMarginUtility.IsMatch(c) || css.MarginClasses.Contains(c));

    private static bool FlowsItsChildren(Node node) =>
        node.Name == "OmniCard"
        || (node.Name is "div" or "section" or "fieldset" && !ClassAttribute.IsMatch(node.Attrs));

    private static bool IsInlineText(Child child)
    {
        if (child.Name is "span" or "strong" or "em" or "small")
        {
            return true;
        }

        if (child.Name != "OmniText")
        {
            return false;
        }

        var element = TextElement.Match(child.Attrs);
        return !element.Success
            || element.Groups["v"].Value is "OmniTextElement.Span" or "OmniTextElement.Strong"
                or "OmniTextElement.Emphasis" or "OmniTextElement.Small";
    }

    private static bool HasVerticalMargin(Child child) =>
        Classes(child.Attrs).Any(c => VerticalMarginUtility.IsMatch(c));

    private static bool IsBlockAlready(Child child, IReadOnlySet<string> displayClasses) =>
        Regex.IsMatch(child.Attrs, @"\bTruncate\b")
        || Classes(child.Attrs).Any(c => c == "omni-u-block" || displayClasses.Contains(c));

    private static IEnumerable<Violation> PairsOf(Node node, CssSpacing css)
    {
        var children = node.Children;
        for (var j = 0; j < children.Count; j++)
        {
            if (!BlockTags.Contains(children[j].Name))
            {
                continue;
            }

            foreach (var before in Predecessors(children, j))
            {
                if (!IsSeparated(node, before, children[j], css))
                {
                    yield return new Violation(children[j].Line, before.Name, children[j].Name, node.Name);
                }
            }
        }

        foreach (var (last, first) in LoopWraps(children))
        {
            if (!IsSeparated(node, last, first, css))
            {
                yield return new Violation(first.Line, last.Name, first.Name + " (next iteration)", node.Name);
            }
        }
    }

    /// <summary>
    /// The blocks that can render immediately before <c>children[j]</c>: walking back, an element in the
    /// other branch of a shared <c>if</c> is skipped, a block is reported, and the walk stops at the first
    /// element that always renders between (it is not conditional on anything the block is not).
    /// </summary>
    private static IEnumerable<Child> Predecessors(List<Child> children, int j)
    {
        var block = children[j];
        for (var i = j - 1; i >= 0; i--)
        {
            var before = children[i];
            if (Exclusive(before, block))
            {
                continue;
            }

            if (BlockTags.Contains(before.Name))
            {
                yield return before;
            }

            if (AlwaysRendersWith(before, block))
            {
                yield break;
            }
        }
    }

    /// <summary>The last and first element of every loop whose body starts and ends with a block.</summary>
    private static IEnumerable<(Child Last, Child First)> LoopWraps(List<Child> children)
    {
        var loops = children.SelectMany(c => c.Frames.Where(f => f.Kind == "loop")).Select(f => f.Id).Distinct();
        foreach (var loop in loops)
        {
            var body = children.Where(c => c.Frames.Any(f => f.Id == loop)).ToList();
            if (BlockTags.Contains(body[0].Name) && BlockTags.Contains(body[^1].Name))
            {
                yield return (body[^1], body[0]);
            }
        }
    }

    private static bool Exclusive(Child a, Child b) =>
        a.Frames.Any(x => x.Kind == "if" && b.Frames.Any(y => y.Id == x.Id && y.Branch != x.Branch));

    private static bool AlwaysRendersWith(Child before, Child block) =>
        before.Frames.All(f => block.Frames.Any(g => g.Id == f.Id && g.Branch == f.Branch));

    private static bool IsSeparated(Node parent, Child first, Child second, CssSpacing css) =>
        ParentGaps(parent, css) || HasMargin(first, css) || HasMargin(second, css);

    private static bool ParentGaps(Node parent, CssSpacing css)
    {
        if (parent.Name is "OmniStack" or "OmniRow" or "OmniTileGrid")
        {
            return StackGapIsVisible(parent.Attrs);
        }

        return Classes(parent.Attrs).Any(css.GapClasses.Contains);
    }

    /// <summary>A stack (or an OmniTileGrid, whose Gap defaults to Medium too) separates its items unless its Gap is None or XSmall; an absent Gap is OE's Medium
    /// and a computed Gap cannot be judged from the markup, so both pass.</summary>
    private static bool StackGapIsVisible(string attrs)
    {
        var gap = GapAttribute.Match(attrs);
        if (!gap.Success)
        {
            return true;
        }

        var value = gap.Groups["v"].Value.TrimStart('@');
        return !value.StartsWith("OmniSpacing.", StringComparison.Ordinal)
            || VisibleGaps.Contains(value["OmniSpacing.".Length..]);
    }

    private static bool HasMargin(Child element, CssSpacing css) =>
        InlineMargin.IsMatch(element.Attrs)
        || Classes(element.Attrs).Any(c => SpacingUtility.IsMatch(c) || css.MarginClasses.Contains(c));

    private static IEnumerable<string> Classes(string attrs) =>
        ClassAttribute.Matches(attrs).SelectMany(m =>
            m.Groups["v"].Value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Walks the source once, building every element's child list with the frames around each child.</summary>
    private sealed class TreeReader(string source)
    {
        private readonly List<Node> _done = [];
        private readonly Stack<Node> _open = new();
        private readonly List<Frame> _frames = [];
        private readonly Dictionary<int, Frame> _lastClosed = [];
        private int _nextFrame = 1;

        internal List<Node> Read()
        {
            _open.Push(new Node("", "", 0));
            var i = 0;
            while (i < source.Length && !StartsCode(i))
            {
                i = source[i] == '<' ? ReadTag(i) : ReadCodeOrText(i);
            }

            while (_open.Count > 0)
            {
                _done.Add(_open.Pop());
            }

            return _done;
        }

        private bool StartsCode(int i) =>
            source[i] == '@'
            && (string.CompareOrdinal(source, i, "@code", 0, 5) == 0
                || string.CompareOrdinal(source, i, "@functions", 0, 10) == 0);

        private int ReadTag(int i)
        {
            var tag = Tag.Match(source, i);
            if (!tag.Success || IsGenericArgument(i, tag))
            {
                return i + 1;
            }

            var name = tag.Groups["name"].Value;
            if (tag.Groups["close"].Success)
            {
                Close(name);
                return i + tag.Length;
            }

            var attrs = tag.Groups["attrs"].Value;
            var line = source.AsSpan(0, i).Count('\n') + 1;
            var parent = _open.Peek();
            parent.Children.Add(new Child(name, attrs, line, [.. _frames.Skip(parent.FrameDepth)]));
            if (!attrs.TrimEnd().EndsWith('/') && !VoidElements.Contains(name))
            {
                _open.Push(new Node(name, attrs, _frames.Count));
            }

            return i + tag.Length;
        }

        /// <summary>True for the type argument of generic C# (<c>List&lt;string&gt;</c>): an opening tag glued to the word before it.</summary>
        private bool IsGenericArgument(int i, Match tag) =>
            !tag.Groups["close"].Success && i > 0 && char.IsLetterOrDigit(source[i - 1]);

        private void Close(string name)
        {
            if (!_open.Any(n => n.Name == name))
            {
                return;
            }

            Node node;
            do
            {
                node = _open.Pop();
                _done.Add(node);
            }
            while (node.Name != name);
        }

        private int ReadCodeOrText(int i)
        {
            var c = source[i];
            var atWordStart = i == 0 || !char.IsLetterOrDigit(source[i - 1]);
            if (atWordStart && (c == '@' || char.IsLetter(c)))
            {
                var next = ReadFrameStart(i);
                if (next > i)
                {
                    return next;
                }
            }

            if (c == '{')
            {
                _frames.Add(new Frame(_nextFrame++, "other", 0));
            }
            else if (c == '}' && _frames.Count > 0)
            {
                CloseFrame();
            }

            return i + 1;
        }

        private void CloseFrame()
        {
            var closed = _frames[^1];
            _frames.RemoveAt(_frames.Count - 1);
            if (closed.Kind != "other")
            {
                _lastClosed[_frames.Count] = closed;
            }
        }

        /// <summary>Opens the frame of an <c>if</c>, <c>else</c> or loop starting at <paramref name="i"/>; returns where to go on, or <paramref name="i"/> when none starts.</summary>
        private int ReadFrameStart(int i)
        {
            var elseMatch = Else.Match(source, i);
            if (elseMatch.Success)
            {
                var previous = _lastClosed.TryGetValue(_frames.Count, out var p) ? p : null;
                _frames.Add(previous is null
                    ? new Frame(_nextFrame++, "if", 0)
                    : new Frame(previous.Id, "if", previous.Branch + 1));
                return i + elseMatch.Length;
            }

            var keyword = Keyword.Match(source, i);
            if (!keyword.Success)
            {
                return i;
            }

            var open = BraceAfterCondition(keyword);
            if (open < 0)
            {
                return i;
            }

            var kw = keyword.Groups["kw"].Value;
            var isLoop = kw is "foreach" or "for" or "while";
            var isElse = kw.StartsWith("else", StringComparison.Ordinal);
            var previousIf = isElse && _lastClosed.TryGetValue(_frames.Count, out var q) ? q : null;
            _frames.Add(isLoop
                ? new Frame(_nextFrame++, "loop", 0)
                : previousIf is null ? new Frame(_nextFrame++, "if", 0) : new Frame(previousIf.Id, "if", previousIf.Branch + 1));
            return open + 1;
        }

        /// <summary>The index of the brace opening the body after the parenthesised condition, or -1.</summary>
        private int BraceAfterCondition(Match keyword)
        {
            var i = keyword.Index + keyword.Length;
            for (var depth = 1; depth > 0; i++)
            {
                if (i >= source.Length)
                {
                    return -1;
                }

                if (source[i] is '"' or '\'')
                {
                    i = LiteralEnd(i);
                }
                else if (source[i] == '(')
                {
                    depth++;
                }
                else if (source[i] == ')')
                {
                    depth--;
                }
            }

            while (i < source.Length && char.IsWhiteSpace(source[i]))
            {
                i++;
            }

            return i < source.Length && source[i] == '{' ? i : -1;
        }

        private int LiteralEnd(int start)
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
    }
}
