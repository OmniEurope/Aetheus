// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Enforces role-based button colours, specific destructive confirmation verbs and at most
/// one simultaneously visible primary action per zone, preserving documented exceptions, and one
/// visible action per zone besides its dismiss action and menu trigger, at most two on a grid row
/// (detection 11, owner decision of 2026-10-12). Labels
/// establish roles before handler names, while main-action selection remains an audit
/// judgment.</summary>
public class ButtonRoleColourGuardTests
{
    private const string Danger = "OmniButtonVariant.Danger";

    /// <summary>
    /// The green buttons the user chose against the rule, each recorded where it is applied (file,
    /// the exact markup, the recette line). Nothing else may be green.
    /// </summary>
    private static readonly (string File, string Markup, string Decision)[] UserChosenGreenButtons =
    [
        ("Components/Notifications/NotificationBell.razor", @"TriggerVariant=""OmniButtonVariant.Success""",
            "R-170, confirmed by R-385 (2026-09-28): the notification bell is green, the tasks button blue"),
    ];

    /// <summary>
    /// Second blue buttons of a zone the user chose against the rule, found by their click handler:
    /// (file, handler, recette line).
    /// </summary>
    private static readonly (string File, string Handler, string Decision)[] UserChosenSecondBlueButtons =
    [
        ("Components/Vaults/VaultEdit.razor", "GenerateNewSecretValue",
            "R-436 (2026-09-28): Generate, the value field's helper, is blue beside Add"),
        ("Components/Servers/ServerDetailSections/MailStatusFix.razor", "OnInstallCertificate",
            "User decision (2026-09-28): Obtain and Install the mail certificate are both blue"),
        ("Components/Pipelines/PipelineSetupReadinessPanel.razor", "ProvisionAsync",
            "R2-26 (2026-10-21): in the summary's to-do block, the libraries and vaults line shows its create button blue, "
            + "like the repository line's, both aligned in one column (one main action per line)"),
    ];

    /// <summary>
    /// The orange (<c>Warning</c>) and light-blue (<c>Info</c>) buttons the contract keeps, each found by its
    /// exact markup: (file, markup, decision). Every other button takes the colour of its importance.
    /// </summary>
    private static readonly (string File, string Markup, string Decision)[] UserChosenInfoOrWarningButtons =
    [
        ("Components/PackageRegistry/PackageRegistryPackageDialog.razor", @"Variant=""OmniButtonVariant.Warning"" OnClick=""@(() => ToggleListedAsync(v))""",
            "R1-18: the registry version's Unlist / Relist is orange, chosen by the user"),
        ("Components/PackageRegistry/PackageRegistryPackageDialog.razor", @"Variant=""OmniButtonVariant.Warning"" OnClick=""@(() => ToggleDeprecatedAsync(v))""",
            "R1-18: the registry version's Deprecate is orange, chosen by the user"),
        ("Components/Shared/PipelineCatalogView.cs", "PipelineCatalogStatus.Running => OmniButtonVariant.Info",
            "R-165: each status chip of the pipelines catalogue has its colour (a selector, colour per state)"),
        ("Components/Shared/PipelineCatalogView.cs", "PipelineCatalogStatus.Outdated => OmniButtonVariant.Warning",
            "R-165: each status chip of the pipelines catalogue has its colour (a selector, colour per state)"),
    ];

