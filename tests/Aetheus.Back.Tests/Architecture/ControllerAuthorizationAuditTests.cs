// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Back.Components.AppMonitoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// Architectural guard: every controller in the production assembly must require authentication
/// at the class level, except for a small explicit whitelist that performs its own scheme-specific
/// authentication or is gated per-endpoint.
///
/// Failing this test means a new controller was added without <c>[Authorize]</c> - either add the
/// attribute or, if the endpoint is intentionally anonymous, add the type to the whitelist below
/// with a comment explaining why.
/// </summary>
public class ControllerAuthorizationAuditTests
{
    /// <summary>
    /// Controllers exempt from the class-level <c>[Authorize]</c> rule. Each entry MUST be justified
    /// in the comment - the test enforces auth coverage, the whitelist documents the exceptions.
    /// </summary>
    private static readonly HashSet<string> AnonymousControllerWhitelist = new(StringComparer.Ordinal)
    {
        // Dev helpers - registered only in Development; each endpoint is individually guarded
        // ([AllowAnonymous] for capability probe, [Authorize(Roles="Admin")] for mutating actions),
        // and the controller itself returns 404 outside Development.
        "Aetheus.Back.Components.Dev.DevController",
        // Aetheus / OpenMetrics scrape endpoint at GET /metrics. Must be anonymous so that
        // the Aetheus server can scrape it without a JWT; it exposes only non-sensitive
        // process counters (background queue length / drops / processed).
        "Aetheus.Back.Components.Monitoring.MetricsController",
        // Git Smart HTTP: upload-pack (clone/fetch) is anonymous so pipeline checkouts work
        // without credentials; receive-pack (push) has [Authorize] per-endpoint.
        "Aetheus.Back.Components.Git.GitSmartHttpController",
        // OTLP ingestion (ADR-021 phase 2): authenticated by a per-app ingestion key in the
        // x-aetheus-ingest-key header (resolved to a MonitoredApp), not by a JWT - so a monitored
        // app can push telemetry without a user token. Rate-limited per key + tight body cap.
        "Aetheus.Back.Components.AppMonitoring.IngestController",
        // Browser analytics ingestion is intentionally anonymous. The public site id selects the
        // application, declared origins are checked, identifiers are HMAC-derived server-side, and
        // the route is protected by a dedicated per-site/per-address limiter and a 4 KiB body cap.
        "Aetheus.Back.Components.AppMonitoring.PublicWebAnalyticsController",
        // A CSP violation report is posted by the browser itself, with no session and no way to carry
        // one. It writes nothing and reads nothing: it only logs what the policy refused, bounded by a
        // dedicated per-address limiter and an 8 KiB body cap.
        "Aetheus.Back.Components.Security.CspReportController",
    };

    /// <summary>
    /// Individual actions marked <c>[AllowAnonymous]</c> on an otherwise <c>[Authorize]</c>'d controller.
    /// The class-level guard above can't see these - a new anonymous endpoint slipped onto a secured
    /// controller would otherwise be invisible. Each entry (keyed <c>{ControllerFullName}.{Method}</c>)
    /// MUST be justified: the test enforces that every anonymous action is a deliberate, reviewed choice.
    /// </summary>
    private static readonly HashSet<string> AnonymousActionWhitelist = new(StringComparer.Ordinal)
    {
        // Bootstrap auth endpoints - must be reachable without a JWT (they mint/refresh tokens or
        // enroll a new agent), and are rate-limited (login/external-login/webhook limiters).
        "Aetheus.Back.Components.Auth.AuthController.RegisterServer",
        "Aetheus.Back.Components.Auth.AuthController.Login",
        "Aetheus.Back.Components.Auth.AuthController.ExternalLogin",
        "Aetheus.Back.Components.Auth.AuthController.RefreshToken",
        // The post-session diagnostic endpoint accepts only bounded reason and correlation codes,
        // echoes nothing and shares the authentication rate limiter because no JWT remains.
        "Aetheus.Back.Components.Auth.AuthController.SessionEnded",
        // Public login-page metadata. Returns one boolean only, contains no secret or user data,
        // and must be available before authentication so credentials can be explained on the demo.
        "Aetheus.Back.Components.Auth.AuthController.GetPublicDemoInfo",
        // Inbound git webhook - authenticated by HMAC signature (WebhookSecret), not by a JWT.
        "Aetheus.Back.Components.Pipelines.PipelinesController.HandleWebhook",
        // Inbound release webhook - authenticated by a shared secret (Webhook:Secret), not by a JWT.
        "Aetheus.Back.Components.Releases.ReleasesController.Webhook",
        // NuGet v3 bootstrap document: clients must discover the authenticated protocol resources
        // before they can attach source credentials or an X-NuGet-ApiKey. It contains URLs only;
        // search, metadata, downloads and every write endpoint remain PAT-authenticated.
        "Aetheus.Back.Components.PackageRegistry.NuGetRegistryController.GetServiceIndex",
    };

    [Fact]
    public void Every_controller_must_have_authorize_or_be_whitelisted()
    {
        var controllers = typeof(Program).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract
                        && typeof(ControllerBase).IsAssignableFrom(t)
                        && t.GetCustomAttribute<ApiControllerAttribute>(inherit: true) is not null)
            .ToList();

        Assert.NotEmpty(controllers);

        var violations = new List<string>();
        foreach (var controller in controllers)
        {
            var fullName = controller.FullName ?? controller.Name;
            if (AnonymousControllerWhitelist.Contains(fullName))
                continue;

            var hasAuthorize = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Any();
            if (!hasAuthorize)
                violations.Add(fullName);
        }

        Assert.True(violations.Count == 0,
            "Controllers missing [Authorize] (add the attribute or whitelist with justification):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Every_anonymous_action_must_be_explicitly_allowlisted()
    {
        // G2: the class-level guard above is blind to a single action marked [AllowAnonymous] on an
        // otherwise-[Authorize]'d controller. Reflect over EVERY action method and require each
        // [AllowAnonymous] one to be a reviewed, justified entry in AnonymousActionWhitelist - so a
        // new anonymous endpoint can never slip onto a secured controller unnoticed.
        var controllers = typeof(Program).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract
                        && typeof(ControllerBase).IsAssignableFrom(t)
                        && t.GetCustomAttribute<ApiControllerAttribute>(inherit: true) is not null)
            .ToList();

        Assert.NotEmpty(controllers);

        var violations = new List<string>();
        foreach (var controller in controllers)
        {
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) is null)
                    continue;
                var key = $"{controller.FullName}.{method.Name}";
                if (!AnonymousActionWhitelist.Contains(key))
                    violations.Add(key);
            }
        }

        Assert.True(violations.Count == 0,
            "Actions marked [AllowAnonymous] that are not in AnonymousActionWhitelist "
            + "(confirm the endpoint is intentionally anonymous, then allowlist it with justification):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }
}
