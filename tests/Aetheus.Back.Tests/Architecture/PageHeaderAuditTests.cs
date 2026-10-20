// SPDX-License-Identifier: EUPL-1.2

using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>Routed pages use OmniPageHeader directly or through their layout for consistent
/// geometry.</summary>
public sealed class PageHeaderAuditTests
{
    /// <summary>Pages that render no chrome of their own: the two authentication screens, plus the two
    /// pure redirectors that immediately navigate elsewhere and never paint a page.</summary>
    private static readonly string[] ChromelessPages =
        ["Login.razor", "ChangePasswordRequired.razor", "ServerDetail.razor", "ExternalRepo.razor"];

    /// <summary>Components that render the page header for the page hosting them.</summary>
    private static readonly string[] HeaderRenderingComponents = ["PipelineRunHeader"];

    /// <summary>Layouts that render the header on behalf of the sections routed inside them.</summary>
    private static readonly string[] HeaderOwningLayouts =
        ["ProjectDetailLayout", "ServerDetailLayout"];

    [Fact]
    public void RoutedPages_DoNotHandRollATitle()
    {
        var offenders = RoutedPages()
            .Where(page => Regex.IsMatch(page.Text, @"class=""[^""]*rz-text-h[1-4]\b"))
            .Select(page => page.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"These pages write their own title instead of using OmniPageHeader: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void RoutedPages_RenderExactlyOneHeader_ThemselvesOrThroughTheirLayout()
    {
        var pages = RoutedPages().ToList();
        Assert.True(pages.Count >= 40, $"Only {pages.Count} routed pages scanned; the guard is not seeing the app.");

        var offenders = pages
            .Where(page => !ChromelessPages.Contains(page.Name, StringComparer.Ordinal))
            .Where(page => !page.Text.Contains("<OmniPageHeader", StringComparison.Ordinal)
                && !HeaderOwningLayouts.Any(layout => Regex.IsMatch(page.Text, @"@layout\s+([A-Za-z]+\.)*" + layout + @"\b"))
                && !HeaderRenderingComponents.Any(component => page.Text.Contains($"<{component}", StringComparison.Ordinal)))
            .Select(page => page.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"These routed pages render no OmniPageHeader and no header-owning layout: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void PagesInsideAHeaderOwningLayout_DoNotStackASecondHeader()
    {
        // The other half of "exactly one": the layout already draws the frame and the whole trail, so
        // a page routed inside it that adds its own OmniPageHeader shows both twice.
        var offenders = RoutedPages()
            .Where(page => HeaderOwningLayouts.Any(layout =>
                Regex.IsMatch(page.Text, @"@layout\s+([A-Za-z]+\.)*" + layout + @"\b")))
            .Where(page => Regex.IsMatch(page.Text, @"<OmniPageHeader\b"))
            .Select(page => page.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"These pages stack a second header on top of their layout's: {string.Join(", ", offenders)}");
    }

    /// <summary>Routed Razor components under Components.</summary>
    private static IEnumerable<(string Name, string Text)> RoutedPages() =>
        RepositoryScan.Enumerate(
                Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Components"), "*.razor")
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .Where(page => page.Text.Contains("@page ", StringComparison.Ordinal));

}
