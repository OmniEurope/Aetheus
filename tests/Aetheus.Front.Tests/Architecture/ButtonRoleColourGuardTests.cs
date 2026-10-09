// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// STD-BTN (revised 2026-09-28, docs/contracts/ui-patterns.md): a button's colour follows its importance.
/// Blue (Primary) is the main action of its zone, one per zone; grey (Secondary) the other actions; Ghost
/// a minor action; red (Danger) a destructive one; never green (Success) on a button. It replaces the
/// 2026-09-27 mapping (blue opens, green performs), and with it the rule that two actions of one grid row
/// never share a colour. An action repeated on each grid row is grey unless the user names it the row's
/// main action, which is then blue (R-533, 2026-09-30: the colour of a row action is the user's choice,
/// recorded beside its markup, and no list of exceptions gates it any more). This guard enforces the
/// parts of the rule a source scan can decide on its own:
/// <list type="bullet">
/// <item>no button is green, the user's documented exceptions aside;</item>
/// <item>no button is Info or Warning, the contract's exceptions aside (R-293, the per-state selectors);</item>
/// <item>a dismiss button (Cancel, GoBack, Close, Previous) is Secondary;</item>
/// <item>a zone (the element that directly holds its buttons: a header's actions, a dialog footer, a
/// toolbar, one grid row) has at most one blue button rendered at a time (mutually exclusive
/// <c>@if</c>/<c>else</c> branches never render together);</item>
/// <item>a button whose label or handler names a destructive role is red, the button that confirms it
/// in a dialog included;</item>
/// <item>a destructive confirmation (triggered by a red button, or confirmed by a destructive verb) is
/// red and names its verb, never "Valider";</item>
/// <item>a confirmation of a cancellation is not dismissed by a second "Annuler" but by "Revenir".</item>
/// </list>
/// Roles are read from the label key first (a Cancel wired to a lambda has no "Cancel" in its handler),
/// the handler name only adding what a label cannot say. Which action of a zone is its main one stays an
/// audit judgment; the guard only refuses a second blue button beside it.
/// </summary>
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
        ("Components/VariableLibraries/VariableLibraryEdit.razor", @"Variant=""OmniButtonVariant.Success"" OnClick=""@(() => EditEntry(entry))""",
            "R2-066 (2026-10-02): a library entry's Edit is green, its History blue"),
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
    ];

    /// <summary>
    /// The orange (<c>Warning</c>) and light-blue (<c>Info</c>) buttons the contract keeps, each found by its
    /// exact markup: (file, markup, decision). Every other button takes the colour of its importance.
    /// </summary>
    private static readonly (string File, string Markup, string Decision)[] UserChosenInfoOrWarningButtons =
    [
        ("Components/Vaults/VaultEdit.razor", @"Variant=""OmniButtonVariant.Warning"" OnClick=""@(() => RotateSecret(secret))""",
            "R-293: a vault secret's Rotate stays orange, chosen before the revision"),
        ("Components/Shared/PipelineCatalogView.cs", "PipelineCatalogStatus.Running => OmniButtonVariant.Info",
            "R-165: each status chip of the pipelines catalogue has its colour (a selector, colour per state)"),
        ("Components/Shared/PipelineCatalogView.cs", "PipelineCatalogStatus.Outdated => OmniButtonVariant.Warning",
            "R-165: each status chip of the pipelines catalogue has its colour (a selector, colour per state)"),
        ("Components/Analysis/AnalysisFindingDetail.razor.cs", "AnalysisFindingStatus.Accepted => OmniButtonVariant.Warning",
            "ui-patterns, toggle and selector buttons: the decision selector keeps a colour per state, never green"),
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
            // Every destructive action is red, a second one in the same grid row included: the former
            // fourth-role Warning existed only for the retired "a row never shares a colour" rule.
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

    /// <summary>
    /// The confirmation dialogs opened from code: the destructive ones found, their violations added.
    /// Recette R2-107: the two scans of the test above are their own methods, under the grade-A
    /// cyclomatic threshold.
    /// </summary>
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
