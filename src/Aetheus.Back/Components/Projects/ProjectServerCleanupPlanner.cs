// SPDX-License-Identifier: EUPL-1.2
using static Aetheus.Back.Components.Projects.ProjectServerCleanupLibraryValues;

namespace Aetheus.Back.Components.Projects;

/// <summary>
/// Recette R-97 (ADR-058): reads, from the project's variable libraries, what each environment deployed and
/// where. An environment library is one the environment owns, or a project library named
/// <c>&lt;slug&gt;.&lt;environment&gt;</c> (the wizard's <c>&lt;slug&gt;.prod</c>, <c>&lt;slug&gt;.qa</c>); <c>&lt;slug&gt;.share</c>
/// supplies the defaults under each. Its <c>COMPOSE_PROJECT</c> (containers, volumes, networks and the images it
/// built), <c>STATE_DIR</c>, <c>UPSTREAM_CONF</c> (the blue-green upstream file) and hosts (<c>APP_HOST</c> and
/// <c>API_HOST</c>, or the template's formula <c>$(HOST_PREFIX)$(APP_SUBDOMAIN).$(DOMAIN)</c>) name the resources;
/// the environment's servers say where. A production library (<c>&lt;slug&gt;.prod</c>, <c>&lt;slug&gt;.production</c>)
/// with no environment, or one without servers, as the setup wizard leaves it (it creates no environment, it links the
/// chosen server to the project and writes it as the deployment's <c>deployServer</c> default, recette R-77, R-21), is
/// located on the project's server its deployment pipelines name in <c>deployServer</c>, else on every server linked to
/// the project; a name the project has no server for is never looked up elsewhere. Another library without an
/// environment server (the QA's disposable stack runs on the CI runner and its own run destroys it) is located
/// nowhere. A value that is not a strict name, a state directory outside
/// <c>/var/lib/aetheus-agent/projects/&lt;slug&gt;[-...]</c> (or <c>/var/lib/&lt;slug&gt;[-...]</c> before R-97), a value another project's library also names, or an environment without a
/// server is refused and listed, never used. Servers of another organization are never touched, and a server
/// the current user may not write to is listed as such and never asked. Nothing in the control plane namespace
/// (<c>aetheus</c>, <c>aetheus-...</c>) and no public host of the control plane is ever used, even for a project
/// whose slug is <c>aetheus</c>.
/// </summary>
internal static class ProjectServerCleanupPlanner
{
    private static readonly string[] s_productionNames = ["prod", "production"];

    public static ProjectServerCleanupPlan Build(ProjectServerCleanupInputs inputs, ProjectServerCleanupGuards guards)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(guards);
        var slug = ProjectResourceNames.Slug(inputs.ProjectName, inputs.ProjectId);
        var share = inputs.Libraries.FirstOrDefault(library => IsProjectLibrary(library)
            && string.Equals(library.Name, $"{slug}.share", StringComparison.OrdinalIgnoreCase));
        var refused = new List<ProjectDeletionRefusedValueDto>();
        var foreign = ProjectServerCleanupForeignIdentity.From(inputs.OtherProjects);
        var environments = new Dictionary<int, (CleanupServerInfo Server, List<ProjectDeletionEnvironmentDto> Items)>();
        var backups = new Dictionary<int, (CleanupServerInfo Server, List<int> Ids)>();

        foreach (var library in inputs.Libraries.Where(library => library != share))
        {
            var (name, servers) = Locate(library, slug, inputs);
            if (name is null) continue;
            var values = Merge(share?.Entries, library.Entries);
            var identity = Resolve(name, values, slug, foreign, guards.ControlPlaneHosts, refused);
            if (identity is null) continue;
            var reachable = servers.Where(server => Owned(server, inputs.OrganizationId)).ToList();
            if (reachable.Count == 0)
            {
                refused.AddRange(Values(identity).Select(value =>
                    new ProjectDeletionRefusedValueDto(name, value.Key, value.Value, ProjectDeletionRefusal.NoServer)));
                continue;
            }
            foreach (var server in reachable)
            {
                if (!environments.TryGetValue(server.Id, out var entry))
                    environments[server.Id] = entry = (server, []);
                if (!entry.Items.Contains(identity)) entry.Items.Add(identity);
            }
        }

        foreach (var policy in inputs.BackupPolicies.Where(policy => Owned(policy.Server, inputs.OrganizationId)))
        {
            if (!backups.TryGetValue(policy.Server.Id, out var entry))
                backups[policy.Server.Id] = entry = (policy.Server, []);
            entry.Ids.Add(policy.Id);
        }

