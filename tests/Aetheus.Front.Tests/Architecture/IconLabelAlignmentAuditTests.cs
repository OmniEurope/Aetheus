// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Application-composed icon-label rows center icons vertically; OE owns alignment inside
/// its own controls.</summary>
public sealed class IconLabelAlignmentAuditTests
{
    private static readonly string FrontRoot = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");

    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"
    };

    /// <summary>One element of the markup: its name, its opening tag, its parent's opening tag (null at
    /// the root), and its direct child element names.</summary>
    private sealed record Element(string Name, string Tag, int Line, string? ParentTag, List<string> Children);

    [Fact]
    public void EveryHorizontalStackHoldingAnIcon_CentresIt()
    {
        var offenders = new List<string>();
        var stacks = 0;
        foreach (var (file, element) in Elements())
        {
            if (element.Name != "OmniStack" || !element.Tag.Contains("OmniStackOrientation.Horizontal", StringComparison.Ordinal))
                continue;
            stacks++;
            if (!element.Children.Contains("OmniIcon")) continue;
            if (!element.Tag.Contains("Align=\"OmniAlignment.Center\"", StringComparison.Ordinal))
                offenders.Add($"{file}:{element.Line}");
        }

        Assert.True(stacks > 0, "No horizontal OmniStack found: the scan protects nothing.");
        Assert.True(offenders.Count == 0,
            "A horizontal OmniStack holding an OmniIcon beside its label declares Align=\"OmniAlignment.Center\" "
            + "(recette R-339), otherwise the icon sits above the middle of the text:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void NoAppCssRowAlignsAnIconOnItsBaselineOrStart()
    {
        var css = Regex.Replace(File.ReadAllText(Path.Combine(FrontRoot, "wwwroot", "css", "app.css")),
            @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var misaligned = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match rule in Regex.Matches(css, @"(?<selector>[^{}@]+)\{(?<body>[^{}]*)\}"))
        {
            var body = rule.Groups["body"].Value;
            if (!Regex.IsMatch(body, @"(?<![-\w])align-items\s*:\s*(baseline|flex-start|start|first baseline|last baseline)\b"))
                continue;
            // A column stacks the icon above the text: align-items then places it sideways, not vertically.
            if (Regex.IsMatch(body, @"(?<![-\w])flex-direction\s*:\s*column")) continue;
            foreach (var selector in rule.Groups["selector"].Value.Split(','))
            {
                // The element the rule lays out is the last compound of the selector; only a plain
                // class there (no child combinator after it) names a row an element carries.
                var last = Regex.Match(selector.Trim(), @"\.(?<name>[A-Za-z0-9_-]+)(?::[a-z-]+(?:\([^)]*\))?)*$");
                if (last.Success) misaligned.Add(last.Groups["name"].Value);
            }
        }

        var offenders = new List<string>();
        foreach (var (file, element) in Elements())
        {
            if (!element.Children.Contains("OmniIcon")) continue;
            var classes = ClassesOf(element.Tag);
            foreach (var name in classes.Where(misaligned.Contains))
                offenders.Add($"{file}:{element.Line} .{name}");
        }

        Assert.True(offenders.Count == 0,
            "These elements hold an icon beside a label but app.css aligns their row on the baseline or the "
            + "start edge; centre it (align-items: center, recette R-339):\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>In centered flex rows, asymmetric negative vertical margins offset icon margin
    /// boxes. Equal top and bottom negative margins preserve centering.</summary>
    [Fact]
    public void NoIconInACentredRowCarriesAnAsymmetricNegativeVerticalMargin()
    {
        var rules = CssRules();
        var centredRows = rules
            .Where(rule => Regex.IsMatch(rule.Body, @"(?<![-\w])display\s*:\s*(inline-)?flex\b")
                && Regex.IsMatch(rule.Body, @"(?<![-\w])align-items\s*:\s*center\b")
                && !Regex.IsMatch(rule.Body, @"(?<![-\w])flex-direction\s*:\s*column"))
            .SelectMany(rule => rule.Classes)
            .ToHashSet(StringComparer.Ordinal);
        var offset = rules
            .Where(rule => VerticalMargins(rule.Body) is var (top, bottom) && top != bottom && (top.StartsWith('-') || bottom.StartsWith('-')))
            .SelectMany(rule => rule.Classes)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(centredRows.Count > 0, "No centred flex row found in app.css: the scan protects nothing.");

        var offenders = new List<string>();
        foreach (var (file, element) in Elements())
        {
            var isIcon = element.Name is "OmniIcon" or "OmniButton"
                || element.Children.Contains("OmniIcon") || element.Children.Contains("OmniButton");
            if (!isIcon || element.ParentTag is not { } parentTag) continue;
            var parentCentred = ClassesOf(parentTag).Any(centredRows.Contains)
                || (parentTag.StartsWith("<OmniStack", StringComparison.Ordinal)
                    && parentTag.Contains("OmniStackOrientation.Horizontal", StringComparison.Ordinal)
                    && parentTag.Contains("Align=\"OmniAlignment.Center\"", StringComparison.Ordinal));
            if (!parentCentred) continue;
            foreach (var name in ClassesOf(element.Tag).Where(offset.Contains))
                offenders.Add($"{file}:{element.Line} .{name}");
        }

        Assert.True(offenders.Count == 0,
            "These icons sit in a centred row but app.css gives them unequal top and bottom margins, one "
            + "negative, which moves them off the middle of the label; make the vertical margins equal "
            + "(recette R-339):\n  " + string.Join("\n  ", offenders));
    }

    private sealed record CssRule(IReadOnlyList<string> Classes, string Body);

    /// <summary>Every app.css rule with the plain classes its selectors lay out (last compound only).</summary>
    private static List<CssRule> CssRules()
    {
        var css = Regex.Replace(File.ReadAllText(Path.Combine(FrontRoot, "wwwroot", "css", "app.css")),
            @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var rules = new List<CssRule>();
        foreach (Match rule in Regex.Matches(css, @"(?<selector>[^{}@]+)\{(?<body>[^{}]*)\}"))
        {
            var classes = rule.Groups["selector"].Value.Split(',')
                .Select(selector => Regex.Match(selector.Trim(), @"\.(?<name>[A-Za-z0-9_-]+)(?::[a-z-]+(?:\([^)]*\))?)*$"))
                .Where(match => match.Success)
                .Select(match => match.Groups["name"].Value)
                .ToList();
            if (classes.Count > 0) rules.Add(new CssRule(classes, rule.Groups["body"].Value));
        }

        return rules;
    }

    /// <summary>The top and bottom margins a rule body declares (the side it leaves out reads as 0),
    /// or null when it declares no vertical margin. A zero in any unit normalises to "0".</summary>
    private static (string Top, string Bottom)? VerticalMargins(string body)
    {
        string? top = null, bottom = null;
        foreach (Match declaration in Regex.Matches(body, @"(?<![-\w])(?<property>margin(?:-top|-bottom|-block(?:-start|-end)?)?)\s*:\s*(?<value>[^;]+)"))
        {
            var values = declaration.Groups["value"].Value.Replace("!important", string.Empty, StringComparison.Ordinal)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length == 0) continue;
            switch (declaration.Groups["property"].Value)
            {
                case "margin":
                    top = values[0];
                    bottom = values.Length >= 3 ? values[2] : values[0];
                    break;
                case "margin-block":
                    top = values[0];
                    bottom = values.Length >= 2 ? values[1] : values[0];
                    break;
                case "margin-top" or "margin-block-start":
                    top = values[0];
                    break;
                default:
                    bottom = values[0];
                    break;
            }
        }

        if (top is null && bottom is null) return null;
        return (Normalise(top), Normalise(bottom));

        static string Normalise(string? value) =>
            value is null || Regex.IsMatch(value, @"^-?0(\.0+)?[a-z%]*$") ? "0" : value;
    }

    private static IEnumerable<string> ClassesOf(string tag)
    {
        var match = Regex.Match(tag, @"\s(?:class|Class)=""(?<value>[^""]*)""");
        return match.Success
            ? match.Groups["value"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(name => !name.Contains('@'))
            : [];
    }

    private static IEnumerable<(string File, Element Element)> Elements()
    {
        foreach (var path in RepositoryScan.Enumerate(FrontRoot, "*.razor"))
        {
            var text = RazorComment.Replace(File.ReadAllText(path),
                comment => new string(comment.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()));
            var relative = Path.GetRelativePath(FrontRoot, path);
            foreach (var element in Parse(text))
                yield return (relative, element);
        }
    }

    /// <summary>
    /// A tolerant scan of the element tree: opening, closing and self-closing tags, quote-aware so a
    /// lambda's arrow inside an attribute never ends a tag. Razor code between tags is ignored, which is
    /// what the rendered tree does too: an <c>@if</c> around an icon still makes it a direct child.
    /// </summary>
    private static List<Element> Parse(string text)
    {
        var all = new List<Element>();
        var open = new Stack<Element>();
        var index = 0;
        while ((index = text.IndexOf('<', index)) >= 0)
        {
            if (index + 1 >= text.Length) break;
            var closing = text[index + 1] == '/';
            var nameMatch = Regex.Match(text[(index + (closing ? 2 : 1))..Math.Min(text.Length, index + 80)], @"^[A-Za-z][A-Za-z0-9_.:-]*");
            // A generic type argument (List<string>, EventCallback<T>) follows an identifier; a tag never does.
            var generic = index > 0 && (char.IsLetterOrDigit(text[index - 1]) || text[index - 1] == '_');
            if (!nameMatch.Success || generic)
            {
                index++;
                continue;
            }

            var end = TagEnd(text, index);
            if (end < 0) break;
            var name = nameMatch.Value;
            if (closing)
                CloseUpTo(open, name);
            else
                OpenElement(all, open, text, index, end, name);

            index = end + 1;
        }

        return all;
    }

    /// <summary>A closing tag closes every element opened since its matching opener.</summary>
    private static void CloseUpTo(Stack<Element> open, string name)
    {
        while (open.Count > 0)
        {
            var top = open.Pop();
            if (string.Equals(top.Name, name, StringComparison.Ordinal)) break;
        }
    }

    /// <summary>An opening tag: recorded as a child of the open element, and opened unless it closes itself.</summary>
    private static void OpenElement(List<Element> all, Stack<Element> open, string text, int index, int end, string name)
    {
        var tag = text[index..(end + 1)];
        var element = new Element(name, tag, text.AsSpan(0, index).Count('\n') + 1, open.Count > 0 ? open.Peek().Tag : null, []);
        all.Add(element);
        if (open.Count > 0) open.Peek().Children.Add(name);
        var selfClosing = text[end - 1] == '/' || VoidElements.Contains(name);
        if (!selfClosing) open.Push(element);
    }

    private static int TagEnd(string text, int start)
    {
        char? quote = null;
        for (var i = start + 1; i < text.Length; i++)
        {
            var current = text[i];
            if (quote is not null)
            {
                if (current == quote) quote = null;
                continue;
            }

            if (current == '"') { quote = current; continue; }
            if (current == '>') return i;
        }

        return -1;
    }
}
