// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Git;

/// <summary>
/// The branch of an automatic dependency update (PLAN-007), <c>aetheus/deps/&lt;package&gt;-&lt;version&gt;</c>. A package name and a
/// version come from a manifest and an advisory, so what a ref may not hold becomes a dash, and the shape a git ref forbids (a
/// component that starts or ends with a dot, two dots, a <c>.lock</c> end) is never produced. Lives in the Git module because the
/// pipelines read it too: a run on such a branch never chains into a deployment.
/// </summary>
public static partial class DependencyUpdateBranch
{
    public const string Prefix = "aetheus/deps/";
    private const int MaxSlugLength = 100;

    /// <summary>The branch for <paramref name="package"/> at <paramref name="version"/>, or null when either reduces to nothing.</summary>
    public static string? For(string package, string version)
    {
        var name = Slug(package);
        var target = Slug(version);
        return name.Length == 0 || target.Length == 0 ? null : Prefix + name + "-" + target;
    }

    /// <summary>True only for a name <see cref="For"/> can produce, whatever a stored row or a run says.</summary>
    public static bool IsOurs(string? branch) =>
        branch is not null
        && branch.StartsWith(Prefix, StringComparison.Ordinal)
        && Shape().IsMatch(branch[Prefix.Length..])
        && !branch.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);

    private static string Slug(string text)
    {
        var slug = Dots().Replace(Unsafe().Replace(text ?? string.Empty, "-"), ".").Trim('-', '.');
        if (slug.Length > MaxSlugLength) slug = slug[..MaxSlugLength].Trim('-', '.');
        while (slug.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) slug = slug[..^".lock".Length].Trim('-', '.');
        return slug;
    }

    [GeneratedRegex(@"[^A-Za-z0-9_.\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Unsafe();

    [GeneratedRegex(@"\.{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Dots();

    /// <summary>Dot-separated parts of plain characters: no empty part, so no leading, trailing or doubled dot.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}
