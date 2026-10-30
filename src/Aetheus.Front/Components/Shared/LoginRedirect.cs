// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

/// <summary>Preserve the requested local route, query and fragment through sign-in and forced
/// password change so users return to their destination; centralize returnUrl construction and
/// validation to prevent open redirects.</summary>
public static class LoginRedirect
{
    public const string LoginPath = "/login";
    public const string ChangePasswordPath = "/account/change-password";
    private const string Parameter = "returnUrl";

    /// <summary>
    /// Recette R-24: the query parameter of a link that acts on opening (an approval mail's Approve or Reject).
    /// It never survives a sign-in: a link opened while signed out leads to the page without its action, so
    /// signing in decides nothing, and a crafted <c>/login?returnUrl=...&amp;action=approve</c> acts on no one.
    /// </summary>
    public const string OneShotActionParameter = "action";

    /// <summary>The login URL that brings the user back to where they are now.</summary>
    public static string ToLogin(NavigationManager nav) =>
        WithReturnUrl(LoginPath, Current(nav));

    /// <summary>The forced password change, bringing the user back to where they are now.</summary>
    public static string ToChangePassword(NavigationManager nav) =>
        WithReturnUrl(ChangePasswordPath, Current(nav));

    /// <summary>
    /// <paramref name="path"/> with <paramref name="returnUrl"/> attached, once validated; the bare
    /// path when there is nothing safe worth coming back to.
    /// </summary>
    public static string WithReturnUrl(string path, string? returnUrl) =>
        IsSafe(returnUrl) && !IsAuthPage(returnUrl!)
            ? $"{path}?{Parameter}={Uri.EscapeDataString(WithoutOneShotAction(returnUrl!))}"
            : path;

    /// <summary>Where to go after signing in: the requested page when it is safe, else the dashboard; never with
    /// its <see cref="OneShotActionParameter"/>.</summary>
    public static string Resolve(string? returnUrl) =>
        IsSafe(returnUrl) && !IsAuthPage(returnUrl!) ? WithoutOneShotAction(returnUrl!) : "/";

    /// <summary><paramref name="url"/> with every <see cref="OneShotActionParameter"/> of its query removed, the
    /// rest of the query and the fragment kept as written.</summary>
    internal static string WithoutOneShotAction(string url)
    {
        var hashAt = url.IndexOf('#', StringComparison.Ordinal);
        var fragment = hashAt >= 0 ? url[hashAt..] : string.Empty;
        var beforeFragment = hashAt >= 0 ? url[..hashAt] : url;
        var queryAt = beforeFragment.IndexOf('?', StringComparison.Ordinal);
        if (queryAt < 0) return url;
        var kept = beforeFragment[(queryAt + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !string.Equals(Uri.UnescapeDataString(pair.Split('=')[0]), OneShotActionParameter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var path = beforeFragment[..queryAt];
        return (kept.Count == 0 ? path : path + "?" + string.Join('&', kept)) + fragment;
    }

    /// <summary>
    /// Only a path of this application: it starts with a single <c>/</c>, names no scheme or host
    /// (<c>//evil</c>, <c>/\evil</c>, <c>https://evil</c>), and holds no backslash or control
    /// character a browser could normalize into one.
    /// </summary>
    internal static bool IsSafe(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || returnUrl.Length > 2048) return false;
        if (returnUrl[0] != '/' || returnUrl.StartsWith("//", StringComparison.Ordinal)) return false;
        if (returnUrl.Contains('\\') || returnUrl.Any(char.IsControl)) return false;
        // A path never needs a scheme; "/redirect?to=https://x" is fine, "/https://x" is not a scheme.
        var pathPart = returnUrl.Split('?', '#')[0];
        return !pathPart.Contains("://", StringComparison.Ordinal);
    }

    private static string Current(NavigationManager nav) => "/" + nav.ToBaseRelativePath(nav.Uri);

    private static bool IsAuthPage(string returnUrl)
    {
        var path = returnUrl.Split('?', '#')[0].TrimEnd('/');
        return path.Length == 0
            || path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase)
            || path.Equals(ChangePasswordPath, StringComparison.OrdinalIgnoreCase);
    }
}
