// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>Keep local virtualization off unless a grid provides bounded height; allow test hosts
/// to disable virtualization because bUnit has no measurable browser viewport.</summary>
public static class AetheusGrid
{
    /// <summary>False by default: embedded tables page. Never branch on this in product logic, it decides
    /// how rows are rendered, never what they contain.</summary>
    public static bool Virtualization { get; set; }

    /// <summary>Enables server-backed virtualization for standalone lists in the browser. Test hosts
    /// disable it because bUnit has no measurable viewport.</summary>
    public static bool RemoteVirtualization { get; set; } = true;

    /// <summary>Match the row estimate to the 2.25rem CSS row height at a 16px root size so virtual
    /// scrolling does not jump as measured heights replace estimates.</summary>
    public const double RowHeightPixels = 36d;

    /// <summary>Bound embedded or dialog grids without their own height through OE MaxHeight so
    /// local virtualization has a measurable window.</summary>
    public const string ScrollMaxHeight = "calc(100vh - 18rem)";

    /// <summary>R-120: about ten rows of the 2.25rem row height plus the header and the inline filter row, then the
    /// grid scrolls inside. For a page with two lists one above the other, neither of which may take the whole
    /// remaining height.</summary>
    public const string TenRowsMaxHeight = "28rem";

    /// <summary>Use a single blue Markdown action in the export area's STD-BTN exception; export is
    /// opt-in and ExportMarkdown=false disables it for tables whose contents must not be
    /// exported.</summary>
    public static readonly IReadOnlyDictionary<OmniTableExportFormat, OmniButtonVariant> ExportVariants =
        new System.Collections.ObjectModel.ReadOnlyDictionary<OmniTableExportFormat, OmniButtonVariant>(
            new Dictionary<OmniTableExportFormat, OmniButtonVariant> { [OmniTableExportFormat.Markdown] = OmniButtonVariant.Primary });
}
