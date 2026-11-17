// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.E2E;

/// <summary>
/// Single source of truth for the E2E harness configuration: the frontend/backend
/// URLs, the bootstrap admin credentials, and the app-ready timeout. Each value is
/// sourced from an environment variable with the historical inline default as the
/// fallback, so the suite keeps working unchanged on the standard dev ports while a
/// port-overridden worktree (.ylaunch.local) can point it at a non-default port
/// triplet. ylaunch's E2E path exports E2E_FRONTEND_URL / E2E_BACKEND_URL to the
/// actual ports in use.
/// </summary>
internal static class PlaywrightConfig
{
    public static readonly string FrontendUrl =
        Environment.GetEnvironmentVariable("E2E_FRONTEND_URL") ?? "https://localhost:5401";

    public static readonly string BackendUrl =
        Environment.GetEnvironmentVariable("E2E_BACKEND_URL") ?? "https://localhost:5301";

    /// <summary>
    /// Launch options that give the browser a secure context on <paramref name="frontendUrl"/>. The .NET 11
    /// WebAssembly runtime refuses to start without <c>crypto.subtle</c> ("This engine doesn't support
    /// crypto.subtle"), and a browser only offers it to HTTPS and loopback origins. The QA stack, the
    /// launcher's E2E stack and the pipeline rehearsal reach the front over plain HTTP on a container or
    /// <c>host.docker.internal</c> name, so for such an origin Chromium is told to treat exactly that origin as
    /// secure. The flag is ignored by Playwright's default headless shell (proved in the QA image: isSecureContext
    /// stays false), which is why the full Chromium in its new headless mode is selected as well. The origin
    /// itself is not changed, so the backend's CORS and the front's API base address stay valid. HTTPS and
    /// loopback origins (a developer's own browser run) keep the launch options untouched.
    /// </summary>
    public static BrowserTypeLaunchOptions WithSecureFrontendOrigin(BrowserTypeLaunchOptions? options, string frontendUrl)
    {
        var launch = options ?? new BrowserTypeLaunchOptions();
        var origin = new Uri(frontendUrl);
        if (origin.Scheme != Uri.UriSchemeHttp
            || origin.IsLoopback
            // A *.localhost name (its last DNS label is localhost) is already a secure context.
            || string.Equals(origin.Host.Split('.')[^1], "localhost", StringComparison.OrdinalIgnoreCase))
            return launch;

        launch.Channel = "chromium";
        launch.Args = [.. launch.Args ?? [], $"--unsafely-treat-insecure-origin-as-secure={origin.GetLeftPart(UriPartial.Authority)}"];
        return launch;
    }

    /// <summary>The seeded admin's username: a display name used to find its row in lists, never a
    /// sign-in (owner decision 2026-10-28; <see cref="AdminEmail"/> signs in).</summary>
    public static readonly string AdminUser =
        Environment.GetEnvironmentVariable("E2E_ADMIN_USER") ?? "admin";

    /// <summary>The seeded admin's sign-in e-mail (PLAN-003 P3-2): Auth:AdminEmail of the backend, whose
    /// Development configuration and seed default are both this address.</summary>
    public static readonly string AdminEmail =
        Environment.GetEnvironmentVariable("E2E_ADMIN_EMAIL") ?? "admin@aetheus.local";

    public static readonly string AdminPassword =
        Environment.GetEnvironmentVariable("E2E_ADMIN_PASSWORD") ?? "aetheus-dev-admin-pwd";

    // The QA container and production static host must emit CSP. ylaunch deliberately uses the
    // Blazor development server, which cannot reproduce headers owned by StaticServer.Program.cs;
    // it opts out explicitly instead of silently weakening the production default.
    public static readonly bool RequireFrontendSecurityHeaders =
        !bool.TryParse(
            Environment.GetEnvironmentVariable("E2E_REQUIRE_FRONTEND_SECURITY_HEADERS"),
            out var requireFrontendSecurityHeaders)
        || requireFrontendSecurityHeaders;

    /// <summary>
    /// Generous default for the Blazor WASM cold boot. The dev Kestrel + WASM runtime
    /// can take several seconds to JIT and render the first protected route after a
    /// database reset, so navigation/auth waits use this budget. Overridable via
    /// E2E_APP_READY_TIMEOUT_MS for slower CI hosts.
    /// </summary>
    public static readonly int AppReadyTimeoutMs =
        int.TryParse(Environment.GetEnvironmentVariable("E2E_APP_READY_TIMEOUT_MS"), out var ms) && ms > 0
            ? ms
            : 30000;

    /// <summary>
    /// Stable, framework-agnostic readiness hook. MainLayout puts
    /// <c>data-testid="blazor-ready"</c> on the top-level app container that only
    /// renders once the boot gate (auth/orgs/permissions) has resolved, so waiting on
    /// it is a deterministic "the chrome is up" signal that survives component class churn.
    /// </summary>
    public const string BlazorReadyTestId = "blazor-ready";
}
