// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Recette R-111 (rule 3 of R-30): a list grid fills the height the window leaves it, never less than three
/// quarters of the screen, so a page of rows has no useless page scrollbar and no short window of its own.
/// A grid states it with <c>FullHeight</c> (the Aetheus wrapper) or <c>FillAvailableHeight</c> (a raw OE grid).
/// <para>The few grids where filling is wrong are listed below with the reason, and counted per file: a new
/// non-filling grid in a listed file fails, and so does a listed file that no longer needs its entry.</para>
/// </summary>
public sealed class PageGridFillAuditTests
{
    private static readonly string FrontRoot = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");

    /// <summary>The wrapper itself forwards <c>FullHeight</c> to the OE grid; it is checked by
    /// <c>GridCapabilityAuditTests</c>, not by this sweep.</summary>
    private const string WrapperFile = "AetheusDataGrid.razor";

    private const string Dialog = "Grid in a dialog body: the dialog sizes itself to its content, a filling grid has no remainder to take there.";
    private const string Tile = "Dashboard tile: a bounded grid among other tiles, the page is not a list.";
    private const string SecondaryBelow = "Secondary list under (or above) the page's filling grid: the page has one filling list, a second one would double the three-quarter floor.";
    private const string FormSection = "List inside a form or settings section next to fields and cards, not the page's main list.";
    private const string PipelinePanel = "Result or readiness panel of a pipeline page, one block among cards and alerts, sized by its own content.";

