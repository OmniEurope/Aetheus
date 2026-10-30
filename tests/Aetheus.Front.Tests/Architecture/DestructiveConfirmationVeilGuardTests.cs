// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// R-99: a deletion, or any action started from a destructive confirmation, shows OE's veil while it runs. A page or
/// section runs it through the shared <c>BusyVeil</c> (<c>Veil.RunAsync(</c>, the page body takes <c>omni-busy</c>);
/// a dialog that runs the action itself veils its own content (<c>omni-busy</c> in its markup, bound to its busy flag).
/// The guard counts, per source, the destructive confirmations it builds (<c>Destructive = true</c>,
/// <c>ConfirmDeleteAsync(</c>, <c>ConfirmOptions(destructive...)</c>) and the <c>Veil.RunAsync(</c> calls it makes: it needs at
/// least one call per confirmation. A confirmation that is deliberately not veiled is listed in <see cref="Exempt"/> with
/// its reason, and an exemption that is no longer needed fails. It cannot prove that each action sits inside the call that
/// follows its confirmation (the behaviour tests of <c>R99BusyVeilTests</c> do, for the three patterns).
/// </summary>
public sealed class DestructiveConfirmationVeilGuardTests
{
    private static readonly Regex LiteralDestructive = new(@"Destructive\s*=\s*true", RegexOptions.Compiled);
    private static readonly Regex DeleteHelperCall = new(@"\bConfirmDeleteAsync\(", RegexOptions.Compiled);
    private static readonly Regex OptionsFactoryCall = new(@"\bConfirmOptions\(\s*destructive\b", RegexOptions.Compiled);
    private static readonly Regex DelegatedConfirmCall = new(@"\bShowDestructiveConfirm\(", RegexOptions.Compiled);
    private static readonly Regex VeilRun = new(@"\b[vV]eil\.RunAsync\(", RegexOptions.Compiled);
    /// <summary>The declaration of one of the helpers above (a return type, then the name), not a call to it.</summary>
    private static readonly Regex Definition = new(@"\bTask(<[^>]*>)?\s+(ConfirmDeleteAsync|ShowDestructiveConfirm)\(", RegexOptions.Compiled);
    private static readonly Regex DialogBusyFlag = new(@"\b_(deleting|removing|purging|purgingIds|restoring)\b", RegexOptions.Compiled);

    /// <summary>Confirmations that are deliberately not under the shared veil, with the number of them and why.</summary>
    private static readonly Dictionary<string, (int Count, string Reason)> Exempt = new(StringComparer.Ordinal)
    {
        ["Components/MailTemplates/MailTemplates.razor.cs"] =
            (1, "Discarding unsaved edits when another template is picked: local state only, no call to the server."),
        ["Components/Pipelines/VisualPipelineEditor.razor.cs"] =
            (1, "Removing a stage from the draft in the editor: local state only, the pipeline is saved later by the page."),
        ["Components/Pipelines/PipelineEdit.razor.cs"] =
            (1, "R-98: the pipeline page veils itself with its own _deleting flag (omni-busy on pipeline-detail-page in the markup)."),
    };

