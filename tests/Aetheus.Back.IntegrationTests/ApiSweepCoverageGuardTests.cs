// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Back.Components.AppMonitoring;
using Microsoft.AspNetCore.Mvc;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Anti-drift guard for the hand-written <see cref="ApiAuthorizationSmokeTests"/> /
/// <see cref="ApiReadSmokeTests"/> route lists (S-TECH-RTDR). Those sweeps enumerate one route
/// per controller as <c>[InlineData]</c>, but nothing tied those lists to the real set of
/// controllers - so a controller added in three months would be covered by NO sweep without
/// failing anything, and the two lists could silently diverge (as they already had:
/// <c>/api/work-items</c> was in the auth sweep but dropped from the read sweep).
/// <para>
/// This guard reflects over the real <see cref="ControllerBase"/> set (exactly like
/// <c>ControllerAuthorizationAuditTests</c>) and reads the <c>[InlineData]</c> literals back out
/// of both sweeps, then fails if:
/// (1) a controller exposing a GET action is not covered by the anonymous-401 auth sweep; or
/// (2) a controller covered by the auth sweep is not also covered by the admin read sweep,
/// unless it is on a documented exemption list. Pure reflection - no container, no host boot.
/// </para>
/// </summary>
public sealed class ApiSweepCoverageGuardTests
{
    /// <summary>
    /// Controllers intentionally excluded from the anonymous-GET auth sweep. Each MUST be justified.
    /// (Controllers with no GET action - e.g. CronController - are skipped automatically.)
    /// </summary>
    private static readonly HashSet<string> AuthSweepExemptControllers = new(StringComparer.Ordinal)
    {
        // Development-only: anonymous capability probe + admin-gated mutations; 404 outside Development.
        "Aetheus.Back.Components.Dev.DevController",
        // Git Smart HTTP transport: upload-pack (clone/fetch) is anonymous by design; per-endpoint auth.
        "Aetheus.Back.Components.Git.GitSmartHttpController",
    };

    /// <summary>
    /// Controllers covered by the auth sweep but legitimately absent from the PARAMETRIZED read
    /// sweep - matched by EXACT normalized route prefix (not a broad <c>StartsWith</c> family, so a
    /// new server-scoped controller is NOT silently auto-exempted: it fails the cross-check until it
    /// is read-tested or explicitly added here). Each is read-tested by a dedicated, non-literal
    /// case (server-scoped <c>[Theory]</c> interpolating a seeded id, invisible to literal
    /// reflection) or is genuinely environment-dependent.
    /// </summary>
    private static readonly (string Prefix, string Reason)[] ReadSweepExemptPrefixes =
    [
        ("api/servers/#/certbot", "read-tested via ServerScopedGet_AsAdmin (interpolated route)"),
        ("api/servers/#/docker", "read-tested via ServerScopedGet_AsAdmin (interpolated route)"),
        ("api/servers/#/apache", "read-tested via ServerScopedGet_AsAdmin (interpolated route)"),
        ("api/servers/#/mail", "read-tested via ServerScopedGet_AsAdmin (interpolated route)"),
        ("api/servers/#/portsentry", "read-tested via ServerScopedGet_AsAdmin (interpolated route)"),
        ("api/servers/#/rkhunter", "read-tested via ServerScopedGet_AsAdmin (interpolated route)"),
        ("api/servers/#/teamspeak", "read-tested via ServerScopedGet_AsAdmin (interpolated route)"),
        ("api/servers/#/modules", "read-tested via ServerScopedGet_AsAdmin (interpolated route)"),
        ("api/servers/#/apps", "read-tested via ServerScopedGet_AsAdmin (interpolated route)"),
        ("api/servers/#/module-links", "read-tested via ServerScopedGet_AsAdmin (interpolated route)"),
        ("api/servers/#/configuration", "GET export returns 404 without a seeded config - not a success-read target"),
        ("api/pipelines/runs", "read-tested via CheckpointResumePreview_AsAdmin with an interpolated seeded run id"),
        ("api/pipelines/#/runs/#/stage-baselines",
            "read-tested via RunStageBaselines_AsAdmin with the seeded pipeline and run ids"),
        ("api/agent", "AgentInstaller: GET returns an install script, not a JSON collection"),
        ("api/external-repos", "ExternalRepos: gated by Features:ExternalRepos (off in test env) - 404 by design"),
        ("api/logs", "Logs: GET requires a seeded task id with on-disk logs"),
        ("api/variable-libraries/#/ports",
            "read-tested via VariableLibraryPortTargets_AsAdmin, which creates the library the route needs"),
        ("api/packages/nuget", "NuGet protocol reads require a PAT credential, not the admin JWT used by the generic read sweep"),
        ("api/packages/npm", "npm protocol reads require a PAT credential, not the admin JWT used by the generic read sweep"),
    ];

