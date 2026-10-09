// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Product-wide switch for local grid row virtualization, the default of the wrapper's
/// <c>Virtualize</c>. Off by default: every grid declares how it scrolls instead (recette R-327,
/// enforced by <c>GridCapabilityAuditTests</c>), so this switch only decides an undeclared grid.
/// </summary>
/// <remarks>
/// Local virtualization requires a bounded height, so enabling it product-wide gave embedded tables
/// a second scrollbar. Standalone remote lists use <see cref="RemoteVirtualization"/> instead.
/// <para>
/// This is a switch rather than a constant because it stays useful in the other direction: a view
/// with a genuinely unbounded row count can opt back in per grid, and the test host can flip it
/// globally. Under bUnit there is no layout and no real JS runtime, so a virtualised grid measures a
/// zero-height viewport and renders no rows at all, which would fail render tests for a reason that
/// has nothing to do with the component.
/// </para>
/// </remarks>
public static class AetheusGrid
{
    /// <summary>False by default: embedded tables page. Never branch on this in product logic, it decides
    /// how rows are rendered, never what they contain.</summary>
    public static bool Virtualization { get; set; }

    /// <summary>Enables server-backed virtualization for standalone lists in the browser. Test hosts
    /// disable it because bUnit has no measurable viewport.</summary>
    public static bool RemoteVirtualization { get; set; } = true;

    /// <summary>
    /// The height of one data row in CSS pixels, the 2.25rem of <c>--aetheus-grid-row-height</c> at the
    /// 16px root size. Handed to the grid as its row estimate: OE's default of 40px made the virtual
    /// scroll height shrink at every correction and the scrollbar jump (recette R-087).
    /// </summary>
    public const double RowHeightPixels = 36d;

    /// <summary>
    /// Recette R-327: the ceiling of a local grid that scrolls instead of paging, handed to OE's
    /// <c>MaxHeight</c> by an embedded or dialog grid that has no height of its own. The table takes the
    /// height of its rows up to this length, then scrolls and renders only its window. The same cap as
    /// the <c>.aetheus-grid-virtualized</c> body of the wrapper.
    /// </summary>
    public const string ScrollMaxHeight = "calc(100vh - 18rem)";

    /// <summary>
    /// Recette R2-009 (2026-10-01, the user's exception to STD-BTN): on every export bar of a grid, the
    /// Markdown export (for a prompt) is the bar's one blue button; the other formats keep OE's Ghost.
    /// The bar is a zone of its own, so it still holds a single blue button.
    /// </summary>
    /// <remarks>
    /// Recette R2-124 (decisions of 2026-10-05), OE 1.6.0: a table has no export bar by default
    /// (<c>ShowFooterBar</c> off) and offers Markdown alone (for a prompt), never CSV, Excel or PDF: the
    /// preset and <c>AetheusDataGrid</c> turn those three formats off. A table worth handing to an AI writes
    /// <c>ShowFooterBar="true"</c> and gets the Markdown button at the end (right) of a bar under its rows, inside
    /// its frame; a table that must never be exported (a secret value on screen, say) writes
    /// <c>ExportMarkdown="false"</c>. <c>GridExportCoverageAuditTests</c> holds both to their lists, each
    /// table with its reason.
    /// </remarks>
    public static readonly IReadOnlyDictionary<OmniTableExportFormat, OmniButtonVariant> ExportVariants =
        new System.Collections.ObjectModel.ReadOnlyDictionary<OmniTableExportFormat, OmniButtonVariant>(
            new Dictionary<OmniTableExportFormat, OmniButtonVariant> { [OmniTableExportFormat.Markdown] = OmniButtonVariant.Primary });
}