    [Fact]
    public void EveryDestructiveConfirmation_RunsItsActionUnderTheVeil_OrIsExemptedWithItsReason()
    {
        var sources = Sources().ToList();
        Assert.True(sources.Count(s => s.Confirmations > 0) > 50,
            $"The scan found only {sources.Count(s => s.Confirmations > 0)} sources with a destructive confirmation: it is broken.");

        var violations = new List<string>();
        foreach (var source in sources.Where(s => s.Confirmations > 0))
        {
            var deficit = Math.Max(0, source.Confirmations - source.Runs);
            var exempt = Exempt.TryGetValue(source.Relative, out var entry) ? entry.Count : 0;
            if (deficit > exempt)
                violations.Add($"{source.Relative}: {source.Confirmations} destructive confirmation(s) but {source.Runs} Veil.RunAsync call(s)");
        }

        Assert.True(violations.Count == 0,
            "R-99: a destructive confirmation runs its action under OE's veil (Veil.RunAsync from a page or a section, "
            + "omni-busy on the zone of a dialog that runs the action itself), or is exempted with its reason:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void EveryExemption_IsStillNeeded()
    {
        var bySource = Sources().ToDictionary(s => s.Relative, StringComparer.Ordinal);
        var stale = new List<string>();
        foreach (var (path, (count, reason)) in Exempt)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{path}: an exemption names its reason.");
            if (!bySource.TryGetValue(path, out var source)) { stale.Add($"{path}: no such source"); continue; }
            var deficit = Math.Max(0, source.Confirmations - source.Runs);
            if (deficit != count) stale.Add($"{path}: exempts {count} but the source lacks {deficit} Veil.RunAsync call(s)");
        }

        Assert.True(stale.Count == 0, "Stale R-99 exemptions:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void EveryDialogThatRunsADestructiveAction_VeilsItsOwnContent()
    {
        var dialogs = Sources()
            .Where(s => s.Relative.EndsWith("Dialog.razor.cs", StringComparison.Ordinal)
                        && (s.Confirmations > 0 || DialogBusyFlag.IsMatch(s.Code)))
            .ToList();
        Assert.True(dialogs.Count >= 4, "The scan found fewer than the 4 known dialogs that run a destructive action: it is broken.");

        var violations = dialogs
            .Where(dialog => !PairedMarkup(dialog.Path).Contains("omni-busy", StringComparison.Ordinal))
            .Select(dialog => dialog.Relative)
            .ToList();

        Assert.True(violations.Count == 0,
            "R-99: a dialog that runs a destructive action itself veils its own content (omni-busy + aria-busy on its zone):\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void EverySectionThatConfirmsThroughTheServerBase_InheritsTheVeilOfThatBase()
    {
        var all = Sources().ToList();
        var @base = all.Single(s => s.Relative == "Components/Servers/ServerDetailSections/ServerActionSectionBase.cs");
        Assert.True(VeilRun.IsMatch(@base.Code), "ServerActionSectionBase.AskThenRunAsync runs the confirmed action under the veil.");

        var callers = all.Where(s => s.Delegated > 0).ToList();
        Assert.True(callers.Count >= 2, "The scan found fewer than the 2 known sections that confirm through ShowDestructiveConfirm: it is broken.");
        var violations = callers
            .Where(s => !s.Code.Contains(": ServerActionSectionBase", StringComparison.Ordinal))
            .Select(s => s.Relative)
            .ToList();
        Assert.True(violations.Count == 0,
            "R-99: ShowDestructiveConfirm is the base's, which veils the action; a source that calls it must derive from ServerActionSectionBase:\n  "
            + string.Join("\n  ", violations));
    }

    [Theory]
    [InlineData("new OmniConfirmOptions { Destructive = true, OkButtonText = x }", 1)]
    [InlineData("var ok = await Confirm.ConfirmDeleteAsync(\"k\", \"t\", name);", 1)]
    [InlineData("var ok = await Dialog.Confirm(m, t, ConfirmOptions(destructive, verb));", 1)]
    [InlineData("private async Task<bool> ConfirmDeleteAsync(string name) =>", 0)]
    [InlineData("private OmniConfirmOptions ConfirmOptions(bool destructive, string verb) => new()", 0)]
    [InlineData("new OmniConfirmOptions { Destructive = false }", 0)]
    public void TheDetector_RecognisesEachWayOfBuildingADestructiveConfirmation(string line, int expected) =>
        Assert.Equal(expected, CountConfirmations(line));

    /// <summary>
    /// The destructive confirmations a source builds. A source that declares its own <c>ConfirmDeleteAsync</c> helper
    /// builds them once, inside it (its literal <c>Destructive = true</c>), so the calls of that helper are not counted again.
    /// </summary>
    private static int CountConfirmations(string code)
    {
        var lines = code.Split('\n');
        var declaresItsOwnHelper = lines.Any(line => Definition.IsMatch(line) && line.Contains("ConfirmDeleteAsync(", StringComparison.Ordinal));
        return lines.Sum(line =>
            LiteralDestructive.Matches(line).Count
            + (declaresItsOwnHelper || Definition.IsMatch(line) ? 0 : DeleteHelperCall.Matches(line).Count)
            + OptionsFactoryCall.Matches(line).Count);
    }

    private static string PairedMarkup(string codePath) => File.ReadAllText(codePath[..^".cs".Length]);

    private static IEnumerable<Source> Sources()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        return RepositoryScan.Enumerate(front, "*.cs")
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(file =>
            {
                var code = File.ReadAllText(file);
                var delegated = code.Split('\n').Count(line => !Definition.IsMatch(line) && DelegatedConfirmCall.IsMatch(line));
                var confirmations = CountConfirmations(code);
                // A dialog that runs the action itself veils its own zone: its markup's omni-busy stands for the call.
                var ownZone = file.EndsWith("Dialog.razor.cs", StringComparison.Ordinal)
                              && PairedMarkup(file).Contains("omni-busy", StringComparison.Ordinal);
                return new Source(
                    file,
                    Path.GetRelativePath(front, file).Replace('\\', '/'),
                    code,
                    confirmations,
                    VeilRun.Matches(code).Count + (ownZone ? confirmations : 0),
                    delegated);
            });
    }

    private sealed record Source(string Path, string Relative, string Code, int Confirmations, int Runs, int Delegated);
}