    /// <summary>
    /// Controllers exposing a mutation (POST/PUT/DELETE/PATCH) action but legitimately absent from
    /// the anonymous-mutation auth sweep - matched by EXACT controller full name. The
    /// anonymous-rejection guarantee for these mutations is covered another way, or the route is
    /// environment-gated. Each MUST be justified.
    /// </summary>
    private static readonly Dictionary<string, string> MutationSweepExemptControllers = new(StringComparer.Ordinal)
    {
        // Development-only: anonymous capability probe + admin-gated mutations; 404 outside Development.
        ["Aetheus.Back.Components.Dev.DevController"] =
            "Development-only controller, 404 outside Development - exempt from the GET sweep for the same reason",
        // OTLP ingest is [AllowAnonymous] by design: external apps push telemetry authenticated by a
        // per-app ingest key carried in the request, not by a JWT - an anonymous-401 sweep does not apply.
        ["Aetheus.Back.Components.AppMonitoring.IngestController"] =
            "OTLP ingest endpoint is [AllowAnonymous] (per-app ingest-key auth, not JWT) - no 401 to assert",
        ["Aetheus.Back.Components.AppMonitoring.PublicWebAnalyticsController"] =
            "Public browser analytics is anonymous by design and gated by declared origin, rate limit, body cap, and server-side pseudonymization",
        ["Aetheus.Back.Components.Security.CspReportController"] =
            "CSP violation reports are posted by the browser itself, with no session: [AllowAnonymous] by design, bounded by its own rate-limit partition, an 8 KiB body cap and 8 reports per batch",
    };

    /// <summary>
    /// Per-verb facet on the anonymous-401 sweep. The GET facet
    /// (<see cref="Every_get_controller_is_covered_by_the_auth_sweep"/>) structurally cannot see a
    /// controller that exposes ONLY mutations (POST/PUT/DELETE/PATCH) and no GET - such a controller
    /// would be invisible to every anonymous-rejection guarantee without failing anything (the exact
    /// CronController blind spot the sweep comment calls out). This facet closes that gap: any
    /// mutation-bearing controller with no GET action must have at least one of its mutation routes
    /// exercised by the anonymous-mutation <c>[InlineData]</c> set, so its <c>[Authorize]</c> wiring
    /// is proven end-to-end on a real write path. (Controllers that also expose a GET are already
    /// anonymously-401 proven by the GET facet, so they are covered there.)
    /// </summary>
    [Fact]
    public void Every_mutation_only_controller_has_a_mutation_covered_by_the_auth_sweep()
    {
        var mutationRoutes = MutationLiterals(typeof(ApiAuthorizationSmokeTests));
        Assert.NotEmpty(mutationRoutes);

        var allPrefixes = AllControllerPrefixes();
        var uncovered = ApiControllers()
            .Where(c => HasMutationAction(c) && !HasGetAction(c)
                        && !MutationSweepExemptControllers.ContainsKey(c.FullName!))
            .Where(c => !IsCovered(RoutePrefix(c), mutationRoutes, allPrefixes))
            .Select(c => $"{c.FullName} (route '{RouteTemplate(c)}')")
            .ToList();

        Assert.True(uncovered.Count == 0,
            "Mutation-only controllers (POST/PUT/DELETE/PATCH, no GET) whose mutation surface is not covered "
            + "by any ApiAuthorizationSmokeTests anonymous-mutation [InlineData] - they would otherwise be "
            + "invisible to every anonymous-rejection sweep (add a representative mutation route to the auth "
            + "sweep, or exempt with justification):"
            + Environment.NewLine + string.Join(Environment.NewLine, uncovered));
    }

