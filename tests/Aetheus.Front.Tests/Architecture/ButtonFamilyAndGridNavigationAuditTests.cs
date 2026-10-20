// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Guards the two global interaction contracts: recurring button actions keep their STD-BTN
/// colour (revised 2026-09-28, colour follows importance: a Create or Add button is the main action of its
/// zone, blue; Delete is destructive, red), and grids consistently expose inline filtering, sorting and an
/// honest row destination. The rules that a label alone cannot decide (no green button, one blue button
/// per zone, destructive handlers, cancellation dialogs) live in <see cref="ButtonRoleColourGuardTests"/>.</summary>
public class ButtonFamilyAndGridNavigationAuditTests
{
    private static readonly IReadOnlyDictionary<string, string> CanonicalActionVariants =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Add"] = "OmniButtonVariant.Primary",
            ["Create"] = "OmniButtonVariant.Primary",
            ["Delete"] = "OmniButtonVariant.Danger"
        };

    [Fact]
    public void RecurringButtonActions_UseTheirDesignFamily()
    {
        var violations = new List<string>();

        foreach (var (file, tag) in EnumerateButtonTags())
        {
            var keyMatch = Regex.Match(tag, @"@L\[""(?<key>[^""]+)""\]");
            if (!keyMatch.Success || !CanonicalActionVariants.TryGetValue(keyMatch.Groups["key"].Value, out var expected))
                continue;
            var variant = Attribute(tag, "Variant");
            if (!string.Equals(variant, expected, StringComparison.Ordinal))
                violations.Add($"{file}: {keyMatch.Groups["key"].Value} must use {expected}, found {variant}");
        }

        Assert.True(violations.Count == 0,
            "Recurring OE button actions must use the shared semantic family:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void ClickableGridClass_AlwaysHasARowDestination()
    {
        var violations = new List<string>();

        foreach (var (file, tag) in EnumerateRawGridTags())
        {
            if (!tag.Contains("aetheus-clickable-rows", StringComparison.Ordinal)) continue;
            if (!tag.Contains("RowClick=", StringComparison.Ordinal)) violations.Add(file);
        }

        Assert.True(violations.Count == 0,
            "A grid must not advertise clickable rows without a primary RowClick destination:\n  "
            + string.Join("\n  ", violations.Distinct()));
    }

    [Fact]
    public void EveryRawGrid_ExposesSortingAndInlineFilters()
    {
        var violations = new List<string>();

        foreach (var (file, tag) in EnumerateRawGridTags())
        {
            var sorting = Attribute(tag, "AllowSorting");
            var filtering = Attribute(tag, "AllowFiltering");
            var filterMode = Attribute(tag, "FilterMode");
            if (sorting is "false"
                || filtering is "false"
                || filterMode is not null and not "OmniDataGridFilterMode.Simple" and not "@FilterMode")
                violations.Add(file);
        }

        Assert.True(violations.Count == 0,
            "Every data grid must keep sorting and simple inline header filters enabled:\n  "
            + string.Join("\n  ", violations.Distinct()));
    }

    [Fact]
    public void EverySharedGrid_UsesSimpleDefaultsOrADocumentedServerConstraint()
    {
        var documentedFilterExceptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Components/Organizations/Organizations.razor",
            "Components/Notifications/NotificationsAdmin.razor",
            "Components/PackageRegistry/PackageRegistryAdmin.razor",
            "Components/PackageFeeds/PackageFeedsAdmin.razor",
            "Components/Pipelines/PipelineEdit.razor",
            "Components/Servers/ServerDetailSections/ModuleLinksTab.razor"
        };
        var documentedSortExceptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Components/Pipelines/PipelineEdit.razor"
        };
        var violations = new List<string>();

        foreach (var (file, tag) in EnumerateSharedGridTags())
        {
            if (string.Equals(Attribute(tag, "AllowSorting"), "false", StringComparison.Ordinal)
                && !documentedSortExceptions.Contains(file))
                violations.Add($"{file}: sorting disabled without a documented server constraint");
            if (string.Equals(Attribute(tag, "AllowFiltering"), "false", StringComparison.Ordinal)
                && !documentedFilterExceptions.Contains(file))
                violations.Add($"{file}: filtering disabled without a documented server constraint");
        }

        var sharedSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Aetheus.Front", "Components", "Shared", "AetheusDataGrid.razor.cs"));
        Assert.Contains("public bool AllowSorting { get; set; } = true;", sharedSource);
        Assert.Contains("public bool AllowFiltering { get; set; } = true;", sharedSource);
        Assert.Contains("public OmniDataGridFilterMode FilterMode { get; set; } = OmniDataGridFilterMode.Simple;", sharedSource);
        Assert.True(violations.Count == 0,
            "Shared grids must keep sorting and advanced filtering unless their paged endpoint cannot express them:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void EveryRawGrid_UsesTheSharedCompactRowDensity()
    {
        var violations = new List<string>();

        foreach (var (file, tag) in EnumerateRawGridTags())
        {
            // Omitted grid density uses the registered Compact preset, which raw grids must retain.
            var density = Attribute(tag, "Density");
            if (density is not null and not "OmniDensity.Compact" and not "@Density")
                violations.Add(file);
        }

        Assert.True(violations.Count == 0,
            "Every OE data grid must use the shared compact row density:\n  "
            + string.Join("\n  ", violations.Distinct()));
    }

    [Fact]
    public void EveryDataGridLink_UsesTheSharedPipelineLinkStyle()
    {
        var css = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Aetheus.Front", "wwwroot", "css", "app.css"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(".pipeline-name-link,\n.server-name-link,\n.omni-data-grid tr[data-omni-row-index] a:not(.omni-button)", css);
        Assert.Contains(".omni-data-grid tr[data-omni-row-index] a:not(.omni-button):hover", css);
    }


    private static IEnumerable<(string File, string Tag)> EnumerateButtonTags()
    {
        var front = Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");
        foreach (var file in RepositoryScan.Enumerate(front, "*.razor"))
        {
            var source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source,
                         @"<OmniButton\b(?:(?:""[^""]*"")|[^>])*?>.*?</OmniButton>", RegexOptions.Singleline))
                yield return (Path.GetRelativePath(front, file), match.Value);
        }
    }

    private static IEnumerable<(string File, string Tag)> EnumerateRawGridTags()
    {
        var front = Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");
        foreach (var file in RepositoryScan.Enumerate(front, "*.razor"))
        {
            var source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source, @"<OmniDataGrid(?!Column)\b(?:(?:""[^""]*"")|[^>])*?>", RegexOptions.Singleline))
                yield return (Path.GetRelativePath(front, file), match.Value);
        }
    }

    private static IEnumerable<(string File, string Tag)> EnumerateSharedGridTags()
    {
        var front = Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");
        foreach (var file in RepositoryScan.Enumerate(front, "*.razor"))
        {
            var source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source,
                         @"<(?:Aetheus\.Front\.Shared\.)?AetheusDataGrid\b(?:(?:""[^""]*"")|[^>])*?>",
                         RegexOptions.Singleline))
                yield return (Path.GetRelativePath(front, file).Replace('\\', '/'), match.Value);
        }
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $@"\b{Regex.Escape(name)}\s*=\s*""(?<v>[^""]*)""");
        return match.Success ? match.Groups["v"].Value : null;
    }

    private static string FindRepoRoot() => Aetheus.Front.Tests.Architecture.RepositoryScan.Root;
}
