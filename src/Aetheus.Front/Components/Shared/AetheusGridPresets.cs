// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// PLAN-012: the settings every Aetheus table shares, registered once as the default OmniEurope preset of
/// <see cref="OmniDataGrid{TItem}"/>. The raw grids no longer repeat them, so a grid cannot silently miss
/// one (recette R-086). A grid still writes a setting explicitly to differ.
/// Virtualization stays per grid: it needs a bounded height, and <see cref="AetheusGrid.Virtualization"/>
/// is a switch read at render, not a value to freeze at startup.
/// </summary>
public static class AetheusGridPresets
{
    public const string Default = "aetheus";

    /// <summary>Idempotent, like AddOmniEuropeBlazor: a host (or a test) that wires twice registers once.</summary>
    public static IServiceCollection AddAetheusGridPresets(this IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(OmniPresetRegistry))
            ? services
            : services.AddOmniEuropePreset(typeof(OmniDataGrid<>), Default, new Dictionary<string, object?>
            {
                [nameof(OmniDataGrid<object>.AllowSorting)] = true,
                [nameof(OmniDataGrid<object>.Filterable)] = true,
                [nameof(OmniDataGrid<object>.FilterMode)] = OmniDataGridFilterMode.Simple,
                [nameof(OmniDataGrid<object>.ShowHeaderFilterMenu)] = true,
                [nameof(OmniDataGrid<object>.AllowColumnResize)] = true,
                // OE made the double click on a column edge (fit to content) opt-in; Aetheus tables had it.
                [nameof(OmniDataGrid<object>.AllowColumnAutoFit)] = true,
                [nameof(OmniDataGrid<object>.AllowAlternatingRows)] = true,
                [nameof(OmniDataGrid<object>.HighlightRowOnHover)] = true,
                [nameof(OmniDataGrid<object>.Density)] = OmniDensity.Compact,
                // STD-GRIDTITLE (recette R-538): a column title stays on one line and is cut with an
                // ellipsis when it does not fit; OE wraps titles by default.
                [nameof(OmniDataGrid<object>.HeaderWrap)] = OmniDataGridHeaderWrap.Truncate,
                // Recette R2-124 (second decision of 2026-10-05): no export bar by default (OE's
                // ShowFooterBar is off) and Markdown alone; a table worth handing to an AI writes
                // ShowFooterBar="true" and gets the Markdown button at the end (right) of a bar under its rows,
                // blue (R2-009). A raw grid holds its rows in Items, so the export writes all of them, not
                // the window on screen. A table that must not be exported writes ExportMarkdown="false"
                // (GridExportCoverageAuditTests).
                // User decision of 2026-10-06: the export buttons at the end (right) of the bar; OE's
                // default stays the start.
                [nameof(OmniDataGrid<object>.FooterBarExport)] = OmniDataGridBarExport.End,
                [nameof(OmniDataGrid<object>.ExportCsv)] = false,
                [nameof(OmniDataGrid<object>.ExportExcel)] = false,
                [nameof(OmniDataGrid<object>.ExportPdf)] = false,
                [nameof(OmniDataGrid<object>.ExportVariants)] = AetheusGrid.ExportVariants,
            }, isDefault: true);
}