    [Fact]
    public void Every_get_controller_is_covered_by_the_auth_sweep()
    {
        var authRoutes = NormalizedLiterals(typeof(ApiAuthorizationSmokeTests));
        Assert.NotEmpty(authRoutes);

        var allPrefixes = AllControllerPrefixes();
        var uncovered = ApiControllers()
            .Where(c => HasGetAction(c) && !AuthSweepExemptControllers.Contains(c.FullName!))
            .Where(c => !IsCovered(RoutePrefix(c), authRoutes, allPrefixes))
            .Select(c => $"{c.FullName} (route '{RouteTemplate(c)}')")
            .ToList();

        Assert.True(uncovered.Count == 0,
            "Controllers with a GET action not covered by any ApiAuthorizationSmokeTests [InlineData] "
            + "(add a representative route to the auth sweep, or exempt with justification):"
            + Environment.NewLine + string.Join(Environment.NewLine, uncovered));
    }

    [Fact]
    public void Every_auth_swept_controller_is_also_read_swept()
    {
        var authRoutes = NormalizedLiterals(typeof(ApiAuthorizationSmokeTests));
        var readRoutes = NormalizedLiterals(typeof(ApiReadSmokeTests));
        Assert.NotEmpty(readRoutes);

        var allPrefixes = AllControllerPrefixes();
        var drifted = ApiControllers()
            .Where(c => HasGetAction(c) && !AuthSweepExemptControllers.Contains(c.FullName!))
            .Where(c => IsCovered(RoutePrefix(c), authRoutes, allPrefixes))
            .Where(c => !IsCovered(RoutePrefix(c), readRoutes, allPrefixes) && !IsReadExempt(RoutePrefix(c)))
            .Select(c => $"{c.FullName} (route '{RouteTemplate(c)}')")
            .ToList();

        Assert.True(drifted.Count == 0,
            "Controllers in the auth sweep but missing from the admin read sweep - read drift "
            + "(add the route to ApiReadSmokeTests, or document a ReadSweepExemptPrefixes entry):"
            + Environment.NewLine + string.Join(Environment.NewLine, drifted));
    }

