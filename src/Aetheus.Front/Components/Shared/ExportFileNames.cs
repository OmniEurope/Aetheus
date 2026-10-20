// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;

namespace Aetheus.Front.Components.Shared;

/// <summary> Readable names for the files Aetheus exports. The owner's name first (the application
/// or the project, lowercase, without accents, words joined by hyphens), then what the file holds,
/// then the date: <c>aetheus-logs</c>, <c>aetheus-run-2478-findings-2026-10-01.md</c>. When the
/// name is unknown, a technical stand-in (<c>app-3</c>) keeps the file recognisable. </summary>
public static class ExportFileNames
{
    /// <summary>Longest slug kept from a name, so a long application name does not make a long file name.</summary>
    internal const int MaximumSlugLength = 40;

    /// <summary>
    /// The name without extension and without date, for the export bar of a grid: OmniEurope.Blazor
    /// (1.4.0) appends its own generation time (UTC, <c>-yyyy-MM-dd-HHmm</c>) and the extension, so
    /// <c>aetheus-logs</c> gives <c>aetheus-logs-2026-10-01-0840.md</c>; a date here would appear twice.
    /// </summary>
    public static string Stem(string? ownerName, string fallbackOwner, string content) =>
        string.Concat(Slug(ownerName) is { Length: > 0 } slug ? slug : fallbackOwner, "-", content);

    /// <summary>A complete file name: the stem, the local date of <paramref name="at"/>, the extension.</summary>
    public static string Dated(string? ownerName, string fallbackOwner, string content, DateTime at, string extension) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Stem(ownerName, fallbackOwner, content)}-{at:yyyy-MM-dd}.{extension.TrimStart('.')}");

    /// <summary>
    /// The name in lowercase ASCII letters and digits, accents removed, every other run of characters
    /// a single hyphen, no hyphen at either end. Empty when nothing usable is left.
    /// </summary>
    public static string Slug(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var slug = new StringBuilder(name.Length);
        var pendingHyphen = false;
        foreach (var character in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            var lower = char.ToLowerInvariant(character);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingHyphen && slug.Length > 0) slug.Append('-');
                pendingHyphen = false;
                slug.Append(lower);
            }
            else
            {
                pendingHyphen = true;
            }
        }
        return slug.Length <= MaximumSlugLength ? slug.ToString() : slug.ToString(0, MaximumSlugLength).TrimEnd('-');
    }
}
