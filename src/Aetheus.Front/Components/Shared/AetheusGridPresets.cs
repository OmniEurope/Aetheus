// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>Register shared grid settings once through the OmniEurope preset so tables inherit a
/// consistent baseline; keep virtualization per grid because it requires bounded layout and a
/// runtime switch.</summary>
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
                // Enable column-edge double-click autofit so users can size columns to their
                // content.
                [nameof(OmniDataGrid<object>.AllowColumnAutoFit)] = true,
                // R-69: size every column to its content so a narrower page tightens the columns
                // without ever cutting a value; a declared Width is ignored, MinWidth stays a floor.
                [nameof(OmniDataGrid<object>.ColumnSizing)] = OmniDataGridColumnSizing.Content,
                [nameof(OmniDataGrid<object>.AllowAlternatingRows)] = true,
                [nameof(OmniDataGrid<object>.HighlightRowOnHover)] = true,
                [nameof(OmniDataGrid<object>.Density)] = OmniDensity.Compact,
                // Keep the footer hidden by default and offer Markdown only, with actions at the
                // footer's end; raw grids export all Items, while ExportMarkdown=false forbids
                // export under GridExportCoverageAuditTests.
                [nameof(OmniDataGrid<object>.FooterBarExport)] = OmniDataGridBarExport.End,
                [nameof(OmniDataGrid<object>.ExportCsv)] = false,
                [nameof(OmniDataGrid<object>.ExportExcel)] = false,
                [nameof(OmniDataGrid<object>.ExportPdf)] = false,
                [nameof(OmniDataGrid<object>.ExportVariants)] = AetheusGrid.ExportVariants,
            }, isDefault: true);
}
