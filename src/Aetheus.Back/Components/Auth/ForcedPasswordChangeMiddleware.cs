// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;

namespace Aetheus.Back.Components.Auth;

/// <summary>
/// Enforces a forced password change on the server. A token minted while the account must change its
/// password carries <see cref="AetheusClaimTypes.MustChangePassword"/>; until then only the requests the
/// change itself needs are served, everything else (hubs included) gets 403. The front used to be the
/// only gate, so the same token reached the whole API (audit SITE-SEC-001). The 403 carries
/// <see cref="RequiredHeader"/> so the front can tell it from an ordinary permission refusal.
/// </summary>
public sealed class ForcedPasswordChangeMiddleware(RequestDelegate next)
{
    public const string RequiredHeader = "X-Aetheus-Password-Change-Required";

    // Method + path, compared case-insensitively on the whole path.
    private static readonly (string Method, string Path)[] Allowed =
    [
        ("POST", "/api/users/me/change-password"),
        ("GET", "/api/users/me"),
        ("POST", "/api/auth/renew"),
        ("POST", "/api/auth/token/refresh"),
        ("POST", "/api/auth/session-ended"),
        // R1-37: logging out of the forced-change screen still forgets the held mailbox passwords.
        ("POST", "/api/auth/logout"),
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!MustChangePassword(context) || IsAllowed(context.Request))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers[RequiredHeader] = "true";
        await context.Response.WriteAsJsonAsync(
            new { error = "The password of this account must be changed before anything else." },
            context.RequestAborted).ConfigureAwait(false);
    }

    private static bool MustChangePassword(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
        && string.Equals(context.User.FindFirst(AetheusClaimTypes.MustChangePassword)?.Value, "true", StringComparison.Ordinal);

    internal static bool IsAllowed(HttpRequest request) =>
        Allowed.Any(entry =>
            string.Equals(entry.Method, request.Method, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Path, request.Path.Value?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
}