    [Fact]
    public void NoButton_UsesInfoOrWarning_OutsideTheUserExceptions()
    {
        var violations = new List<string>();
        var allowed = UserChosenInfoOrWarningButtons.ToList();
        var files = FrontFiles("*.razor").Concat(FrontFiles("*.cs")).ToList();
        foreach (var file in files)
        {
            var rel = Rel(file);
            var lines = File.ReadAllLines(file);
            for (var n = 0; n < lines.Length; n++)
            {
                var line = lines[n];
                if (!Regex.IsMatch(line, @"OmniButtonVariant\.(Info|Warning)\b|omni-(split-)?button--(info|warning)")) continue;
                var exception = allowed.FindIndex(a => a.File == rel && line.Contains(a.Markup, StringComparison.Ordinal));
                if (exception >= 0) { allowed.RemoveAt(exception); continue; }
                violations.Add($"{rel}:{n + 1}: {line.Trim()}");
            }
        }

        var cssLines = File.ReadAllLines(Path.Combine(FrontDirectory(), "wwwroot", "css", "app.css"));
        for (var n = 0; n < cssLines.Length; n++)
        {
            if (Regex.IsMatch(cssLines[n], @"omni-(split-)?button--(info|warning)"))
                violations.Add($"wwwroot/css/app.css:{n + 1}: {cssLines[n].Trim()}");
        }

        Assert.True(files.Count >= 300, $"Expected the front's razor and C# files, found {files.Count}: the scan is broken.");
        Assert.True(allowed.Count == 0,
            "A documented Info/Warning exception no longer matches its markup; update or remove it:\n  "
            + string.Join("\n  ", allowed.Select(a => $"{a.File}: {a.Markup} ({a.Decision})")));
        Assert.True(violations.Count == 0,
            "STD-BTN (revised 2026-09-28): Info and Warning are not button colours. The main action of a zone is "
            + "Primary, the others Secondary, a minor one Ghost, a destructive one Danger:\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>Localized label keys of a button that dismisses its zone rather than acting.</summary>
    private static readonly Regex DismissLabelKey = new(@"^(Cancel|GoBack|Close|Previous|Back)$", RegexOptions.CultureInvariant);

    [Fact]
    public void DismissButtons_AreSecondary()
    {
        var violations = new List<string>();
        var checkedCount = 0;

        foreach (var (file, tag) in ButtonTags())
        {
            var keys = LabelKeys(tag);
            if (keys.Count != 1 || !DismissLabelKey.IsMatch(keys[0])) continue;
            var variant = Attribute(tag, "Variant");
            // A red Close closes a thing (a pull request), a destructive action ruled by
            // DestructiveButtons_AreDanger, not the dismissal of a zone.
            if (keys[0] == "Close" && variant == Danger) continue;
            // DialogFooter's own dismiss button reads its CancelVariant parameter, Secondary by default;
            // the callers that would override it are checked below.
            if (file == "Components/Shared/DialogFooter.razor" && variant == "@OmniCancelVariant") continue;
            checkedCount++;
            if (variant == "OmniButtonVariant.Secondary") continue;
            violations.Add($"{file}: dismiss button {keys[0]} must be OmniButtonVariant.Secondary, found {variant ?? "(none, OE's Primary)"}");
        }

        foreach (var file in FrontFiles("*.razor"))
        {
            // DialogFooter's dismiss button is Secondary unless a caller overrides it.
            foreach (Match footer in Regex.Matches(File.ReadAllText(file), @"<DialogFooter\b[^>]*\bCancelVariant\s*=\s*""(?<v>[^""]*)"""))
            {
                if (footer.Groups["v"].Value != "OmniButtonVariant.Secondary")
                    violations.Add($"{Rel(file)}: DialogFooter dismiss button set to {footer.Groups["v"].Value}");
            }
        }

        Assert.True(checkedCount >= 20,
            $"Expected the app's Annuler/Revenir/Fermer/Précédent buttons to be found, found only {checkedCount}: the scan is broken.");
        Assert.True(violations.Count == 0,
            "STD-BTN (revised 2026-09-28): a dismiss button (Annuler, Revenir, Fermer, Précédent) is Secondary:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void NoButton_UsesTheSuccessColour()
    {
        var violations = new List<string>();
        var allowed = UserChosenGreenButtons.ToList();
        var files = FrontFiles("*.razor").Concat(FrontFiles("*.cs")).ToList();
        foreach (var file in files)
        {
            var rel = Rel(file);
            var lines = File.ReadAllLines(file);
            for (var n = 0; n < lines.Length; n++)
            {
                var line = lines[n];
                if (!line.Contains("OmniButtonVariant.Success", StringComparison.Ordinal)
                    && !Regex.IsMatch(line, @"omni-(split-)?button--success")) continue;
                var exception = allowed.FindIndex(a => a.File == rel && line.Contains(a.Markup, StringComparison.Ordinal));
                if (exception >= 0) { allowed.RemoveAt(exception); continue; }
                violations.Add($"{rel}:{n + 1}: {line.Trim()}");
            }
        }

        var css = Path.Combine(FrontDirectory(), "wwwroot", "css", "app.css");
        var cssLines = File.ReadAllLines(css);
        for (var n = 0; n < cssLines.Length; n++)
        {
            if (Regex.IsMatch(cssLines[n], @"omni-(split-)?button--success"))
                violations.Add($"wwwroot/css/app.css:{n + 1}: {cssLines[n].Trim()}");
        }

        Assert.True(files.Count >= 300, $"Expected the front's razor and C# files, found {files.Count}: the scan is broken.");
        Assert.True(allowed.Count == 0,
            "A documented green exception no longer matches its markup; update or remove it:\n  "
            + string.Join("\n  ", allowed.Select(a => $"{a.File}: {a.Markup} ({a.Decision})")));
        Assert.True(violations.Count == 0,
            "STD-BTN (revised 2026-09-28): never the success colour on a button. The main action of a zone is "
            + "Primary, the others Secondary, a minor one Ghost, a destructive one Danger:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void AZone_HasAtMostOneBlueButton()
    {
        var violations = new List<string>();
        var zonesWithBlue = 0;

        foreach (var file in FrontFiles("*.razor"))
        {
            var rel = Rel(file);
            var blue = ButtonZoneScanner.Buttons(File.ReadAllText(file))
                .Where(ButtonZoneScanner.IsMain)
                .Where(b => !UserChosenSecondBlueButtons.Any(e => e.File == rel
                    && Regex.IsMatch(b.Markup, $@"OnClick=""[^""]*\b{Regex.Escape(e.Handler)}\b")))
                .ToList();
            foreach (var zone in blue.GroupBy(b => b.Zone))
            {
                zonesWithBlue++;
                var buttons = zone.ToList();
                for (var a = 0; a < buttons.Count; a++)
                    for (var b = a + 1; b < buttons.Count; b++)
                    {
                        if (ButtonZoneScanner.Exclusive(buttons[a], buttons[b])) continue;
                        violations.Add($"{rel}: lines {buttons[a].Line} and {buttons[b].Line} are both blue in one zone");
                    }
            }
        }

        Assert.True(zonesWithBlue >= 150, $"Expected the app's zones holding a blue button, found {zonesWithBlue}: the scan is broken.");
        Assert.True(violations.Count == 0,
            "STD-BTN (revised 2026-09-28): blue is the main action of its zone, one per zone; the other actions "
            + "are Secondary (or Ghost when minor):\n  " + string.Join("\n  ", violations.Distinct()));
    }

    /// <summary>
    /// The zones and grid rows the user chose to keep with several visible actions (decision of 2026-10-17),
    /// each named by its file and the exact actions it shows (a button's label key, read from its Text, Label,
    /// title, aria-label or content, else its click handler). A zone of the same file showing other actions,
    /// or one action more, is still refused.
    /// </summary>
    private static readonly (string File, string[] Actions, string Decision)[] UserChosenCrowdedZones =
    [
        ("Components/VariableLibraries/VariableLibraryEdit.razor", ["CopyKey", "CopyValue", "Edit"],
            "R2-066 and R-435, narrowed by R1-4 on 2026-10-21: a library entry row shows its cell copy buttons and Edit; History and Delete are in the row menu"),
        ("Components/Vaults/VaultEdit.razor", ["CopyKey", "CopyValue", "Edit"],
            "R-293 and R-436, narrowed by R1-4 on 2026-10-21: a secret row shows its cell copy buttons and Edit; the other actions are in the row menu"),
        ("Components/Vaults/VaultEdit.razor", ["Generate", "Add"],
            "R-436, kept on 2026-10-17: the add row shows Generate blue beside Add"),
        ("Components/Servers/ServerDetailSections/MailStatusFix.razor", ["MailObtainCertificate", "MailInstallCertificate"],
            "User decision of 2026-09-28, kept on 2026-10-17: Obtain and Install the mail certificate are both shown, both blue"),
        ("Components/Pipelines/PipelineRunApprovalPanel.razor", ["Approve", "Reject", "ApprovalAddComment"],
            "User decision of 2026-10-17: Approve and Reject both stay visible, with Add a comment beside them"),
    ];

    [Fact]
    public void AZone_ShowsOneMainAction_AndAGridRowAtMostTwo()
    {
        var violations = new List<string>();
        var used = new HashSet<int>();
        var scanned = 0;

        foreach (var file in FrontFiles("*.razor"))
        {
            var buttons = ButtonZoneScanner.Buttons(File.ReadAllText(file));
            scanned += buttons.Count;
            violations.AddRange(UnexcusedCrowdedActions(Rel(file), buttons, UserChosenCrowdedZones, used)
                .Select(lines => $"{Rel(file)}: lines {lines}"));
        }

        var stale = UserChosenCrowdedZones.Where((_, i) => !used.Contains(i)).ToList();
        Assert.True(scanned >= 500, $"Expected the app's buttons, found {scanned}: the scan is broken.");
        Assert.True(stale.Count == 0,
            "A recorded crowded-zone decision no longer matches its zone; update or remove it:\n  "
            + string.Join("\n  ", stale.Select(e => $"{e.File}: {string.Join(", ", e.Actions)} ({e.Decision})")));
        Assert.True(violations.Count == 0,
            "STD-BTN (detection 11, owner decision of 2026-10-12): one visible main action per zone (plus its dismiss "
            + "action and menu trigger), at most two actions on a grid row; the other actions go into a menu "
            + "(OmniSplitButton or OmniOverflowMenu):\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void CrowdedScan_FlagsSideBySideActionsAndCrowdedRows_AndSparesDismissMenuAndBranches()
    {
        const string sample = """
            <div class="header">
                <OmniButton Variant="OmniButtonVariant.Secondary" OnClick="@Export"><OmniIcon Name="download" />@L["Export"]</OmniButton>
                <OmniButton OnClick="@Create"><OmniIcon Name="add" />@L["Create"]</OmniButton>
            </div>
            <div class="footer">
                <OmniButton Variant="OmniButtonVariant.Secondary" OnClick="@Close"><OmniIcon Name="close" />@L["Cancel"]</OmniButton>
                <OmniButton OnClick="@Save"><OmniIcon Name="save" />@L["Save"]</OmniButton>
                <OmniButton Variant="OmniButtonVariant.Secondary" Label="@L["MoreActions"]"><OmniIcon Name="more_vert" /></OmniButton>
            </div>
            <div class="toolbar">
                @if (_editing)
                {
                    <OmniButton OnClick="@Save"><OmniIcon Name="save" />@L["Save"]</OmniButton>
                }
                else
                {
                    <OmniButton Variant="OmniButtonVariant.Secondary" OnClick="@Edit"><OmniIcon Name="edit" />@L["Edit"]</OmniButton>
                }
                <OmniButton Variant="@(_grid ? OmniButtonVariant.Primary : OmniButtonVariant.Secondary)" OnClick="@Toggle"><OmniIcon Name="view_list" />@L["View"]</OmniButton>
                <OmniButton Variant="OmniButtonVariant.Ghost" title="@L["FullScreen"]" OnClick="@Full"><OmniIcon Name="fullscreen" /></OmniButton>
            </div>
            <OmniDataGrid TItem="Item">
                <OmniDataGridColumn TItem="Item">
                    <Template Context="item">
                        <OmniButton Variant="OmniButtonVariant.Secondary" Label="@L["Details"]"><OmniIcon Name="visibility" /></OmniButton>
                        <OmniButton Variant="OmniButtonVariant.Secondary" Label="@L["Edit"]"><OmniIcon Name="edit" /></OmniButton>
                    </Template>
                    <EditTemplate Context="item">
                        <OmniButton Variant="OmniButtonVariant.Secondary" Label="@L["Save"]"><OmniIcon Name="check" /></OmniButton>
                        <OmniButton Variant="OmniButtonVariant.Secondary" Label="@L["Cancel"]"><OmniIcon Name="close" /></OmniButton>
                    </EditTemplate>
                </OmniDataGridColumn>
                <OmniDataGridColumn TItem="Item">
                    <Template Context="item">
                        <OmniButton Variant="OmniButtonVariant.Danger" Label="@L["Delete"]"><OmniIcon Name="delete" /></OmniButton>
                        <OmniButton Variant="OmniButtonVariant.Secondary" Label="@L["MoreActions"]"><OmniIcon Name="more_horiz" /></OmniButton>
                    </Template>
                </OmniDataGridColumn>
            </OmniDataGrid>
            <OmniDataGrid TItem="Item">
                <OmniDataGridColumn TItem="Item">
                    <Template Context="item">
                        <OmniButton Variant="OmniButtonVariant.Secondary" Label="@L["Edit"]"><OmniIcon Name="edit" /></OmniButton>
                        <OmniButton Variant="OmniButtonVariant.Danger" Label="@L["Delete"]"><OmniIcon Name="delete" /></OmniButton>
                    </Template>
                </OmniDataGridColumn>
            </OmniDataGrid>
            """;

        var crowded = CrowdedActions(ButtonZoneScanner.Buttons(sample)).ToList();

        Assert.Equal(["2, 3", "25, 26, 35 (grid row)"], crowded);
    }

    [Fact]
    public void CrowdedDecision_CoversOnlyItsExactZone_InItsOwnFile()
    {
        const string sample = """
            <div class="decisions">
                <OmniButton OnClick="@Approve" Label="@L["Approve"]"><OmniIcon Name="check" /> <span>@L["Approve"]</span></OmniButton>
                <OmniButton Variant="OmniButtonVariant.Danger" OnClick="@Reject" Label="@L["Reject"]"><OmniIcon Name="block" /> <span>@L["Reject"]</span></OmniButton>
            </div>
            <div class="header">
                <OmniButton Variant="OmniButtonVariant.Secondary" OnClick="@Export"><OmniIcon Name="download" />@L["Export"]</OmniButton>
                <OmniButton OnClick="@Create"><OmniIcon Name="add" />@L["Create"]</OmniButton>
            </div>
            <div class="grown">
                <OmniButton OnClick="@Approve" Label="@L["Approve"]"><OmniIcon Name="check" /> <span>@L["Approve"]</span></OmniButton>
                <OmniButton Variant="OmniButtonVariant.Danger" OnClick="@Reject" Label="@L["Reject"]"><OmniIcon Name="block" /> <span>@L["Reject"]</span></OmniButton>
                <OmniButton Variant="OmniButtonVariant.Secondary" OnClick="@Defer"><OmniIcon Name="schedule" />@L["Defer"]</OmniButton>
            </div>
            """;
        (string File, string[] Actions, string Decision)[] decisions = [("A.razor", ["Reject", "Approve"], "test decision")];
        var buttons = ButtonZoneScanner.Buttons(sample);

        var used = new HashSet<int>();
        var inItsFile = UnexcusedCrowdedActions("A.razor", buttons, decisions, used).ToList();
        var elsewhere = UnexcusedCrowdedActions("B.razor", buttons, decisions, []).ToList();

        // The decided zone passes; the other zone of the same file, and the zone with one action more, do not.
        Assert.Equal(["6, 7", "10, 11, 12"], inItsFile);
        Assert.Equal([0], used);
        Assert.Equal(["2, 3", "6, 7", "10, 11, 12"], elsewhere);
    }

    /// <summary>
    /// The lines of every zone that shows more than one action besides its dismiss action and menu trigger,
    /// and of every grid row that shows more than two actions besides its menu trigger. A toggle (its variant
    /// computed per state) is not an action, and neither is, off a grid row, an icon-only button named by
    /// its <c>Label</c> or title (window chrome or toolbar icon, allowed by the text-and-icon rule).
    /// </summary>
    private static IEnumerable<string> CrowdedActions(List<ButtonZoneScanner.ZoneButton> buttons) =>
        CrowdedGroups(buttons).Select(Describe);

    private static string Describe((List<ButtonZoneScanner.ZoneButton> Buttons, bool Row) group) =>
        string.Join(", ", group.Buttons.Select(b => b.Line)) + (group.Row ? " (grid row)" : string.Empty);

    private static IEnumerable<(List<ButtonZoneScanner.ZoneButton> Buttons, bool Row)> CrowdedGroups(List<ButtonZoneScanner.ZoneButton> buttons)
    {
        var actions = buttons.Where(b => !IsMenuTrigger(b) && !IsToggle(b)).ToList();

        foreach (var zone in actions.Where(b => b.Row == 0 && !IsDismiss(b) && !IsIconOnly(b)).GroupBy(b => b.Zone))
        {
            var together = ButtonZoneScanner.RenderedTogether(zone.ToList());
            if (together.Count > 1)
                yield return (together, false);
        }

        foreach (var row in actions.Where(b => b.Row != 0).GroupBy(b => b.Row))
        {
            var together = ButtonZoneScanner.RenderedTogether(row.ToList());
            if (together.Count > 2)
                yield return (together, true);
        }
    }

    /// <summary>
    /// The crowded zones and rows of <paramref name="file"/> that no recorded decision names: a decision
    /// covers a zone only when the file matches and the zone shows exactly its actions. The index of each
    /// decision that covered a zone is added to <paramref name="used"/>.
    /// </summary>
    private static IEnumerable<string> UnexcusedCrowdedActions(string file, List<ButtonZoneScanner.ZoneButton> buttons,
        (string File, string[] Actions, string Decision)[] decisions, HashSet<int> used)
    {
        foreach (var group in CrowdedGroups(buttons))
        {
            var shown = group.Buttons.Select(ActionName).Order(StringComparer.Ordinal).ToList();
            var decision = Array.FindIndex(decisions, d => d.File == file
                && d.Actions.Order(StringComparer.Ordinal).SequenceEqual(shown, StringComparer.Ordinal));
            if (decision >= 0)
            {
                used.Add(decision);
                continue;
            }

            yield return Describe(group);
        }
    }

    /// <summary>
    /// The name of a button's action: the label key of its Text, Label, title or aria-label, else the first
    /// label key of its content, else its click handler.
    /// </summary>
    private static string ActionName(ButtonZoneScanner.ZoneButton button)
    {
        var open = OpeningTag(button.Markup);
        foreach (var name in new[] { "Text", "Label", "title", "aria-label" })
        {
            var key = Regex.Match(open, $@"\b{Regex.Escape(name)}\s*=\s*""@?L\[""(?<key>\w+)""\]""");
            if (key.Success) return key.Groups["key"].Value;
        }

        var content = Regex.Match(button.Markup[open.Length..], @"\bL\[""(?<key>\w+)""\]");
        if (content.Success) return content.Groups["key"].Value;
        var handler = Regex.Match(open, @"OnClick=""[^""]*?(?<name>\w+)\s*\(|OnClick=""@?(?<name>\w+)""");
        return handler.Success ? handler.Groups["name"].Value : $"line {button.Line}";
    }

    /// <summary>Localized label keys and icons of a button that only opens a menu of the zone's other actions.</summary>
    private static readonly Regex MenuTriggerLabelKey = new(@"^(MoreActions|OtherActions)$", RegexOptions.CultureInvariant);

    private static readonly Regex MenuTriggerIcon = new(@"<OmniIcon\b[^>]*\bName\s*=\s*""(more_vert|more_horiz)""", RegexOptions.CultureInvariant);

    // Role keyed by the label key the button displays (its content, Label, title or aria-label).
    private static bool IsDismiss(ButtonZoneScanner.ZoneButton button) =>
        LabelKeys(button.Markup) is [var key] && DismissLabelKey.IsMatch(key);

    private static bool IsMenuTrigger(ButtonZoneScanner.ZoneButton button) =>
        (LabelKeys(button.Markup) is [var key] && MenuTriggerLabelKey.IsMatch(key)) || MenuTriggerIcon.IsMatch(button.Markup);

    // A toggle or selector: its variant computed per state, or its pressed state announced (aria-pressed).
    private static bool IsToggle(ButtonZoneScanner.ZoneButton button)
    {
        var open = OpeningTag(button.Markup);
        return (Attribute(open, "Variant") ?? string.Empty).TrimStart().StartsWith('@')
               || Attribute(open, "aria-pressed") is not null;
    }

    private static bool IsIconOnly(ButtonZoneScanner.ZoneButton button)
    {
        var open = OpeningTag(button.Markup);
        if (open.StartsWith("<OmniSplitButton", StringComparison.Ordinal)) return false;
        var content = button.Markup[open.Length..];
        content = Regex.Replace(content, @"</OmniButton>\s*$", string.Empty);
        var text = Regex.Replace(content, @"<(?:""[^""]*""|[^>""])*>", string.Empty);
        var named = Attribute(open, "Label") ?? Attribute(open, "title") ?? Attribute(open, "aria-label");
        return string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(named);
    }

    private static string OpeningTag(string markup) =>
        Regex.Match(markup, @"^<\w+\b(?:""[^""]*""|[^>""])*>").Value;

    /// <summary>Localized label keys that name a destructive or interrupting action.</summary>
    private static readonly Regex DestructiveLabelKey = new(
        @"^(Delete\w*|Remove|RemoveFrom(?!Favorites)\w*|CancelRun|CancelTask|\w*Revoke\w*|Stop|StopAll|StopApache|Uninstall|Unregister|Unlink|Reject|Ban|Kick|Rollback|Purge\w*|Retire\w*|Restore|RestoreVersion|PortRelease|Detach|Deactivate|DisableModule|Disable2FA|FirewallDeny|FirewallDisable|Unsubscribe|DockerPrune|Regenerate\w*)$",
        RegexOptions.CultureInvariant);

    /// <summary>Handler names that start a destructive or interrupting action.</summary>
    private static readonly Regex DestructiveHandler = new(
        @"^(?:Confirm)?(Delete|Remove(?!.*Favorite)|Cancel(Run|Task)|Revoke|Uninstall|Unregister|Purge|Rollback)\w*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex ConfirmCall = new(
        @"\.Confirm\((?:(?!\.Confirm\().)*?\}\s*\)(?:\s*==\s*true)?\s*;", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    [Fact]
    public void DestructiveButtons_AreDanger()
    {
        var violations = new List<string>();
        var checkedCount = 0;

        foreach (var (file, tag) in ButtonTags())
        {
            if (!IsDestructive(tag, out var reason)) continue;
            checkedCount++;
            var variant = Attribute(tag, "Variant");
            // Every destructive action uses Danger, including multiple destructive actions in one
            // row.
            if (string.Equals(variant, Danger, StringComparison.Ordinal)) continue;
            violations.Add($"{file}: {reason} must be {Danger}, found {variant ?? "(none)"}");
        }

        Assert.True(checkedCount >= 50,
            $"Expected the app's delete/stop/revoke buttons to be found, found only {checkedCount}: the scan is broken.");
        Assert.True(violations.Count == 0,
            "STD-BTN: a destructive or interrupting button (delete, remove, cancel a run, stop, revoke...) "
            + "is red, on the button that starts it and on the one that confirms it:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void DestructiveConfirmations_AreRedAndNameTheirVerb()
    {
        var violations = new List<string>();
        var destructiveFound = CheckDestructiveConfirmCalls(violations) + CheckActionSectionConfirmations(violations);

        Assert.True(destructiveFound >= 40,
            $"Expected the app's destructive confirmations to be found, found only {destructiveFound}: the scan is broken.");
        Assert.True(violations.Count == 0,
            "STD-BTN: a destructive action stays red on the button that confirms it, which names the verb, never \"Valider\":\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>Scans code-opened confirmation dialogs for destructive-action violations.</summary>
    private static int CheckDestructiveConfirmCalls(List<string> violations)
    {
        var dangerHandlers = DangerButtonHandlers();
        var found = 0;
        foreach (var file in FrontFiles("*.cs"))
        {
            // The server sections' shared confirmation takes its verb and its colour from each caller:
            // the callers are what the button scan below checks.
            if (Path.GetFileName(file) == "ServerActionSectionBase.cs") continue;
            var source = File.ReadAllText(file);
            foreach (Match call in ConfirmCall.Matches(source))
            {
                if (CheckConfirmCall(file, source, call, dangerHandlers, violations)) found++;
            }
        }
        return found;
    }

    /// <summary>One confirmation call: false when it is not destructive, its violations added otherwise.</summary>
    private static bool CheckConfirmCall(
        string file, string source, Match call, HashSet<string> dangerHandlers, List<string> violations)
    {
        var method = EnclosingMethod(source, call.Index);
        var okKey = Regex.Match(call.Value, @"OkButtonText\s*=\s*\w+\[""(?<key>\w+)""\]").Groups["key"].Value;
        var conditionalLabel = Regex.IsMatch(call.Value, @"OkButtonText\s*=\s*\w+\s*\?");
        var flagged = Regex.IsMatch(call.Value, @"Destructive\s*=\s*(?!false\b)\w+");
        var triggeredByRed = method.Length > 0 && dangerHandlers.Contains(method);
        var destructiveVerb = okKey.Length > 0 && DestructiveLabelKey.IsMatch(okKey);
        if (!flagged && !triggeredByRed && !destructiveVerb) return false;

        var where = $"{Rel(file)} ({method})";
        if (!flagged)
            violations.Add($"{where}: destructive confirmation without Destructive = true (its confirm button must be red)");
        if (!conditionalLabel && (okKey.Length == 0 || okKey == "Validate"))
            violations.Add($"{where}: destructive confirmation labelled {(okKey.Length == 0 ? "by default" : okKey)}, it must name its verb (Supprimer, Arrêter...)");
        return true;
    }

    /// <summary>The red buttons of the server sections, which confirm through ServerActionSectionBase.</summary>
    private static int CheckActionSectionConfirmations(List<string> violations)
    {
        var actionSections = FrontFiles("*.razor")
            .Where(f => File.ReadAllText(f).Contains("@inherits ServerActionSectionBase", StringComparison.Ordinal))
            .Select(Rel)
            .ToHashSet(StringComparer.Ordinal);
        var found = 0;
        foreach (var (file, tag) in ButtonTags())
        {
            // The confirmation of the server sections (ServerActionSectionBase): a red button asks it
            // through ShowDestructiveConfirm, which passes the verb and turns the confirm button red.
            if (!actionSections.Contains(file) || Attribute(tag, "Variant") != Danger
                || !Regex.IsMatch(tag, @"Show(Destructive)?Confirm\(")) continue;
            found++;
            if (!tag.Contains("ShowDestructiveConfirm(", StringComparison.Ordinal))
                violations.Add($"{file}: a red button opens ShowConfirm, a green \"Valider\" confirmation (use ShowDestructiveConfirm with its verb)");
        }
        return found;
    }

    [Fact]
    public void CancellationConfirmations_AreNotDismissedByAnotherCancel()
    {
        var violations = new List<string>();
        var found = 0;

        foreach (var file in FrontFiles("*.cs"))
        {
            var source = File.ReadAllText(file);
            foreach (Match call in ConfirmCall.Matches(source))
            {
                if (!Regex.IsMatch(call.Value, @"\[""Cancel(Run|Task)\w*""\]")) continue;
                found++;
                var dismiss = Regex.Match(call.Value, @"CancelButtonText\s*=\s*\w+\[""(?<key>\w+)""\]");
                var confirm = Regex.Match(call.Value, @"OkButtonText\s*=\s*\w+\[""(?<key>\w+)""\]");
                if (!dismiss.Success || dismiss.Groups["key"].Value == "Cancel")
                    violations.Add($"{Rel(file)}: the dismiss button of a cancellation says \"Annuler\" (use GoBack, \"Revenir\")");
                if (confirm.Success && confirm.Groups["key"].Value.StartsWith("Cancel", StringComparison.Ordinal))
                    violations.Add($"{Rel(file)}: the confirm button of a cancellation is labelled {confirm.Groups["key"].Value}, another \"Annuler\" (use a short verb such as Stop)");
            }
        }

        Assert.True(found >= 3,
            $"Expected the run and task cancellation confirmations to be found, found {found}: the scan is broken.");
        Assert.True(violations.Count == 0,
            "STD-BTN: a dialog confirming a cancellation is dismissed by \"Revenir\", never by a second \"Annuler\":\n  "
            + string.Join("\n  ", violations));
    }

    private static bool IsDestructive(string tag, out string reason)
    {
        reason = string.Empty;
        var labelKeys = LabelKeys(tag);
        var destructiveKey = labelKeys.Count == 1 && DestructiveLabelKey.IsMatch(labelKeys[0]) ? labelKeys[0] : null;
        var handler = Regex.Match(tag, @"OnClick=""[^""]*?(?<name>\w+)(?:\.InvokeAsync)?\s*\(").Groups["name"].Value;
        if (!Regex.IsMatch(tag, @"OnClick=""\s*@?\(?\s*(?:\(\)|_)\s*=>")) handler = Regex.Match(tag, @"OnClick=""@?(?<name>\w+)""").Groups["name"].Value;
        var destructiveHandler = handler.Length > 0 && DestructiveHandler.IsMatch(handler) ? handler : null;
        if (destructiveKey is null && destructiveHandler is null) return false;
        reason = destructiveKey is not null ? $"label {destructiveKey}" : $"handler {destructiveHandler}";
        return true;
    }

    /// <summary>
    /// The distinct localized keys a button's markup names (its label, title, aria-label), the
    /// permission notices aside. A label that switches between two actions (Enable/Disable, Follow/Stop
    /// following) gives two keys: a toggle whose colour is chosen per state, which a static rule skips.
    /// </summary>
    private static List<string> LabelKeys(string tag) =>
        Regex.Matches(tag, @"\bL\[""(?<key>\w+)""\]").Select(m => m.Groups["key"].Value).Distinct()
            .Where(k => k != "InsufficientPermissions" && k != "AgentOfflineActionBlocked").ToList();

    private static string FirstLine(string tag) => tag.Split('\n')[0].Trim();

    /// <summary>
    /// The handler methods that red buttons call (<c>OnClick="@(() => M(...))"</c> or
    /// <c>OnClick="@M"</c>): a confirmation asked from one of them confirms a destructive action.
    /// </summary>
    private static HashSet<string> DangerButtonHandlers()
    {
        var handlers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, tag) in ButtonTags())
        {
            if (Attribute(tag, "Variant") != Danger) continue;
            var click = Regex.Match(tag, @"OnClick=""(?<v>[^""]*)""").Groups["v"].Value;
            var call = Regex.Match(click, @"=>\s*(?<name>\w+)\s*\(");
            var direct = Regex.Match(click, @"^@(?<name>\w+)$");
            if (call.Success) handlers.Add(call.Groups["name"].Value);
            else if (direct.Success) handlers.Add(direct.Groups["name"].Value);
        }
        return handlers;
    }

    /// <summary>The name of the method whose body holds <paramref name="index"/>.</summary>
    private static string EnclosingMethod(string source, int index) =>
        Regex.Matches(source[..index], @"\b(?:Task|void)(?:<[^>]+>)?\??\s+(?<name>\w+)\s*\(")
            .LastOrDefault()?.Groups["name"].Value ?? string.Empty;

    private static IEnumerable<(string File, string Tag)> ButtonTags()
    {
        foreach (var file in FrontFiles("*.razor"))
        {
            var source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source, @"<OmniButton\b(?:(?!</OmniButton>).)*?</OmniButton>", RegexOptions.Singleline))
                yield return (Rel(file), match.Value);
        }
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $@"\b{Regex.Escape(name)}\s*=\s*""(?<v>[^""]*)""");
        return match.Success ? match.Groups["v"].Value : null;
    }

    private static IEnumerable<string> FrontFiles(string pattern) =>
        RepositoryScan.Enumerate(FrontDirectory(), pattern)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string Rel(string file) => Path.GetRelativePath(FrontDirectory(), file).Replace('\\', '/');

    private static string FrontDirectory() => Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
}
