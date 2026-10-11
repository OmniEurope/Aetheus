// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// SITE-FUNC-HOME-CAPABILITY-001: an OmniDataGrid row runs its row click on both <c>click</c> and
/// <c>keydown</c> (Enter/Space), and OmniButton, OmniLink and a plain link stop neither. A control in a cell
/// of a clickable row that does not stop BOTH events lets the row handler run after its own and win the
/// navigation. This guard reads every <c>.razor</c> file, finds the grids with a row click (<c>RowClick</c>
/// on AetheusDataGrid, <c>OnRowClick</c> on OmniDataGrid) and asserts that every interactive control in a
/// column <c>Template</c> sits on, or under, an element carrying both <c>@onclick:stopPropagation</c> and
/// <c>@onkeydown:stopPropagation</c>. It follows the Aetheus components a template uses (ShortId, a column
/// set such as AnalysisFindingColumns) into their own markup, transitively, and applies the same rule to a
/// hand-written <c>&lt;tr @onclick&gt;</c> (OnboardingStepList).
/// Limits: a column template written in C#, or an OE component rendering its own controls, is out of its
/// reach; the per-component bUnit tests built on RowEventIsolation read the rendered DOM for those.
/// </summary>
public class GridRowControlIsolationAuditTests
{
    private static readonly HashSet<string> InteractiveTags = new(StringComparer.Ordinal)
    {
        "a", "button", "input", "select", "textarea", "NavLink",
        "OmniButton", "OmniLink", "OmniCheckBox", "OmniSwitch", "OmniSelect", "OmniSplitButton",
        "OmniTextBox", "OmniNumeric"
    };

    // A stop modifier written on a component reaches the DOM only when the component splats its extra
    // attributes on the element that receives the event: true for these (NavLink on its a, OmniStack on
    // its div, OmniDropDown on its select), unknown for any other.
    private static readonly HashSet<string> ComponentsForwardingStops = new(StringComparer.Ordinal)
    {
        "NavLink", "OmniStack", "OmniDropDown"
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "input", "br", "img", "hr", "meta", "link", "col", "wbr", "source"
    };

    [Fact]
    public void InteractiveControlsInClickableRows_StopClickAndKeydown()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var files = RepositoryScan.Enumerate(front, "*.razor")
            .Select(file => (Path: Path.GetRelativePath(front, file).Replace('\\', '/'), Root: Parse(File.ReadAllText(file))))
            .ToList();
        var components = files
            .GroupBy(f => Path.GetFileNameWithoutExtension(f.Path), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var violations = new List<string>();
        var followed = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        var clickableGrids = 0;
        var clickableRows = 0;

        void CheckScope(string path, Element scope)
        {
            foreach (var element in Descendants(scope))
            {
                if (IsInteractive(element.Name) && !Isolated(element, scope))
                    violations.Add($"{path}: <{element.Name}> {Shorten(element.Tag)}");
                if (components.ContainsKey(element.Name) && followed.Add(element.Name))
                    pending.Enqueue(element.Name);
            }
        }

        foreach (var (path, root) in files)
        {
            foreach (var grid in Descendants(root).Where(IsClickableGrid))
            {
                clickableGrids++;
                foreach (var template in Descendants(grid).Where(e => e.Name == "Template"))
                    CheckScope(path, template);
                // A column set (AnalysisFindingColumns) given to the grid brings its own templates.
                foreach (var columns in Descendants(grid).Where(e => e.Name == "Columns"))
                {
                    foreach (var child in Descendants(columns).Where(e => components.ContainsKey(e.Name) && followed.Add(e.Name)))
                        pending.Enqueue(child.Name);
                }
            }

            // A hand-written table row with its own click (OnboardingStepList) has the same trap.
            foreach (var row in Descendants(root).Where(e => e.Name == "tr" && Regex.IsMatch(e.Tag, @"\s@onclick\s*=")))
            {
                clickableRows++;
                CheckScope(path, row);
            }
        }

        while (pending.TryDequeue(out var name))
        {
            var (path, root) = components[name];
            CheckScope(path, root);
        }

        // The scan must keep finding the grids and cell components it exists for, or it passes over nothing.
        Assert.True(clickableGrids >= 20, $"Only {clickableGrids} clickable grids found: the parser no longer reads the markup.");
        Assert.True(clickableRows >= 1, "No hand-written clickable table row found: the parser no longer reads the markup.");
        Assert.Contains("ShortId", followed);
        Assert.Contains("AnalysisFindingColumns", followed);
        Assert.True(violations.Count == 0,
            "A control in a clickable grid row must stop both click and keydown (wrap it in an element with "
            + "@onclick:stopPropagation=\"true\" @onkeydown:stopPropagation=\"true\"):\n  "
            + string.Join("\n  ", violations.Distinct()));
    }

