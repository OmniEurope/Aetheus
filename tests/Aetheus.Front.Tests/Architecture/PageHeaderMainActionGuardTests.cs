// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Recette R-30 rule 1 (R-109): on every page the main action sits top right in the page header and the
/// other actions in the "⋮" menu beside it, never a row of visible buttons in the content. For every
/// routable page (a file with <c>@page</c>), every project or server section body, and the few components
/// that are a page's whole body: (1) an action button lives in the header (<c>OmniPageHeader</c>, or the
/// <c>SectionContent</c> a detail layout renders there) or in a smaller zone that owns its action (a grid
/// cell, a form, a tab, an empty state, an alert); (2) the header shows one visible action, the rest in
/// its menu. A dismiss button, a menu trigger and a toggle are not actions. The grid rows and the dialog
/// footers are other rules' business and are not read here.
/// </summary>
public sealed partial class PageHeaderMainActionGuardTests
{
    private static readonly string Components = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Components");

    /// <summary>
    /// Components that are the whole body of a routed page without carrying <c>@page</c> themselves. A tab body
    /// (ModuleLinksTab, the Docker and Mail tabs) or an editor canvas (VisualPipelineEditor) is not listed: a tab is
    /// a zone with its own toolbar, and its page header already holds the page's main action.
    /// </summary>
    private static readonly string[] PageBodyComponents =
    [
        "Projects/MonitoredAppsList.razor",
    ];

    /// <summary>
    /// Tags whose content is a zone with its own action: a grid cell or edit row, a form, a tab, an empty state, an
    /// alert, a dialog, the danger zone of a settings page (R3-71).
    /// </summary>
    private static readonly string[] OwnActionZones =
    [
        "Template", "EditTemplate", "OmniTemplateForm", "EditForm", "OmniTabsItem", "EmptyTemplate", "EmptyState",
        "OmniAlert", "OmniDialog", "OmniSettingsDangerZone",
    ];

    /// <summary>
    /// A card that holds a form (a labelled field, a text area or a form element) owns the submit of that form.
    /// A card of tiles, filters or values does not: its action belongs in the page header.
    /// </summary>
    private static readonly Regex FormInCard = new(@"<(OmniFormField|OmniTemplateForm|OmniTextArea)\b", RegexOptions.CultureInvariant);

    /// <summary>Tags that make the header of a page.</summary>
    private static readonly string[] HeaderZones = ["OmniPageHeader", "SectionContent"];

    /// <summary>The header regions that show visible actions (the menu items live in <c>MenuContent</c>).</summary>
    private static readonly string[] HeaderActionRegions = ["ActionsContent", "PinnedActionsContent", "SectionContent"];

    /// <summary>
    /// Buttons kept in the content on purpose: (file, a text of the button's markup, the reason). Each must
    /// still match a button, so a removed or renamed one cannot excuse another.
    /// </summary>
    private static readonly (string File, string Markup, string Reason)[] AllowedContentActions =
    [
        ("Artifacts/ArtifactDetail.razor", "OnClick=\"CopyPathAsync\"",
            "R-79: the icon-only copy button of a value sits beside that value"),
        ("Artifacts/ArtifactDetail.razor", "OnClick=\"CopyChecksumAsync\"",
            "R-79: the icon-only copy button of a value sits beside that value"),
        ("Releases/ReleaseDetail.razor", "OnClick=\"@CopyVersionAsync\"",
            "R-31: the icon-only copy button of the release name sits beside that name in the Source tile"),
        ("Releases/ReleaseDetail.razor", "OnClick=\"@CopyTagAsync\"",
            "R-31: the icon-only copy button of the tag sits beside that tag in the Source tile"),
        ("Dashboards/Dashboards.razor", "EditDashboard(dashboard)",
            "an action of one dashboard tile, repeated per tile like a grid row's (Edit visible, the rest in the tile's menu)"),
        ("Git/GitRepositories.razor", "OnClick=\"@ClearFilters\"",
            "the reset of the filters sits beside the filter controls it resets, it is not an action of the page"),
        ("Projects/Projects.razor", "OnClick=\"@ClearFilters\"",
            "the reset of the filters sits beside the filter controls it resets, it is not an action of the page"),
        ("Logs/Logs.razor", "OnClick=\"@StartWatching\"",
            "the submit of the page's only inline form (the task to follow); there is nothing to watch before it is chosen"),
        ("Shared/NotFound.razor", "BackToHome",
            "the 404 page has no other content than its message and this one call to action"),
        ("Projects/ProjectDetailSections/ProjectBoardSection.razor", "DeleteAsync(card)",
            "an action of one kanban card, repeated per card like a grid row's"),
    ];

