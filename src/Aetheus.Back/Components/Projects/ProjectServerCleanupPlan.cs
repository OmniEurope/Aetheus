// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>A server as the deletion plan sees it.</summary>
public sealed record CleanupServerInfo(int Id, string Name, int OrganizationId, ServerStatus Status, string? CapabilitiesJson, bool Deleted);

/// <summary>A variable library of the project with its entries.</summary>
public sealed record CleanupLibrary(string Name, int? EnvironmentId, int? ProjectServerId, IReadOnlyDictionary<string, string> Entries);

/// <summary>An environment of the project with the servers it was given.</summary>
public sealed record CleanupEnvironment(int Id, string Name, IReadOnlyList<CleanupServerInfo> Servers);

/// <summary>A project server and the registered server it points to, if any.</summary>
public sealed record CleanupProjectServer(int Id, string DisplayName, CleanupServerInfo? Server);

/// <summary>A backup policy of the project and the server that keeps its archives.</summary>
public sealed record CleanupBackupPolicy(int Id, CleanupServerInfo Server);

/// <summary>Everything the plan is computed from, read once by the repository.</summary>
/// <param name="ProductionDeployServers">The server names the project's production deployment pipelines target: the
/// default of their <c>deployServer</c> parameter, which the setup wizard writes (recette R-21, R-77). Empty when no
/// pipeline of the project declares one.</param>
public sealed record ProjectServerCleanupInputs(
    int ProjectId,
    string ProjectName,
    int OrganizationId,
    IReadOnlyList<CleanupLibrary> Libraries,
    IReadOnlyList<CleanupEnvironment> Environments,
    IReadOnlyList<CleanupProjectServer> ProjectServers,
    IReadOnlyList<CleanupBackupPolicy> BackupPolicies,
    IReadOnlyList<CleanupForeignProject> OtherProjects,
    IReadOnlyList<string>? ProductionDeployServers = null);

/// <summary>Another project (or, with an empty slug, the libraries no project owns) and its libraries: what it names
/// must not be removed by this project's deletion.</summary>
public sealed record CleanupForeignProject(string Slug, bool SameOrganization, IReadOnlyList<CleanupLibrary> Libraries);

/// <summary>What a project deployed on one server, as far as its libraries and backup policies say.</summary>
public sealed record ProjectServerCleanupTarget(
    int ServerId,
    string ServerName,
    ProjectDeletionServerState State,
    IReadOnlyList<ProjectDeletionEnvironmentDto> Environments,
    IReadOnlyList<int> BackupPolicyIds)
{
    /// <summary>The project's slug, which the agent checks the state directories and Compose projects against.</summary>
    public string Slug { get; init; } = string.Empty;

    /// <summary>Capabilities the agent lacks to remove what it covers (<c>apache.manage</c>, <c>certbot.manage</c>).</summary>
    public IReadOnlyList<string> MissingCapabilities { get; init; } = [];

    /// <summary>Whether the agent can remove resources of this kind (virtual hosts and the blue-green upstream file
    /// need <c>apache.manage</c>, certificates <c>certbot.manage</c>).</summary>
    public bool CanRemove(ProjectServerResourceKind kind) => kind switch
    {
        ProjectServerResourceKind.VirtualHost or ProjectServerResourceKind.UpstreamConfiguration =>
            !MissingCapabilities.Contains(AgentCapabilities.ApacheManagement),
        ProjectServerResourceKind.Certificate => !MissingCapabilities.Contains(AgentCapabilities.CertbotManagement),
        _ => true
    };

    /// <summary>The identity the agent is asked about, with the confirmed removals when given.</summary>
    public ProjectServerResourceSpec Spec(IReadOnlyList<ProjectServerResourceRef>? remove = null) => new()
    {
        Slug = Slug,
        ComposeProjects = [.. Environments.Select(item => item.ComposeProject).OfType<string>().Distinct(StringComparer.Ordinal)],
        Hosts = [.. Environments.SelectMany(item => item.Hosts).Distinct(StringComparer.Ordinal)],
        StateDirectories = [.. Environments.Select(item => item.StateDirectory).OfType<string>().Distinct(StringComparer.Ordinal)],
        BackupPolicyIds = [.. BackupPolicyIds.Distinct()],
        UpstreamConfigurations = [.. Environments.Select(item => item.UpstreamConfiguration).OfType<string>().Distinct(StringComparer.Ordinal)],
        Remove = remove is null ? [] : [.. remove],
    };

    /// <summary>The environment a resource belongs to, through the scope it was found in (backup archives
    /// belong to the project, not to an environment).</summary>
    public string? EnvironmentOf(ProjectServerResourceKind kind, string scope) => kind switch
    {
        ProjectServerResourceKind.Container or ProjectServerResourceKind.Volume or ProjectServerResourceKind.Network
            or ProjectServerResourceKind.Image or ProjectServerResourceKind.DeploymentDirectory =>
            Environments.FirstOrDefault(item => item.ComposeProject == scope)?.Name,
        ProjectServerResourceKind.UpstreamConfiguration => Environments.FirstOrDefault(item => item.UpstreamConfiguration == scope)?.Name,
        ProjectServerResourceKind.VirtualHost or ProjectServerResourceKind.Certificate =>
            Environments.FirstOrDefault(item => item.Hosts.Contains(scope, StringComparer.Ordinal))?.Name,
        ProjectServerResourceKind.StateDirectory => Environments.FirstOrDefault(item => item.StateDirectory == scope)?.Name,
        _ => null
    };

    public ProjectDeletionServerDto ToDto() => new()
    {
        ServerId = ServerId,
        ServerName = ServerName,
        State = State,
        Environments = [.. Environments],
        BackupPolicyCount = BackupPolicyIds.Count,
        MissingCapabilities = [.. MissingCapabilities],
    };
}

/// <summary>The servers to clean and the library values refused, for one project.</summary>
public sealed record ProjectServerCleanupPlan(
    int ProjectId,
    string ProjectName,
    int OrganizationId,
    IReadOnlyList<ProjectServerCleanupTarget> Servers,
    IReadOnlyList<ProjectDeletionRefusedValueDto> Refused)
{
    public ProjectServerCleanupTarget? Server(int serverId) => Servers.FirstOrDefault(item => item.ServerId == serverId);
}

/// <summary>The servers the user deleting a project may write to: <see cref="ServerIds"/> null means every server
/// (the user's role grants Write on all of them).</summary>
public sealed record ServerWriteAccess(IReadOnlySet<int>? ServerIds)
{
    public static ServerWriteAccess All { get; } = new((IReadOnlySet<int>?)null);

    public static ServerWriteAccess None { get; } = new(new HashSet<int>());

    public bool Allows(int serverId) => ServerIds is null || ServerIds.Contains(serverId);
}

/// <summary>What a project deletion must not touch, for the current request: servers the user may not write to,
/// and the public hosts of the Aetheus control plane (lower case).</summary>
public sealed record ProjectServerCleanupGuards(ServerWriteAccess Writable, IReadOnlySet<string> ControlPlaneHosts);

/// <summary>Whether every server resource of the project is proved removed, and what is not.</summary>
public sealed record ProjectServerCleanupVerdict(bool Clean, IReadOnlyList<string> Problems, string Summary);
