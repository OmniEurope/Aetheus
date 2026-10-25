// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Guards the product-wide grid contract: every column remains rendered at every viewport
/// width, and compact screens use horizontal scrolling instead of responsive column collapse.</summary>
public sealed class ResponsiveGridColumnAuditTests
{
    [Fact]
    public void EveryGrid_DisablesResponsiveColumnCollapse()
    {
        var frontDir = Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");
        var razorFiles = RepositoryScan.Enumerate(frontDir, "*.razor");
        var gridCount = 0;

        foreach (var file in razorFiles)
        {
            var source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(
                         source,
                         @"<OmniDataGrid(?!Column)\b(?:(?:""[^""]*"")|[^>])*>",
                         RegexOptions.Singleline))
            {
                gridCount++;
                Assert.DoesNotContain("Responsive=\"true\"", match.Value, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("col-hide-", source, StringComparison.Ordinal);
            Assert.DoesNotContain("HiddenColumnsHint", source, StringComparison.Ordinal);
        }

        Assert.True(gridCount > 0, "No OE data grids were found to audit.");
    }

    /// <summary>
    /// Every data grid is sortable and advanced-filterable. All 81 grids already satisfied this when
    /// the guard was written on 2026-08-19 - it exists to keep it that way, since a new grid silently
    /// missing the attributes is invisible until a user needs to find a row.
    /// </summary>
    [Fact]
    public void EveryGrid_AllowsSortingAndAdvancedFiltering()
    {
        var frontDir = Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");
        var violations = new List<string>();
        var gridCount = 0;

        foreach (var file in RepositoryScan.Enumerate(frontDir, "*.razor"))
        {
            var source = File.ReadAllText(file);
            var relative = Path.GetRelativePath(frontDir, file).Replace('\\', '/');

            foreach (Match match in Regex.Matches(
                         source,
                         @"<OmniDataGrid(?!Column)\b(?:(?:""[^""]*"")|[^>])*>",
                         RegexOptions.Singleline))
            {
                gridCount++;
                var disabled = new List<string>();
                if (match.Value.Contains("AllowSorting=\"false\"", StringComparison.Ordinal)) disabled.Add("AllowSorting");
                if (match.Value.Contains("AllowFiltering=\"false\"", StringComparison.Ordinal)) disabled.Add("AllowFiltering");
                if (disabled.Count > 0) violations.Add($"{relative}: disables {string.Join(" + ", disabled)}");
            }
        }

        Assert.True(gridCount > 0, "No OE data grids were found to audit.");
        Assert.True(violations.Count == 0,
            "Every data grid sets AllowSorting and AllowFiltering (docs/contracts/ui-patterns.md):\n  "
            + string.Join("\n  ", violations.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void EveryGrid_HasAHorizontalOverflowAndDefaultWidthContract()
    {
        var css = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Aetheus.Front", "wwwroot", "css", "app.css"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(".omni-data-grid .omni-data-grid__viewport {\n    overflow-x: auto;", css);
        Assert.Contains(
            // The default reaches only columns without a width of their own: an OE Width arrives as
            // --omni-col-width and must win (recette 2026-09-21, every column was 172px).
            ".omni-data-grid .omni-data-grid__table > colgroup > col:not([style^=\"width:\"]):not([style*=\";width:\"]):not([style*=\"; width:\"]):not([style*=\"--omni-col-width\"]) {\n    width: max(10rem, var(--omni-col-min, 0px));",
            css);
        Assert.DoesNotContain(".omni-data-grid .col-hide-", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".hidden-columns-hint", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".pipeline-project-column {\n        display: none", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".pipeline-secondary-column {\n        display: none", css, StringComparison.Ordinal);
    }

    [Fact]
    public void DenseProjectAndPipelineTrees_ScrollWithoutHidingColumns()
    {
        var css = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Aetheus.Front", "wwwroot", "css", "app.css"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(".project-dense-list {\n    border:", css, StringComparison.Ordinal);
        Assert.Contains("overflow-x: auto;", css, StringComparison.Ordinal);
        Assert.Contains(".project-dense-header,\n.project-dense-row", css, StringComparison.Ordinal);
        Assert.Contains("min-width: 58rem;", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".project-dense-users {\n        display: none", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".project-dense-header {\n        display: none", css, StringComparison.Ordinal);

        Assert.Contains(".pipeline-linked-runs {", css, StringComparison.Ordinal);
        Assert.Contains(".pipeline-run-tree-row {", css, StringComparison.Ordinal);
        Assert.Contains("min-width: 52rem;", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".pipeline-run-tree-step,\n    .pipeline-run-tree-duration {\n        display: none", css, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactSidebar_LayoutAndJavascriptShareTheOmniEuropeMediumBoundary()
    {
        var root = FindRepoRoot();
        var layout = File.ReadAllText(Path.Combine(
            root, "src", "Aetheus.Front", "Layout", "MainLayout.razor"));
        var layoutCode = File.ReadAllText(Path.Combine(
            root, "src", "Aetheus.Front", "Layout", "MainLayout.razor.cs"));
        var javascript = File.ReadAllText(Path.Combine(
            root, "src", "Aetheus.Front", "wwwroot", "js", "layout.js"));

        // The sidebar and both toggles read one reveal, decided on the same boundary as the JS watcher.
        Assert.Contains("Reveal=\"@SidebarReveal\"", layout, StringComparison.Ordinal);
        Assert.Contains("SidebarReveal => _isMobile ? OmniSidebarReveal.Overlay : OmniSidebarReveal.Push;", layoutCode, StringComparison.Ordinal);
        Assert.Contains("Collapse=\"@(_isMobile ? OmniSidebarCollapse.Hidden : OmniSidebarCollapse.Icons)\"", layout, StringComparison.Ordinal);
        Assert.Contains("matchMedia('(max-width: 63.99rem)')", javascript, StringComparison.Ordinal);
        Assert.Contains(
            "return dotNetRef.invokeMethodAsync('OnViewportChanged'",
            javascript,
            StringComparison.Ordinal);
    }

    /// <summary>R-35 (user decision of 2026-10-22): a grid column title is never truncated. The app adds
    /// no stylesheet rule that forces a header onto one line or cuts it with an ellipsis, and no grid
    /// sets <c>HeaderWrap</c> to Truncate.</summary>
    [Fact]
    public void EveryGrid_HeaderTitleIsNeverTruncated()
    {
        var css = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

        var headerRules = Regex.Matches(css, @"(?<selector>[^{}]*\.omni-data-grid__(?:title|header)\b[^{}]*)\{(?<body>[^}]*)\}");
        foreach (Match rule in headerRules)
        {
            Assert.False(
                Regex.IsMatch(rule.Groups["body"].Value, @"white-space\s*:\s*nowrap|text-overflow\s*:\s*ellipsis"),
                $"R-35: header rule '{rule.Groups["selector"].Value.Trim()}' truncates or forces a column title onto one line.");
        }

        var frontDir = Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");
        var truncatingSources = RepositoryScan.Enumerate(frontDir, "*.razor")
            .Concat(RepositoryScan.Enumerate(frontDir, "*.cs"))
            .Where(file => File.ReadAllText(file).Contains("OmniDataGridHeaderWrap.Truncate", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(frontDir, file))
            .ToList();
        Assert.True(truncatingSources.Count == 0,
            $"R-35: column titles are never truncated, remove HeaderWrap.Truncate from: {string.Join(", ", truncatingSources)}");
    }

    [Fact]
    public void PipelineActionsColumn_SizesToItsContentAndKeepsItsButtonsOnOneLine()
    {
        // R-6 (user decision 2026-10-24): the actions column of the pipeline grids sizes to its content
        // like every other column (grid preset ColumnSizing.Content) instead of being pinned to a
        // width, and its row buttons never wrap onto a second line.
        var root = FindRepoRoot();
        var css = File.ReadAllText(Path.Combine(root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

        foreach (Match rule in Regex.Matches(css, @"(?<selector>[^{}]*\.pipeline-actions-column\b[^{}]*)\{(?<body>[^}]*)\}"))
        {
            Assert.False(
                Regex.IsMatch(rule.Groups["body"].Value, @"(?<![\w-])(?:min-|max-)?width\s*:"),
                $"R-6: rule '{rule.Groups["selector"].Value.Trim()}' gives the pipeline actions column a fixed width; it sizes to its content.");
        }

        var rowActionsRules = Regex.Matches(css, @"(?<selector>[^{}]*\.pipeline-row-actions\b[^{}]*)\{(?<body>[^}]*)\}")
            .Where(rule => !rule.Groups["selector"].Value.Contains('@', StringComparison.Ordinal))
            .ToList();
        Assert.True(
            rowActionsRules.Any(rule =>
                Regex.IsMatch(rule.Groups["body"].Value, @"white-space\s*:\s*nowrap")
                && Regex.IsMatch(rule.Groups["body"].Value, @"flex-wrap\s*:\s*nowrap")),
            "R-6: .pipeline-row-actions must keep the row action buttons on one line (white-space and flex-wrap nowrap).");

        var gridSource = File.ReadAllText(Path.Combine(root, "src", "Aetheus.Front", "Components", "Shared", "PipelineDependencyGrid.razor"));
        var actionsColumn = Regex.Match(gridSource, @"<OmniDataGridColumn\b[^>]*pipeline-actions-column[^>]*>");
        Assert.True(actionsColumn.Success, "R-6: the pipeline actions column was not found in PipelineDependencyGrid.razor.");
        Assert.DoesNotMatch(@"\s(?:Width|MinWidth|MaxWidth)\s*=", actionsColumn.Value);
        Assert.Contains("pipeline-row-actions", gridSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Wrap=\"true\"", Regex.Match(gridSource, @"<OmniStack\b[^>]*pipeline-row-actions[^>]*>").Value, StringComparison.Ordinal);
    }

    private static string FindRepoRoot() => Aetheus.Front.Tests.Architecture.RepositoryScan.Root;
}