    [Fact]
    public void AnActionButton_OfAPage_SitsInThePageHeaderOrInAZoneOfItsOwn()
    {
        var used = new HashSet<int>();
        var offenders = new List<string>();
        var pages = PageFiles();
        var buttonsRead = 0;

        foreach (var file in pages)
        {
            var source = Masked(File.ReadAllText(file));
            var buttons = Located(source);
            buttonsRead += buttons.Count;
            var offending = ContentActions(source, buttons).ToList();
            foreach (var button in offending)
            {
                var allowed = Array.FindIndex(AllowedContentActions, a => a.File == Rel(file)
                    && button.Button.Markup.Contains(a.Markup, StringComparison.Ordinal));
                if (allowed >= 0) { used.Add(allowed); continue; }
                offenders.Add($"{Rel(file)}:{button.Button.Line} {ActionName(button.Button)}");
            }
        }

        var stale = AllowedContentActions.Where((_, i) => !used.Contains(i)).Select(a => $"{a.File}: {a.Markup} ({a.Reason})").ToList();
        Assert.True(pages.Count >= 100, $"Only {pages.Count} page files scanned: the scan is broken.");
        Assert.True(buttonsRead >= 250, $"Only {buttonsRead} buttons read on the pages: the scan is broken.");
        Assert.True(stale.Count == 0, "An allowed content action no longer matches a button; update or remove it:\n  " + string.Join("\n  ", stale));
        Assert.True(offenders.Count == 0,
            "Recette R-30 rule 1: the main action of a page sits top right in the page header (ActionsContent, or "
            + "<SectionContent SectionName=\"@PageHeaderActions.SectionName\"> in a detail layout) and the others in the "
            + "header's menu (MenuContent, OmniOverflowMenu or the menu of an OmniSplitButton), never visible buttons "
            + "in the content:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void ThePageHeader_ShowsOneVisibleAction_TheOthersAreInItsMenu()
    {
        var offenders = new List<string>();
        var headerActions = 0;

        foreach (var file in PageFiles())
        {
            var source = Masked(File.ReadAllText(file));
            var regions = HeaderActionRegions.SelectMany(tag => Zones(source, tag)).ToList();
            var popups = Zones(source, "OmniCard");
            var inHeader = Located(source)
                .Where(b => regions.Any(r => r.Start < b.Index && b.Index < r.End))
                .Where(b => !popups.Any(c => c.Start < b.Index && b.Index < c.End))
                .Where(b => !IsMenuTrigger(b.Button.Markup) && !IsToggle(b.Button.Markup))
                .Select(b => b.Button)
                .ToList();
            headerActions += inHeader.Count;
            var together = ButtonZoneScanner.RenderedTogether(inHeader);
            if (together.Count > 1)
            {
                offenders.Add($"{Rel(file)}: lines {string.Join(", ", together.Select(b => b.Line))}");
            }
        }

        Assert.True(headerActions >= 40, $"Only {headerActions} header actions read: the scan is broken.");
        Assert.True(offenders.Count == 0,
            "Recette R-30 rule 1: a page header shows its main action only, a dismiss goes to ShowBack and the other "
            + "actions to the menu:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheScanner_FlagsABodyBarAndASecondHeaderButton_AndSparesTheHeaderAndZonesOfTheirOwn()
    {
        const string bodyBar = """
            <OmniPageHeader Title="Tasks"><ActionsContent></ActionsContent></OmniPageHeader>
            <OmniStack Justify="OmniJustification.Between">
                <OmniTextBox aria-label="Search" />
                <OmniButton Variant="OmniButtonVariant.Primary" OnClick="@Create"><OmniIcon Name="add" /><span>@L["Create"]</span></OmniButton>
            </OmniStack>
            """;
        const string compliant = """
            <OmniPageHeader Title="Tasks">
                <ActionsContent>
                    <OmniButton Variant="OmniButtonVariant.Primary" OnClick="@Create"><OmniIcon Name="add" /><span>@L["Create"]</span></OmniButton>
                    <OmniOverflowMenu Label="@L["MoreActions"]"></OmniOverflowMenu>
                </ActionsContent>
            </OmniPageHeader>
            <OmniAlert><OmniButton OnClick="@Retry"><span>@L["Retry"]</span></OmniButton></OmniAlert>
            <OmniDataGridColumn><Template><OmniButton Variant="OmniButtonVariant.Secondary" OnClick="@Open"><span>@L["Open"]</span></OmniButton></Template></OmniDataGridColumn>
            <OmniTemplateForm><OmniButton OnClick="@Save"><span>@L["Save"]</span></OmniButton></OmniTemplateForm>
            """;
        const string crowdedHeader = """
            <OmniPageHeader Title="Tasks">
                <ActionsContent>
                    <OmniButton Variant="OmniButtonVariant.Primary" OnClick="@Run"><span>@L["Run"]</span></OmniButton>
                    <OmniButton Variant="OmniButtonVariant.Secondary" OnClick="@Back"><span>@L["GoBack"]</span></OmniButton>
                </ActionsContent>
            </OmniPageHeader>
            """;

        var bar = Masked(bodyBar);
        Assert.Single(ContentActions(bar, Located(bar)));
        var good = Masked(compliant);
        Assert.Empty(ContentActions(good, Located(good)));
        var crowded = Masked(crowdedHeader);
        var region = Zones(crowded, "ActionsContent").Single();
        Assert.Equal(2, ButtonZoneScanner.RenderedTogether(Located(crowded)
            .Where(b => region.Start < b.Index && b.Index < region.End).Select(b => b.Button).ToList()).Count);
    }

    /// <summary>The routable pages, the section bodies and the listed page bodies.</summary>
    private static IReadOnlyList<string> PageFiles()
    {
        var all = RepositoryScan.Enumerate(Components, "*.razor");
        var pages = all.Where(f => PageDirective().IsMatch(File.ReadAllText(f)))
            .Concat(all.Where(f => Rel(f).StartsWith("Projects/ProjectDetailSections/", StringComparison.Ordinal)
                                   && f.EndsWith("Section.razor", StringComparison.Ordinal)))
            .Concat(all.Where(f => Rel(f).StartsWith("Servers/ServerDetailSections/Server", StringComparison.Ordinal)
                                   && f.EndsWith("Section.razor", StringComparison.Ordinal)))
            .Concat(PageBodyComponents.Select(p => Path.Combine(Components, p.Replace('/', Path.DirectorySeparatorChar))))
            .Distinct()
            .ToList();
        Assert.All(PageBodyComponents, p => Assert.Contains(pages, f => Rel(f) == p));
        return pages;
    }

    /// <summary>The action buttons of a page that sit outside its header and every zone of its own.</summary>
    private static IEnumerable<LocatedButton> ContentActions(string source, List<LocatedButton> buttons)
    {
        var allowed = HeaderZones.Concat(OwnActionZones).SelectMany(tag => Zones(source, tag))
            .Concat(Zones(source, "OmniCard").Where(card => FormInCard.IsMatch(source[card.Start..card.End])))
            .ToList();
        return buttons
            .Where(b => !IsMenuTrigger(b.Button.Markup) && !IsToggle(b.Button.Markup) && !IsDismiss(b.Button.Markup))
            .Where(b => !allowed.Any(z => z.Start < b.Index && b.Index < z.End));
    }

    private sealed record LocatedButton(ButtonZoneScanner.ZoneButton Button, int Index);

    /// <summary>The buttons of a masked source with their character index, in document order.</summary>
    private static List<LocatedButton> Located(string source)
    {
        var located = new List<LocatedButton>();
        var cursor = 0;
        foreach (var button in ButtonZoneScanner.Buttons(source))
        {
            var index = source.IndexOf(button.Markup, cursor, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Could not locate the button of line {button.Line}.");
            located.Add(new LocatedButton(button, index));
            cursor = index + 1;
        }

        return located;
    }

    /// <summary>The source with its Razor comments blanked, line breaks kept (the scanner does the same).</summary>
    private static string Masked(string razor) =>
        RazorComment().Replace(razor, m => Regex.Replace(m.Value, @"[^\n]", " "));

    /// <summary>The character ranges of the elements named <paramref name="tag"/>, self-closing ones skipped.</summary>
    private static List<(int Start, int End)> Zones(string source, string tag)
    {
        var zones = new List<(int Start, int End)>();
        var open = new Stack<int>();
        foreach (Match token in Regex.Matches(source,
                     $@"<(?<close>/)?{tag}\b(?<attrs>(?:""[^""]*""|[^>""])*)>", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            if (token.Groups["close"].Success)
            {
                if (open.Count > 0) zones.Add((open.Pop(), token.Index));
            }
            else if (!token.Groups["attrs"].Value.TrimEnd().EndsWith('/'))
            {
                open.Push(token.Index);
            }
        }

        Assert.True(open.Count == 0, $"Unbalanced <{tag}>: the guard cannot read the page.");
        return zones;
    }

    private static readonly Regex DismissKey = new(@"^(Cancel|GoBack|Close|Previous|Back)$", RegexOptions.CultureInvariant);

    private static bool IsDismiss(string markup) =>
        LabelKey().Match(markup) is { Success: true } key && DismissKey.IsMatch(key.Groups["key"].Value);

    private static bool IsMenuTrigger(string markup) =>
        MenuTrigger().IsMatch(markup);

    private static bool IsToggle(string markup)
    {
        var open = OpeningTag(markup);
        return VariantComputed().IsMatch(open)
               || open.Contains("aria-pressed", StringComparison.Ordinal)
               || open.Contains("aria-expanded", StringComparison.Ordinal);
    }

    private static string ActionName(ButtonZoneScanner.ZoneButton button) =>
        LabelKey().Match(button.Markup) is { Success: true } key ? key.Groups["key"].Value : "(unnamed)";

    private static string OpeningTag(string markup) => Regex.Match(markup, @"^<\w+\b(?:""[^""]*""|[^>""])*>").Value;

    private static string Rel(string file) => Path.GetRelativePath(Components, file).Replace('\\', '/');

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RazorComment();

    [GeneratedRegex(@"^@page\s", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PageDirective();

    [GeneratedRegex(@"\bL\[""(?<key>\w+)""\]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LabelKey();

    [GeneratedRegex(@"L\[""(MoreActions|OtherActions)""\]|<OmniIcon\b[^>]*\bName\s*=\s*""(more_vert|more_horiz)""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MenuTrigger();

    [GeneratedRegex(@"\bVariant\s*=\s*""\s*@", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VariantComputed();
}
