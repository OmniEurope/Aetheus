// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Analysis;

/// <summary>The name of the branch a fix goes to, <c>cve/&lt;id&gt;-&lt;package&gt;</c>: an advisory and a package name are not refs, so
/// what a ref should not hold becomes a dash. Nothing else is ever written by this feature: the prefix is the guarantee.</summary>
internal static partial class CveRemediationBranch
{
    private const string Prefix = "cve/";
    private const int MaxSlugLength = 120;

    public static string For(string advisory, string package) => Prefix + Slug(advisory) + "-" + Slug(package);

    /// <summary>True only for a name <see cref="For"/> can produce, whatever a stored row says.</summary>
    public static bool IsOurs(string? branch) =>
        branch is not null && branch.StartsWith(Prefix, StringComparison.Ordinal) && Shape().IsMatch(branch[Prefix.Length..]);

    private static string Slug(string text)
    {
        var slug = Unsafe().Replace(text, "-").Trim('-');
        return slug.Length <= MaxSlugLength ? slug : slug[..MaxSlugLength].Trim('-');
    }

    [GeneratedRegex(@"[^A-Za-z0-9_\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Unsafe();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}
