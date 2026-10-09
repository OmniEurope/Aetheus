// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Recette R2-124 / R-473: where the rows of an exported table come from, said at the head of the file.
/// The title is the page's breadcrumb (<c>Projets &gt; Aetheus &gt; Qualité</c>), followed by the name of
/// the table when the page holds several; the header lines name the application and the address of the
/// page. Read each time the grid renders, so the names the page loaded after its first render are in it.
/// </summary>
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

    /// <summary>
    /// The file name without date nor extension (OE appends both, recette R2-036): the breadcrumb past its
    /// first item, the module every page of it shares, then <paramref name="tableName"/>, as
    /// <see cref="ExportFileNames.Slug"/> writes it (<c>aetheus-qualite-findings</c>). Null when nothing is
    /// left, so OE derives the name from the title.
    /// </summary>
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