        var targets = environments.Keys.Union(backups.Keys).Order()
            .Select(id =>
            {
                var server = environments.TryGetValue(id, out var env) ? env.Server : backups[id].Server;
                var items = environments.TryGetValue(id, out var found) ? found.Items : [];
                var state = StateOf(server, guards.Writable);
                return new ProjectServerCleanupTarget(server.Id, server.Name, state, items,
                    backups.TryGetValue(id, out var policies) ? policies.Ids : [])
                {
                    Slug = slug,
                    // Nothing is asked of a server the user may not write to, so no capability is missing there.
                    MissingCapabilities = state == ProjectDeletionServerState.NoWriteAccess ? [] : MissingCapabilities(server, items),
                };
            })
            .ToList();
        return new ProjectServerCleanupPlan(inputs.ProjectId, inputs.ProjectName, inputs.OrganizationId, targets, refused);
    }

    private static (string? Name, IReadOnlyList<CleanupServerInfo> Servers) Locate(
        CleanupLibrary library, string slug, ProjectServerCleanupInputs inputs)
    {
        if (library.EnvironmentId is { } environmentId)
        {
            var environment = inputs.Environments.FirstOrDefault(item => item.Id == environmentId);
            return environment is null ? (null, []) : (environment.Name, environment.Servers);
        }
        if (library.ProjectServerId is { } projectServerId)
        {
            var projectServer = inputs.ProjectServers.FirstOrDefault(item => item.Id == projectServerId);
            return projectServer is null ? (null, []) : (projectServer.DisplayName, projectServer.Server is null ? [] : [projectServer.Server]);
        }
        var prefix = slug + ".";
        if (!library.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || library.Name.Length == prefix.Length)
            return (null, []);
        var suffix = library.Name[prefix.Length..];
        var production = s_productionNames.Contains(suffix, StringComparer.OrdinalIgnoreCase);
        var names = production ? s_productionNames : [suffix];
        var match = names
            .Select(name => inputs.Environments.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(item => item is not null);
        var servers = match?.Servers ?? [];
        // The wizard creates no environment (recette R-77): only production is located elsewhere, the QA stack is a
        // disposable one on the CI runner that its own run tears down.
        if (servers.Count == 0 && production) servers = ProductionServers(inputs);
        return (match?.Name ?? suffix, servers);
    }

    /// <summary>Where a production with no environment server runs: the servers of the project its deployment
    /// pipelines name in <c>deployServer</c>, else every server linked to the project. A name the project has no
    /// server for is never looked up elsewhere.</summary>
    private static IReadOnlyList<CleanupServerInfo> ProductionServers(ProjectServerCleanupInputs inputs)
    {
        var linked = inputs.ProjectServers.Select(item => item.Server).OfType<CleanupServerInfo>()
            .Concat(inputs.Environments.SelectMany(item => item.Servers))
            .DistinctBy(server => server.Id)
            .ToList();
        var deployed = linked
            .Where(server => (inputs.ProductionDeployServers ?? []).Contains(server.Name, StringComparer.Ordinal))
            .ToList();
        return deployed.Count > 0
            ? deployed
            : [.. inputs.ProjectServers.Select(item => item.Server).OfType<CleanupServerInfo>().DistinctBy(server => server.Id)];
    }

    private static ProjectDeletionEnvironmentDto? Resolve(
        string environment, IReadOnlyDictionary<string, string> values, string slug,
        ProjectServerCleanupForeignIdentity foreign, IReadOnlySet<string> controlPlaneHosts, List<ProjectDeletionRefusedValueDto> refused)
    {
        string? Accept(string key, string? raw, Func<string, ProjectDeletionRefusal?> check, bool lowerCase = false)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var value = Expand(raw, values);
            // Host names are case-insensitive: the files and certificates are named in lower case.
            if (lowerCase) value = value?.ToLowerInvariant();
            var reason = value is null ? ProjectDeletionRefusal.UnresolvedReference
                : value.Length == 0 ? (ProjectDeletionRefusal?)null
                : check(value) ?? (foreign.Claims(slug, key, value) ? ProjectDeletionRefusal.SharedWithAnotherProject : null);
            if (reason is { } refusal)
            {
                refused.Add(new ProjectDeletionRefusedValueDto(environment, key, value ?? raw, refusal));
                return null;
            }
            return string.IsNullOrEmpty(value) ? null : value;
        }

        // A Compose project is tied to this project by its name (the wizard writes <slug>-prod): any other name
        // could be a service the project only points at, so it is listed and left alone.
        var compose = Accept(ComposeProjectKey, values.GetValueOrDefault(ComposeProjectKey), value =>
            !ProjectServerResourceNames.IsComposeProject(value) ? ProjectDeletionRefusal.InvalidName
            : ProjectServerResourceNames.IsReservedForControlPlane(value) ? ProjectDeletionRefusal.ReservedForControlPlane
            : !ProjectServerResourceNames.IsNamedAfter(value, slug) ? ProjectDeletionRefusal.NotNamedAfterProject
            : null);
        var stateDirectory = Accept(StateDirectoryKey, values.GetValueOrDefault(StateDirectoryKey), value =>
            !ProjectServerResourceNames.IsWellFormedStateDirectory(value) ? ProjectDeletionRefusal.InvalidName
            : !ProjectServerResourceNames.IsStateDirectory(value) ? ProjectDeletionRefusal.ReservedForControlPlane
            : !ProjectServerResourceNames.IsStateDirectoryOf(value, slug) ? ProjectDeletionRefusal.OutsideProjectBase
            : null);
        // The blue-green upstream file: a plain file of sites-available named after the project (the wizard writes
        // /etc/apache2/sites-available/000-<slug>-upstream.conf).
        var upstream = Accept(UpstreamConfigurationKey, values.GetValueOrDefault(UpstreamConfigurationKey), value =>
            !ProjectServerResourceNames.IsWellFormedUpstreamConfiguration(value) ? ProjectDeletionRefusal.InvalidName
            : !ProjectServerResourceNames.IsUpstreamConfiguration(value) ? ProjectDeletionRefusal.ReservedForControlPlane
            : !ProjectServerResourceNames.IsUpstreamConfigurationOf(value, slug) ? ProjectDeletionRefusal.NotNamedAfterProject
            : null);
        var hosts = new List<string>();
        foreach (var (hostKey, raw) in RawHosts(values))
        {
            var host = Accept(hostKey, raw, value =>
                !ProjectServerResourceNames.IsHost(value) ? ProjectDeletionRefusal.InvalidName
                : ProjectServerResourceNames.IsControlPlaneNamespaceHost(value) ? ProjectDeletionRefusal.ReservedForControlPlane
                : controlPlaneHosts.Contains(value) ? ProjectDeletionRefusal.ControlPlaneHost
                : null, lowerCase: true);
            if (host is not null && !hosts.Contains(host, StringComparer.Ordinal)) hosts.Add(host);
        }
        return compose is null && stateDirectory is null && upstream is null && hosts.Count == 0
            ? null
            : new ProjectDeletionEnvironmentDto
            {
                Name = environment,
                ComposeProject = compose,
                Hosts = hosts,
                StateDirectory = stateDirectory,
                DeploymentDirectory = compose is null ? null : ProjectServerResourceNames.DeploymentDirectory(compose),
                UpstreamConfiguration = upstream,
            };
    }

    private static IEnumerable<KeyValuePair<string, string>> Values(ProjectDeletionEnvironmentDto identity)
    {
        if (identity.ComposeProject is not null) yield return new(ComposeProjectKey, identity.ComposeProject);
        foreach (var host in identity.Hosts) yield return new("HOST", host);
        if (identity.StateDirectory is not null) yield return new(StateDirectoryKey, identity.StateDirectory);
        if (identity.UpstreamConfiguration is not null) yield return new(UpstreamConfigurationKey, identity.UpstreamConfiguration);
    }

    private static bool IsProjectLibrary(CleanupLibrary library) => library.EnvironmentId is null && library.ProjectServerId is null;

    private static bool Owned(CleanupServerInfo server, int organizationId) => !server.Deleted && server.OrganizationId == organizationId;

    /// <summary>What the agent needs to remove the project's virtual hosts and blue-green upstream file
    /// (<c>apache.manage</c>) and certificates (<c>certbot.manage</c>) and does not declare; empty when the project has
    /// neither a host nor an upstream file on this server.</summary>
    private static List<string> MissingCapabilities(CleanupServerInfo server, IReadOnlyList<ProjectDeletionEnvironmentDto> environments)
    {
        string[] needed = environments.Any(item => item.Hosts.Count > 0) ? s_hostCapabilities
            : environments.Any(item => item.UpstreamConfiguration is not null) ? [AgentCapabilities.ApacheManagement]
            : [];
        return [.. needed.Where(capability => !AgentCapabilities.IsDeclared(server.CapabilitiesJson, capability))];
    }

    private static readonly string[] s_hostCapabilities = [AgentCapabilities.ApacheManagement, AgentCapabilities.CertbotManagement];

    private static ProjectDeletionServerState StateOf(CleanupServerInfo server, ServerWriteAccess writable) =>
        !writable.Allows(server.Id) ? ProjectDeletionServerState.NoWriteAccess
        : server.Status != ServerStatus.Online ? ProjectDeletionServerState.Offline
        : !AgentCapabilities.IsDeclared(server.CapabilitiesJson, AgentCapabilities.ProjectResources) ? ProjectDeletionServerState.AgentTooOld
        : ProjectDeletionServerState.Ready;
}
