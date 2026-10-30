// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Servers;

// Server lifecycle/read orchestration (IServerLifecycleService) plus the secure-by-default posture
// toggles. The heartbeat-ingestion, service-management, agent-contact and diagnostic surfaces are
// implemented by dedicated collaborators (no partial split) that this service composes from its own
// dependencies and delegates to. One instance is registered in DI and exposed under every interface.
public class ServerService(
    IServerRepository repo,
    IServerHeartbeatRepository heartbeatRepo,
    IHubContext<ServerHub> serverHub,
    IHubContext<AlertHub> alertHub,
    IAuditService audit,
    Tasks.ITaskService taskService,
    IOptions<BackgroundServicesOptions> backgroundOptions,
    TimeProvider timeProvider,
    IDbTransactionScope transaction,
    ILoggerFactory? loggerFactory = null,
    IAgentCompatibilityPolicy? compatibilityPolicy = null,
    // No IPipelineRepository: its single use was enriching server releases with their source
    // pipeline, and that whole endpoint now lives in Releases, which already sits above Pipelines.
    IAgentUpdateConfirmationService? updateConfirmation = null,
    // Optional for direct unit-test construction; without this dependency the heartbeat carries no
    // port observations.
    PortRegistry.IPortRegistryService? portRegistry = null,
    // Dispatches MailInventoryReportedEvent from heartbeats; optional for direct test construction,
    // with the real dispatcher supplied by DI.
    Services.DomainEvents.IDomainEventDispatcher? domainEvents = null)
    : IServerLifecycleService, IServerHeartbeatService, IServerServiceManagementService,
      IServerAgentContactService, IServerDiagnosticService
{
    // ServerHeartbeatProcessor is internal, so a typed ILogger<> for it cannot appear on this public
    // ctor - build the category logger from the (public) factory instead. Optional/null-defaulted so unit
    // tests that new-up the service directly need not thread a factory (DI still injects the real one).
    private readonly ServerHeartbeatProcessor _heartbeat = new(repo, heartbeatRepo, serverHub, alertHub, timeProvider, transaction, updateConfirmation, portRegistry, domainEvents,
        (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ServerHeartbeatProcessor>());
    private readonly ServerServiceManager _serviceManager = new(repo, heartbeatRepo, audit, taskService);
    private readonly ServerAgentContactProbe _contact = new(repo, backgroundOptions, timeProvider);
    private readonly ServerDiagnosticAnalyzer _diagnostic = new(repo, timeProvider, compatibilityPolicy);

    public async Task<PaginatedResult<ServerDto>> GetServersAsync(
        PaginationRequest request,
        ServerType? type = null,
        ServerStatus? status = null,
        AgentCompatibilityStatus? compatibility = null,
        List<int>? accessibleIds = null,
        CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var effectiveAccessibleIds = accessibleIds;
        if (compatibility is not null && compatibilityPolicy is not null)
        {
            var facts = await repo.GetServersForCompatibilityAsync(accessibleIds, ct).ConfigureAwait(false);
            effectiveAccessibleIds = facts
                .Where(server => compatibilityPolicy.Evaluate(server).Status == compatibility)
                .Select(server => server.Id)
                .ToList();
        }

        // The tag and agent columns resolve to server ids here; the others reach the query.
        var columnFilters = request.Filters ?? [];
        foreach (var filter in columnFilters.Where(ServerListQuery.IsResolvedInMemory))
        {
            var matching = await ResolveInMemoryFilterAsync(filter, effectiveAccessibleIds, ct).ConfigureAwait(false);
            effectiveAccessibleIds = effectiveAccessibleIds is null
                ? [.. matching]
                : [.. effectiveAccessibleIds.Where(matching.Contains)];
        }

        var storedFilters = columnFilters.Where(filter => !ServerListQuery.IsResolvedInMemory(filter)).ToList();
        var (items, totalCount) = await repo.GetServersPagedProjectedAsync(
            request.Search, request.SortBy, request.SortDescending,
            type, status, page, pageSize, effectiveAccessibleIds, ct,
            storedFilters.Count == 0 ? null : storedFilters).ConfigureAwait(false);

        return new PaginatedResult<ServerDto>
        {
            Items = items.Select(WithCompatibility).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    private async Task<HashSet<int>> ResolveInMemoryFilterAsync(GridFilter filter, List<int>? scope, CancellationToken ct)
    {
        if (string.Equals(filter.Field, ServerListQuery.TagsKey, StringComparison.OrdinalIgnoreCase))
        {
            var facts = await repo.GetServerFilterFactsAsync(scope, ct).ConfigureAwait(false);
            return ServerListQuery.TaggedWithAny(filter, facts.Select(fact => (fact.Id, (IReadOnlyCollection<string>)fact.Tags)));
        }

        var servers = await repo.GetServersForCompatibilityAsync(scope, ct).ConfigureAwait(false);
        return ServerListQuery.AgentMatchingAny(filter, servers.Select(server => (
            server.Id,
            server.AgentVersion,
            compatibilityPolicy is null ? (AgentCompatibilityStatus?)null : compatibilityPolicy.Evaluate(server).Status)));
    }

    /// <summary>The OS, versions and tags present across the servers the caller can read.</summary>
    public async Task<ServerFilterValuesDto> GetServerFilterValuesAsync(List<int>? accessibleIds, CancellationToken ct = default)
    {
        var facts = await repo.GetServerFilterFactsAsync(accessibleIds, ct).ConfigureAwait(false);
        static List<string> Distinct(IEnumerable<string> values) => [.. values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
        return new ServerFilterValuesDto
        {
            OsDescriptions = Distinct(facts.Select(fact => fact.OsDescription)),
            AgentVersions = Distinct(facts.Select(fact => fact.AgentVersion)),
            Tags = Distinct(facts.SelectMany(fact => fact.Tags))
        };
    }

    public async Task<AgentCompatibilitySummaryDto> GetAgentCompatibilitySummaryAsync(
        List<int>? accessibleIds = null,
        CancellationToken ct = default)
    {
        if (compatibilityPolicy is null) return new AgentCompatibilitySummaryDto();
        var facts = await repo.GetServersForCompatibilityAsync(accessibleIds, ct).ConfigureAwait(false);
        var states = facts.Select(server => compatibilityPolicy.Evaluate(server).Status).ToList();
        return new AgentCompatibilitySummaryDto
        {
            UpToDate = states.Count(state => state == AgentCompatibilityStatus.UpToDate),
            UpdateRecommended = states.Count(state => state == AgentCompatibilityStatus.UpdateRecommended),
            UpdateRequired = states.Count(state => state == AgentCompatibilityStatus.UpdateRequired),
            Unknown = states.Count(state => state == AgentCompatibilityStatus.Unknown)
        };
    }

    public async Task<ServerDetailDto?> GetServerDetailAsync(int id, CancellationToken ct = default)
    {
        var server = await repo.GetServerDetailAsync(id, ct).ConfigureAwait(false);

        if (server is null) return null;

        var latestMetric = server.Metrics.FirstOrDefault();
        // S-TECH-N8R3: heartbeat history over the last 30 min so the detail can show a real uptime%/sparkline.
        var heartbeatSince = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-30);
        var heartbeatHistory = await heartbeatRepo.GetRecentMetricTimestampsAsync(id, heartbeatSince, ct).ConfigureAwait(false) ?? [];
        var blockingTaskCount = server.AgentUpdateReserved
            ? await repo.CountActiveNonUpdateTasksAsync(id, ct).ConfigureAwait(false)
            : 0;
        var latestUpdate = server.AgentUpdateRequests.FirstOrDefault();
        var detail = new ServerDetailDto
        {
            HeartbeatHistory = heartbeatHistory,
            Id = server.Id,
            Name = server.Name,
            Hostname = server.Hostname,
            OsDescription = server.OsDescription,
            IpAddress = server.IpAddress,
            AgentVersion = server.AgentVersion,
            AgentProtocolVersion = server.AgentProtocolVersion,
            AgentCapabilities = ServerDataMapper.DeserializeDiagnostics(server.AgentCapabilitiesJson),
            AgentUpdateReserved = server.AgentUpdateReserved,
            AgentUpdateRequest = latestUpdate is null
                ? null
                : new AgentUpdateRequestSummaryDto
                {
                    RequestId = latestUpdate.Id,
                    ObservedVersion = latestUpdate.ObservedVersion,
                    TargetVersion = latestUpdate.TargetVersion,
                    Status = latestUpdate.Status,
                    BlockingTaskCount = blockingTaskCount,
                    RequestedAt = latestUpdate.RequestedAt,
                    HandoffAt = latestUpdate.HandoffAt,
                    ConfirmedAt = latestUpdate.ConfirmedAt,
                    FailureCode = latestUpdate.FailureCode,
                    FailureDiagnostic = latestUpdate.FailureDiagnostic
                },
            Status = server.Status,
            Type = server.Type,
            LastHeartbeat = server.LastHeartbeat,
            Tags = TagsHelper.DeserializeTags(server.Tags),
            CreatedAt = server.CreatedAt,
            OrganizationId = server.OrganizationId,
            AgentInstalledAt = server.AgentInstalledAt,
            ExternalProbe = server.ExternalProbe,
            ExternalProbeAt = server.ExternalProbeAt,
            ExternalProbeTarget = server.ExternalProbeTarget,
            PipelineRunnerEnabled = server.PipelineRunnerEnabled,
            DockerAvailable = server.DockerAvailable,
            PackageManagementAvailable = server.PackageManagementAvailable,
            DeploymentTargetAvailable = server.DeploymentTargetAvailable,
            MailSetupAvailable = server.MailSetupAvailable,
            TeamspeakSetupAvailable = server.TeamspeakSetupAvailable,
            RequireContainerIsolation = server.RequireContainerIsolation,
            InsecureTls = server.InsecureTls,
            OomKillsSinceBoot = server.OomKillCount,
            CpuPercent = latestMetric?.CpuPercent ?? 0,
            MemoryUsedMb = latestMetric?.MemoryUsedMb ?? 0,
            MemoryTotalMb = latestMetric?.MemoryTotalMb ?? 0,
            DiskUsedGb = latestMetric?.DiskUsedGb ?? 0,
            DiskTotalGb = latestMetric?.DiskTotalGb ?? 0,
            InodeUsedPercent = latestMetric?.InodeUsedPercent,
            Disks = ServerDisks.Read(server.DisksJson),
            ClockOffsetMs = latestMetric?.ClockOffsetMs,
            ClockOffsetUncertaintyMs = latestMetric?.ClockOffsetUncertaintyMs,
            StorageDiagnostics = latestMetric is null
                ? new StorageDiagnosticsDto()
                : new StorageDiagnosticsDto
                {
                    DryRun = latestMetric.StorageMaintenanceDryRun,
                    DeploymentOnly = latestMetric.DeploymentOnly,
                    BuildActive = latestMetric.BuildActive,
                    LastBuildAttemptAtUtc = latestMetric.LastBuildAttemptAtUtc,
                    BuildCacheAvailable = latestMetric.BuildCacheAvailable,
                    DockerInventoryAvailable = latestMetric.DockerInventoryAvailable,
                    BuildCacheBytes = latestMetric.BuildCacheBytes,
                    BuildCacheReclaimableBytes = latestMetric.BuildCacheReclaimableBytes,
                    DockerImagesBytes = latestMetric.DockerImagesBytes,
                    DockerContainersBytes = latestMetric.DockerContainersBytes,
                    DockerVolumesBytes = latestMetric.DockerVolumesBytes,
                    AgentWorkDirectoryBytes = latestMetric.AgentWorkDirectoryBytes,
                    AgentInstallDirectoryBytes = latestMetric.AgentInstallDirectoryBytes,
                    NuGetCacheBytes = latestMetric.NuGetCacheBytes,
                    JournalBytes = latestMetric.JournalBytes,
                    CollectedAtUtc = latestMetric.Timestamp
                },
            Services = ServerDataMapper.BuildServicesWithWellKnown(server.Services),
            RecentTasks = server.Tasks.Select(ServerDataMapper.MapTaskDto).ToList(),
            Docker = ServerDataMapper.MapDockerDataDto(server),
            Apache = ServerDataMapper.MapApacheDataDto(server),
            Certbot = ServerDataMapper.MapCertbotDataDto(server),
            Cron = ServerDataMapper.MapCronDataDto(server),
            Mail = ServerDataMapper.MapMailDataDto(server),
            Teamspeak = ServerDataMapper.MapTeamspeakDataDto(server),
            Portsentry = ServerDataMapper.MapPortsentryDataDto(server),
            Rkhunter = ServerDataMapper.MapRkhunterDataDto(server),
            SshConfig = SshConfigMonitor.Read(server.SshConfigStateJson)
        };
        return detail with { AgentCompatibility = compatibilityPolicy?.Evaluate(detail) };
    }

    public async Task<ServerDto?> UpdateServerAsync(int id, UpdateServerRequest request, CancellationToken ct = default)
    {
        var server = await repo.FindServerAsync(id, ct).ConfigureAwait(false);
        if (server is null) return null;

        if (request.Name is not null) server.Name = request.Name;
        if (request.Tags is not null) server.Tags = JsonSerializer.Serialize(request.Tags);
        if (request.Status.HasValue) server.Status = request.Status.Value;
        if (request.Type.HasValue) server.Type = request.Type.Value;
        server.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "Server", server.Id, server.Name, ct).ConfigureAwait(false);
        var dto = WithCompatibility(ServerDataMapper.MapToDto(server));
        await serverHub.Clients.Groups([HubGroups.AllServers, HubGroups.Server(server.Id), HubGroups.ServerOrg(server.OrganizationId)]).SendAsync("ServerUpdated", dto, ct).ConfigureAwait(false);
        return dto;
    }

    public Task<bool> ServerExistsAsync(int serverId, CancellationToken ct = default)
        => repo.ServerExistsAsync(serverId, ct);

    public async Task<List<string>> GetServerNamesAsync(List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        return await repo.GetServerNamesAsync(accessibleIds, ct).ConfigureAwait(false);
    }

    public Task<List<string>> GetServerNamesAsync(CancellationToken ct = default)
        => GetServerNamesAsync(null, ct);

    public async Task<PaginatedResult<ProjectDto>> GetServerProjectsAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default, List<int>? readableProjectIds = null)
    {
        var (page, pageSize) = request.Normalize();
        var (projects, total) = await repo.GetProjectsForServerPagedAsync(
            serverId, request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters, readableProjectIds).ConfigureAwait(false);
        return new PaginatedResult<ProjectDto>
        {
            Items = projects.Select(project => ProjectDtoMapper.ToDto(project)).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<List<PipelineDto>> GetServerPipelinesAsync(int serverId, CancellationToken ct = default)
    {
        var pipelines = await repo.GetPipelinesForServerAsync(serverId, ct).ConfigureAwait(false);
        return pipelines.Select(p =>
        {
            // S-TECH-P2WX: resolve the most recent run so the server-scoped list shows real
            // "Last status"/"Last run" values instead of "-".
            var lastRun = p.Runs.OrderByDescending(r => r.StartedAt).FirstOrDefault();
            return new PipelineDto
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                TriggerType = p.TriggerType,
                ProjectId = p.ProjectId,
                ProjectName = p.Project?.Name,
                LastRunStatus = lastRun?.Status,
                LastRunAt = lastRun?.StartedAt,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt
            };
        }).ToList();
    }

    public async Task<List<VariableLibraryDto>> GetServerVariableLibrariesAsync(int serverId, CancellationToken ct = default)
    {
        var libs = await repo.GetVariableLibrariesForServerAsync(serverId, ct).ConfigureAwait(false);
        return libs.Select(v => new VariableLibraryDto
        {
            Id = v.Id,
            Name = v.Name,
            Description = v.Description,
            ProjectId = v.ProjectId,
            ProjectName = v.Project?.Name,
            EnvironmentId = v.EnvironmentId,
            EnvironmentName = v.Environment?.Name,
            ProjectServerId = v.ProjectServerId,
            ProjectServerName = v.ProjectServer?.DisplayName,
            EntryCount = v.Entries.Count,
            CreatedAt = v.CreatedAt,
            UpdatedAt = v.UpdatedAt,
            RowVersion = v.RowVersion
        }).ToList();
    }

    public async Task<List<VaultDto>> GetServerVaultsAsync(int serverId, CancellationToken ct = default)
    {
        var vaults = await repo.GetVaultsForServerAsync(serverId, ct).ConfigureAwait(false);
        return vaults.Select(v => new VaultDto
        {
            Id = v.Id,
            Name = v.Name,
            Description = v.Description,
            ProjectId = v.ProjectId,
            ProjectName = v.Project?.Name,
            EnvironmentId = v.EnvironmentId,
            EnvironmentName = v.Environment?.Name,
            ProjectServerId = v.ProjectServerId,
            ProjectServerName = v.ProjectServer?.DisplayName,
            SecretCount = v.Secrets.Count,
            CreatedAt = v.CreatedAt,
            UpdatedAt = v.UpdatedAt,
            RowVersion = v.RowVersion
        }).ToList();
    }


    public async Task<PaginatedResult<ServerTaskDto>> GetServerTasksAsync(int serverId, PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetTasksPagedAsync(serverId, page, pageSize, ct).ConfigureAwait(false);
        return new PaginatedResult<ServerTaskDto>
        {
            Items = items.Select(ServerDataMapper.MapTaskDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PaginatedResult<TaskLogDto>> GetServerLogsAsync(int serverId, PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetLogsPagedAsync(
            serverId, page, pageSize, request.Filters, ServerLogQuery.SortsOf(request), ct).ConfigureAwait(false);
        return TaskLogMapper.ToPaginatedResult(items, totalCount, page, pageSize);
    }

    // --- Posture toggles (Server.Admin-gated by the caller; idempotent; Sec-Audit on change) ---

    public async Task<ServerDto?> SetPipelineRunnerEnabledAsync(int id, bool enabled, string actorUsername, CancellationToken ct = default)
    {
        var server = await repo.FindServerAsync(id, ct).ConfigureAwait(false);
        if (server is null) return null;

        // Idempotent - no audit, no broadcast when the value already matches. Keeps the
        // audit log focused on actual posture changes (a clicker burst doesn't pollute it).
        if (server.PipelineRunnerEnabled == enabled)
            return WithCompatibility(ServerDataMapper.MapToDto(server));

        server.PipelineRunnerEnabled = enabled;
        server.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        // Posture change - same audit weight as a role promotion: who, what, when, target.
        var action = enabled ? "PipelineRunnerEnabled" : "PipelineRunnerDisabled";
        var detail = $"{server.Name} pipeline runner {(enabled ? "enabled" : "disabled")} by {actorUsername}";
        await audit.LogAsync(action, "Server", server.Id, detail, ct).ConfigureAwait(false);
        await serverHub.Clients.Groups([HubGroups.AllServers, HubGroups.Server(server.Id), HubGroups.ServerOrg(server.OrganizationId)])
            .SendAsync("ServerUpdated", WithCompatibility(ServerDataMapper.MapToDto(server)), ct).ConfigureAwait(false);
        return WithCompatibility(ServerDataMapper.MapToDto(server));
    }

    public async Task<ServerDto?> SetContainerIsolationRequiredAsync(int id, bool required, string actorUsername, CancellationToken ct = default)
    {
        var server = await repo.FindServerAsync(id, ct).ConfigureAwait(false);
        if (server is null) return null;

        if (server.RequireContainerIsolation == required)
            return WithCompatibility(ServerDataMapper.MapToDto(server));

        server.RequireContainerIsolation = required;
        server.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        var action = required ? "ContainerIsolationRequired" : "ContainerIsolationOptional";
        var detail = $"{server.Name} container-isolation policy {(required ? "enforced" : "relaxed")} by {actorUsername}";
        await audit.LogAsync(action, "Server", server.Id, detail, ct).ConfigureAwait(false);
        await serverHub.Clients.Groups([HubGroups.AllServers, HubGroups.Server(server.Id), HubGroups.ServerOrg(server.OrganizationId)])
            .SendAsync("ServerUpdated", WithCompatibility(ServerDataMapper.MapToDto(server)), ct).ConfigureAwait(false);
        return WithCompatibility(ServerDataMapper.MapToDto(server));
    }

    // --- Delegations to the focused collaborators (one registered instance fronts every interface) ---

    public Task ProcessHeartbeatAsync(int serverId, ServerHeartbeatDto heartbeat, CancellationToken ct = default)
        => _heartbeat.ProcessHeartbeatAsync(serverId, heartbeat, ct);

    public Task<int> ExecuteServiceActionAsync(int serverId, ServiceActionRequest request, CancellationToken ct = default)
        => _serviceManager.ExecuteServiceActionAsync(serverId, request, ct);

    public Task<int> InstallServiceAsync(int serverId, string serviceName, CancellationToken ct = default)
        => _serviceManager.InstallServiceAsync(serverId, serviceName, ct);

    public Task<int> UninstallServiceAsync(int serverId, string serviceName, CancellationToken ct = default)
        => _serviceManager.UninstallServiceAsync(serverId, serviceName, ct);

    public Task<int> CreateServiceLogsTaskAsync(int serverId, string serviceName, int lines, bool follow, CancellationToken ct = default)
        => _serviceManager.CreateServiceLogsTaskAsync(serverId, serviceName, lines, follow, ct);

    public Task<int> ListInstalledPackagesAsync(int serverId, CancellationToken ct = default)
        => _serviceManager.ListInstalledPackagesAsync(serverId, ct);

    public Task<int> UpgradeSystemAsync(int serverId, bool dryRun, CancellationToken ct = default)
        => _serviceManager.UpgradeSystemAsync(serverId, dryRun, ct);

    public Task<ServerSecurityUpdatesDto> GetSecurityUpdatesAsync(int serverId, CancellationToken ct = default)
        => _serviceManager.GetSecurityUpdatesAsync(serverId, ct);

    public Task<int> FirewallAllowAsync(int serverId, FirewallRuleRequest request, CancellationToken ct = default)
        => _serviceManager.FirewallAllowAsync(serverId, request, ct);

    public Task<int> FirewallDenyAsync(int serverId, FirewallRuleRequest request, CancellationToken ct = default)
        => _serviceManager.FirewallDenyAsync(serverId, request, ct);

    public Task<int> FirewallDeleteRuleAsync(int serverId, FirewallRuleRequest request, CancellationToken ct = default)
        => _serviceManager.FirewallDeleteRuleAsync(serverId, request, ct);

    public Task<int> FirewallToggleAsync(int serverId, bool enabled, CancellationToken ct = default)
        => _serviceManager.FirewallToggleAsync(serverId, enabled, ct);

    public Task<ServerFirewallDto> GetFirewallAsync(int serverId, CancellationToken ct = default)
        => _serviceManager.GetFirewallAsync(serverId, ct);

    public Task<ContactAgentResultDto?> ContactAgentAsync(int serverId, CancellationToken ct = default)
        => _contact.ContactAgentAsync(serverId, ct);

    public Task<ServerDiagnosticDto?> DiagnoseAsync(int serverId, CancellationToken ct = default)
        => _diagnostic.DiagnoseAsync(serverId, ct);

    private ServerDto WithCompatibility(ServerDto server) =>
        server with { AgentCompatibility = compatibilityPolicy?.Evaluate(server) };
}