    private static List<Type> ApiControllers() =>
        typeof(Program).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract
                        && typeof(ControllerBase).IsAssignableFrom(t)
                        && t.GetCustomAttribute<ApiControllerAttribute>(inherit: true) is not null)
            .ToList();

    private static bool HasGetAction(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(m => m.GetCustomAttributes<HttpGetAttribute>(inherit: true).Any());

    private static bool HasMutationAction(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(m => m.GetCustomAttributes<HttpPostAttribute>(inherit: true).Any()
                   || m.GetCustomAttributes<HttpPutAttribute>(inherit: true).Any()
                   || m.GetCustomAttributes<HttpDeleteAttribute>(inherit: true).Any()
                   || m.GetCustomAttributes<HttpPatchAttribute>(inherit: true).Any());

    private static string RouteTemplate(Type controller)
    {
        var template = controller.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty;
        var shortName = controller.Name.EndsWith("Controller", StringComparison.Ordinal)
            ? controller.Name[..^"Controller".Length]
            : controller.Name;
        return template.Replace("[controller]", shortName, StringComparison.OrdinalIgnoreCase);
    }

    private static string RoutePrefix(Type controller) => Normalize(RouteTemplate(controller));

    private static IReadOnlyList<string> AllControllerPrefixes() =>
        ApiControllers().Select(RoutePrefix).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// A controller is covered only if some literal sits under its prefix AND that prefix is the
    /// LONGEST controller prefix the literal matches - i.e. the literal genuinely targets THIS
    /// controller, not a more deeply-nested sibling. Without the longest-owner rule,
    /// <c>GitController</c> ("api/git") would be falsely "covered" by a <c>/api/git/repos</c>
    /// literal that actually belongs to <c>GitLightController</c>.
    /// </summary>
    private static bool IsCovered(string prefix, IReadOnlyCollection<string> literals, IReadOnlyList<string> allPrefixes)
    {
        if (prefix.Length == 0)
            return false;

        foreach (var literal in literals)
        {
            if (literal != prefix && !literal.StartsWith(prefix + "/", StringComparison.Ordinal))
                continue;

            var deeperOwnerExists = allPrefixes.Any(p =>
                p.Length > prefix.Length
                && (literal == p || literal.StartsWith(p + "/", StringComparison.Ordinal)));

            if (!deeperOwnerExists)
                return true;
        }

        return false;
    }

    private static bool IsReadExempt(string prefix) =>
        ReadSweepExemptPrefixes.Any(e => prefix == e.Prefix);

    /// <summary>Pulls every route-shaped string out of a sweep's <c>[InlineData]</c> attributes and normalizes it.</summary>
    private static List<string> NormalizedLiterals(Type sweepType)
    {
        var literals = new List<string>();
        foreach (var method in sweepType.GetMethods())
            foreach (var cad in method.GetCustomAttributesData())
                if (cad.AttributeType.Name == "InlineDataAttribute")
                    foreach (var arg in cad.ConstructorArguments)
                        CollectStrings(arg, literals);

        return literals
            .Where(s => s.StartsWith('/'))
            .Select(Normalize)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Pulls the route literals out of a sweep's anonymous-MUTATION <c>[InlineData(method, route)]</c>
    /// attributes only - an InlineData is treated as a mutation case when one of its string args is an
    /// HTTP mutation verb. This deliberately ignores the GET-only sweep so the mutation facet proves a
    /// mutation route (not merely the controller's GET) is exercised by the anonymous-401 sweep.
    /// </summary>
    private static List<string> MutationLiterals(Type sweepType)
    {
        var mutationVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "DELETE", "PATCH" };
        var literals = new List<string>();

        foreach (var method in sweepType.GetMethods())
            foreach (var cad in method.GetCustomAttributesData())
            {
                if (cad.AttributeType.Name != "InlineDataAttribute")
                    continue;

                var args = new List<string>();
                foreach (var arg in cad.ConstructorArguments)
                    CollectStrings(arg, args);

                if (args.Any(a => mutationVerbs.Contains(a)))
                    literals.AddRange(args);
            }

        return literals
            .Where(s => s.StartsWith('/'))
            .Select(Normalize)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static void CollectStrings(CustomAttributeTypedArgument arg, List<string> sink)
    {
        if (arg.Value is string s)
            sink.Add(s);
        else if (arg.Value is IReadOnlyCollection<CustomAttributeTypedArgument> nested)
            foreach (var inner in nested)
                CollectStrings(inner, sink);
    }

    /// <summary>
    /// Collapses a route to comparable segments: query string dropped, numeric ids and
    /// <c>{param:constraint}</c> placeholders both folded to <c>#</c>, everything lower-cased.
    /// </summary>
    private static string Normalize(string path)
    {
        var withoutQuery = path.Split('?', 2)[0];
        var segments = withoutQuery.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(seg =>
                seg.StartsWith('{') ? "#"
                : seg.All(char.IsDigit) ? "#"
                : seg.ToLowerInvariant());
        return string.Join('/', segments);
    }
}
