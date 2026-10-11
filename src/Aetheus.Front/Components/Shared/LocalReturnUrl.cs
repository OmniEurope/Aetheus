// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Recette R1-19 / R1-25: a page opened to fix something for another page (create an environment for
/// the pipeline setup wizard, add a server) brings the user back to it. The address travels as
/// <c>?returnUrl=</c> and is validated by OE's <see cref="OmniReturnUrl.IsLocal"/>, so only a path of
/// this application is ever followed: never an open redirect.
/// </summary>
public static class LocalReturnUrl
{
    public const string Parameter = "returnUrl";

    /// <summary>The address when it is a local path of the application; null otherwise.</summary>
    public static string? Validated(string? returnUrl) => OmniReturnUrl.IsLocal(returnUrl) ? returnUrl : null;

    /// <summary>
    /// <paramref name="path"/> carrying <paramref name="returnUrl"/> in its query when it is local; the
    /// bare path otherwise. <paramref name="path"/> may already hold a query.
    /// </summary>
    public static string Append(string path, string? returnUrl)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Validated(returnUrl) is not { } local) return path;
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{path}{separator}{Parameter}={Uri.EscapeDataString(local)}";
    }

    /// <summary>The page the user is on, as a base-relative local path with its query.</summary>
    public static string Current(NavigationManager nav)
    {
        ArgumentNullException.ThrowIfNull(nav);
        return "/" + nav.ToBaseRelativePath(nav.Uri);
    }
}