    [Fact]
    public void Parser_FindsAnUnisolatedLinkAndAcceptsAWrappedOne()
    {
        const string markup = """
            <AetheusDataGrid TItem="Row" RowClick="@(r => Nav.NavigateTo($"/x/{r.Id}"))">
              <Columns>
                <OmniDataGridColumn TItem="Row" Title="@L["A"]"><Template Context="r">
                  @if (r.Id > 0) { <OmniLink Href="@($"/a/{r.Id}")">a</OmniLink> }
                  <span @onclick:stopPropagation="true" @onkeydown:stopPropagation="true"><OmniButton OnClick="@(() => Go(r))">b</OmniButton></span>
                  <span @onclick:stopPropagation="true"><a href="/c">c</a></span>
                  <OmniCard @onclick:stopPropagation="true" @onkeydown:stopPropagation="true"><OmniButton>d</OmniButton></OmniCard>
                </Template></OmniDataGridColumn>
              </Columns>
            </AetheusDataGrid>
            """;
        var root = Parse(markup);
        var grid = Assert.Single(Descendants(root), IsClickableGrid);
        var template = Assert.Single(Descendants(grid), e => e.Name == "Template");
        var verdicts = Descendants(template).Where(e => IsInteractive(e.Name))
            .Select(c => (c.Name, Isolated(c, template))).ToList();
        Assert.Equal([("OmniLink", false), ("OmniButton", true), ("a", false), ("OmniButton", false)], verdicts);
    }

    private static bool IsClickableGrid(Element element) =>
        element.Name is "AetheusDataGrid" or "OmniDataGrid"
        && Regex.IsMatch(element.Tag, @"\s(On)?RowClick\s*=");

    private static bool IsInteractive(string name) =>
        InteractiveTags.Contains(name) || name.StartsWith("OmniMenu", StringComparison.Ordinal)
        || name.StartsWith("OmniDropDown", StringComparison.Ordinal);

    private static bool Isolated(Element control, Element scope)
    {
        bool click = false, keydown = false;
        for (var current = control; current is not null && current != scope; current = current.Parent)
        {
            if (!char.IsLower(current.Name[0]) && !ComponentsForwardingStops.Contains(current.Name)) continue;
            click |= HasStop(current.Tag, "onclick");
            keydown |= HasStop(current.Tag, "onkeydown");
        }

        return click && keydown;
    }

    private static bool HasStop(string tag, string eventName) =>
        Regex.IsMatch(tag, $@"@{eventName}:stopPropagation(\s*=\s*""(true|@true)"")?(?=[\s/>])");

    private static string Shorten(string tag)
    {
        var line = Regex.Replace(tag, @"\s+", " ");
        return line.Length <= 120 ? line : line[..120] + "...";
    }

    private static IEnumerable<Element> Descendants(Element element)
    {
        foreach (var child in element.Children)
        {
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }

    /// <summary>
    /// A tag-stack reader for Razor markup. Inside a tag it counts parentheses, brackets and braces so the
    /// <c>&gt;</c> of a lambda or the quotes of <c>@L["Key"]</c> and <c>@($"...")</c> do not end it.
    /// </summary>
    private static Element Parse(string source)
    {
        source = Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline);
        source = Regex.Replace(source, @"<!--.*?-->", " ", RegexOptions.Singleline);
        var root = new Element("#root", string.Empty, null);
        var current = root;
        var i = 0;
        while (i < source.Length)
        {
            if (source[i] != '<' || i + 1 >= source.Length || !(char.IsLetter(source[i + 1]) || source[i + 1] == '/'))
            {
                i++;
                continue;
            }

            var end = TagEnd(source, i);
            if (end < 0) break;
            var tag = source[i..(end + 1)];
            i = end + 1;
            var nameMatch = Regex.Match(tag, @"^</?([A-Za-z][\w.:-]*)");
            if (!nameMatch.Success) continue;
            var name = nameMatch.Groups[1].Value;
            if (tag.StartsWith("</", StringComparison.Ordinal))
            {
                for (var open = current; open.Parent is not null; open = open.Parent)
                {
                    if (open.Name != name) continue;
                    current = open.Parent;
                    break;
                }

                continue;
            }

            var element = new Element(name, tag, current);
            current.Children.Add(element);
            if (!tag.EndsWith("/>", StringComparison.Ordinal) && !VoidTags.Contains(name))
                current = element;
        }

        return root;
    }

    private static int TagEnd(string source, int start)
    {
        var depth = 0;
        var inQuote = false;
        for (var j = start + 1; j < source.Length; j++)
        {
            var c = source[j];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && c == '"') inQuote = !inQuote;
            else if (depth == 0 && !inQuote && c == '>') return j;
        }

        return -1;
    }

    private sealed class Element(string name, string tag, Element? parent)
    {
        public string Name { get; } = name;
        public string Tag { get; } = tag;
        public Element? Parent { get; } = parent;
        public List<Element> Children { get; } = [];
    }
}
