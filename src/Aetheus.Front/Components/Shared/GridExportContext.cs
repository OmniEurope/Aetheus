// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>Derive export context from the current breadcrumb, table name and page address so files
/// identify their source; reread it on render to include names loaded asynchronously.</summary>
public sealed class GridExportContext(
    BreadcrumbService breadcrumb,
    NavigationManager navigation,
    IStringLocalizer<AppStrings> localizer)
{
    /// <summary>The product the rows come from, written as is in every language.</summary>
    internal const string Source = "Aetheus";

    /// <summary>The breadcrumb of the page, then <paramref name="tableName"/> when given.</summary>
    public string Title(string? tableName = null)
    {
        var parts = breadcrumb.Items
            .Select(item => item.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        if (!string.IsNullOrWhiteSpace(tableName)) parts.Add(tableName);
        return parts.Count > 0 ? string.Join(" > ", parts) : Text("GridExportUntitled");
    }

    /// <summary>Derive a slug from the breadcrumb below the module and the table name so exported
    /// files remain identifiable; leave date and extension to OE and return null when no specific
    /// name exists.</summary>
    public string? FileName(string? tableName = null)
    {
        var parts = breadcrumb.Items
            .Select(item => item.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        if (parts.Count > 1) parts.RemoveAt(0);
        if (!string.IsNullOrWhiteSpace(tableName)) parts.Add(tableName);
        var slug = ExportFileNames.Slug(string.Join(' ', parts));
        return slug.Length > 0 ? slug : null;
    }

    /// <summary>The application and the address of the page, path and query (the open tab, say).</summary>
    public IReadOnlyList<OmniTableExportField> Fields() =>
    [
        new(Text("GridExportSource"), Source),
        new(Text("GridExportAddress"), "/" + navigation.ToBaseRelativePath(navigation.Uri))
    ];

    /// <summary>The text of a key; the key itself when the localizer has none (a test double, say).</summary>
    private string Text(string key) => (string?)localizer[key] ?? key;
}