    /// <summary>File (relative to the front project, forward slashes) to the number of non-filling grids it may
    /// hold and why. Entries need the user's approval: filling is the rule.</summary>
    private static readonly Dictionary<string, (int Count, string Reason)> Allowed =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Components/Alerts/Alerts.razor"] = (1, "Recent alerts of this session (50 rows at most) above the rules grid, which is the page's filling list. " + SecondaryBelow),
            ["Components/AppBackups/Backups.razor"] = (1, "Run history of one policy, opened under the policies grid, which is the page's filling list. " + SecondaryBelow),
            ["Components/Notifications/NotificationsAdmin.razor"] = (1, "Channels (a handful of transports) above the rules grid, which is the page's filling list. " + SecondaryBelow),
            ["Components/Monitoring/ReclaimSpaceView.razor"] = (2, "Administrator maintenance section under the supervision grid (tables to vacuum and their history). " + SecondaryBelow),
            ["Components/Projects/MonitoredAppsList.razor"] = (1, "The project's few monitored applications, above the telemetry tabs whose grids fill. " + SecondaryBelow),
            ["Components/Servers/ServerDetailSections/DockerContainersTab.razor"] = (1, "Environment variables of one container, a detail panel opened under the containers grid. " + SecondaryBelow),
            ["Components/Servers/ServerDetailSections/ServerApacheSection.razor"] = (2, "Recette R-120, the user's choice over the three-quarter rule (R-1/R-111) for this page: the enabled and the disabled sites grids are each capped to about ten rows and scroll inside (MaxHeight), neither fills the remaining height. The modules grid of the other tab still fills."),
            ["Components/Dashboards/Home.razor"] = (2, Tile),
            ["Components/Shared/MonitoredAppsStatusGrid.razor"] = (1, Tile),
            ["Components/Mail/MailboxView.razor"] = (1, "Message list of the three-pane mailbox, one column of a responsive row beside the folders and the reader."),
            ["Components/Notifications/NotificationPreferencesPanel.razor"] = (1, FormSection),
            ["Components/Users/RoleAssignmentPicker.razor"] = (1, FormSection),
            ["Components/Users/RoleEdit.razor"] = (1, "Permission matrix of the role form, beside the filling users grid. " + FormSection),
            ["Components/Users/EntityAuditTrail.razor"] = (1, "Audit trail inside a card of a detail page's tab."),
            ["Components/Shared/AppIngestionSettings.razor"] = (1, "Thresholds list under the ingestion settings cards. " + FormSection),
            ["Components/Shared/QualityGatePolicyEditor.razor"] = (2, "Rule tables of the quality gate policy editor. " + FormSection),
            ["Components/Projects/ProjectDetailSections/ProjectOverviewSection.razor"] = (1, "Card of the project overview, among other cards."),
            ["Components/Servers/ServerDetailSections/ServerOverviewSection.razor"] = (1, "Scanner capabilities card of the server overview, among other cards."),
            ["Components/Servers/ServerDetailSections/ServerPackageInventory.razor"] = (1, "Read-only package list closing the 'other services' tab, under two service grids."),
            ["Components/Servers/ServerDetailSections/ServerMailDeliveryPanel.razor"] = (1, "Deferred messages of the mail delivery panel, one block among the panel's facts."),
            ["Components/Pipelines/PipelineReadinessEmptyValues.razor"] = (1, PipelinePanel),
            ["Components/Pipelines/PipelineRunAiResultsCard.razor"] = (1, PipelinePanel),
            ["Components/Pipelines/PipelineRunResultsTabs.razor"] = (1, PipelinePanel),
            ["Components/Pipelines/PipelineRunSchemaTab.razor"] = (1, PipelinePanel),
            ["Components/Pipelines/SchemaPendingContractions.razor"] = (1, PipelinePanel),
            ["Components/Pipelines/ReleasePickerGrid.razor"] = (1, "Release picker embedded in a pipeline form field."),
            ["Components/Pipelines/DryRunResultDialog.razor"] = (2, Dialog),
            ["Components/Pipelines/PipelineRunParametersDialog.razor"] = (1, Dialog),
            ["Components/Pipelines/PipelineRunVariablesDialog.razor"] = (1, Dialog),
            ["Components/Pipelines/TemplateVersionHistoryDialog.razor"] = (1, Dialog),
            ["Components/PackageFeeds/PackageFeedPackagesDialog.razor"] = (1, Dialog),
            ["Components/PackageRegistry/PackageRegistryPackageDialog.razor"] = (1, Dialog),
            ["Components/Servers/RetiredServersDialog.razor"] = (1, Dialog),
            ["Components/Servers/ServerDetailSections/DockerProjectDialog.razor"] = (5, Dialog),
            ["Components/VariableLibraries/VersionHistoryDialog.razor"] = (1, Dialog),
            ["Components/Vaults/SecretVersionHistoryDialog.razor"] = (1, Dialog),
        };

    private static readonly Regex GridOpen = new(
        @"<(AetheusDataGrid|OmniDataGrid)(?![A-Za-z])",
        RegexOptions.Compiled);

    /// <summary>Reads one opening tag, ending it at the first '&gt;' outside a quoted value, so the arrow of a
    /// lambda in an attribute does not cut the tag short (see <c>GridCapabilityAuditTests</c>).</summary>
    private static IEnumerable<string> TagsIn(string text)
    {
        foreach (Match open in GridOpen.Matches(text))
        {
            var index = open.Index + open.Length;
            char? quote = null;
            for (; index < text.Length; index++)
            {
                var current = text[index];
                if (quote is not null)
                {
                    if (current == quote) quote = null;
                    continue;
                }
                if (current is '"' or '\'') { quote = current; continue; }
                if (current == '>') break;
            }
            if (index >= text.Length)
                throw new InvalidOperationException("Unterminated grid tag.");
            yield return text[open.Index..(index + 1)];
        }
    }

    /// <summary>True when the tag fills: the attribute is bound to a literal other than false, or to an
    /// expression (a grid that computes it per context still made the decision explicitly).</summary>
    internal static bool Fills(string tag) =>
        Regex.IsMatch(tag, @"(?<=[\s<])(FullHeight|FillAvailableHeight)\s*=\s*""(?!false"")", RegexOptions.IgnoreCase);

    /// <summary>Grids per file (forward-slash path relative to the front project): total and non-filling.</summary>
    private static Dictionary<string, (int Total, int NonFilling)> Scan()
    {
        var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in RepositoryScan.Enumerate(FrontRoot, "*.razor"))
        {
            if (Path.GetFileName(file).Equals(WrapperFile, StringComparison.OrdinalIgnoreCase)) continue;
            var tags = TagsIn(File.ReadAllText(file)).ToList();
            if (tags.Count == 0) continue;
            var relative = Path.GetRelativePath(FrontRoot, file).Replace('\\', '/');
            result[relative] = (tags.Count, tags.Count(tag => !Fills(tag)));
        }

        Assert.True(result.Count > 0, "No grid found. A guard that scans nothing passes vacuously.");
        return result;
    }

    [Fact]
    public void EveryGrid_FillsTheAvailableHeight_UnlessListedWithItsReason()
    {
        var scan = Scan();
        var offenders = scan
            .Where(entry => entry.Value.NonFilling > (Allowed.TryGetValue(entry.Key, out var allowed) ? allowed.Count : 0))
            .Select(entry => $"{entry.Key}: {entry.Value.NonFilling} grid(s) without FullHeight/FillAvailableHeight, "
                             + $"{(Allowed.TryGetValue(entry.Key, out var a) ? a.Count : 0)} allowed")
            .ToList();

        Assert.True(offenders.Count == 0,
            "A list grid fills the height the window leaves it, three quarters of the screen at least "
            + "(recette R-111: FullHeight on AetheusDataGrid, FillAvailableHeight on OmniDataGrid). "
            + "Not filling in:\n" + string.Join("\n", offenders));
    }

    /// <summary>The allow-list cannot rot: an entry whose file now fills (or is gone) is removed, and a file
    /// that changed its number of non-filling grids states the new number.</summary>
    [Fact]
    public void TheAllowList_MatchesTheGridsThatStillDoNotFill()
    {
        var scan = Scan();
        var stale = Allowed
            .Where(entry => !scan.TryGetValue(entry.Key, out var found) || found.NonFilling != entry.Value.Count)
            .Select(entry => $"{entry.Key}: allows {entry.Value.Count}, found "
                             + $"{(scan.TryGetValue(entry.Key, out var f) ? f.NonFilling.ToString() : "no grid")}")
            .ToList();

        Assert.True(stale.Count == 0, "Allow-list entries out of date:\n" + string.Join("\n", stale));
        Assert.All(Allowed.Values, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Reason)));
    }

    /// <summary>The guard must see both kinds of grid and both outcomes, otherwise the checks above are
    /// vacuous: a wrapper grid that fills, a raw grid that fills, and grids that do not.</summary>
    [Fact]
    public void TheScan_SeesFillingAndNonFillingGridsOfBothKinds()
    {
        var wrapperFills = 0;
        var rawFills = 0;
        var nonFilling = 0;
        foreach (var file in RepositoryScan.Enumerate(FrontRoot, "*.razor"))
        {
            if (Path.GetFileName(file).Equals(WrapperFile, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var tag in TagsIn(File.ReadAllText(file)))
            {
                if (!Fills(tag)) nonFilling++;
                else if (tag.StartsWith("<AetheusDataGrid", StringComparison.Ordinal)) wrapperFills++;
                else rawFills++;
            }
        }

        Assert.True(wrapperFills >= 40, $"Only {wrapperFills} filling wrapper grids found.");
        Assert.True(rawFills >= 15, $"Only {rawFills} filling raw grids found.");
        Assert.True(nonFilling >= 20, $"Only {nonFilling} non-filling grids found, the allow-list covers them.");
    }

    [Theory]
    [InlineData("<AetheusDataGrid FullHeight=\"true\" Data=\"@x\">", true)]
    [InlineData("<OmniDataGrid FillAvailableHeight=\"true\" Items=\"@x\">", true)]
    [InlineData("<OmniDataGrid FillAvailableHeight=\"@Installed\" Items=\"@x\">", true)]
    [InlineData("<AetheusDataGrid Data=\"@x\" Class=\"aetheus-grid-fullheight\">", false)]
    [InlineData("<OmniDataGrid MaxHeight=\"@AetheusGrid.ScrollMaxHeight\" Items=\"@x\">", false)]
    [InlineData("<AetheusDataGrid FullHeight=\"false\" Data=\"@x\">", false)]
    public void Fills_ReadsTheDeclaration(string tag, bool expected) =>
        Assert.Equal(expected, Fills(tag));
}
