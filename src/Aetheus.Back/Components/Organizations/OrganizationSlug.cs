// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;
namespace Aetheus.Back.Components.Organizations;

/// <summary>Derives an organization slug from its name when the user leaves the slug empty.</summary>
internal static class OrganizationSlug
{
    // Leaves room for a uniqueness suffix under the 100-character column.
    private const int MaxLength = 90;

    internal static string FromName(string name)
    {
        var sb = new StringBuilder(name.Length);
        // Accented letters keep their base letter ("Équipe" gives "equipe", not "quipe").
        var decomposed = name.Trim().ToLowerInvariant()
            .Replace("œ", "oe", StringComparison.Ordinal).Replace("æ", "ae", StringComparison.Ordinal)
            .Replace("ß", "ss", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
                sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > MaxLength) slug = slug[..MaxLength].TrimEnd('-');
        return slug.Length == 0 ? "organization" : slug;
    }
}
