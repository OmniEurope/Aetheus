// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

public class ServerRepository(AppDbContext db, TimeProvider timeProvider) : IServerRepository
{
    public async Task<(List<Server> Items, int TotalCount)> GetServersPagedAsync(
        string? search, string? sortBy, bool sortDescending,
        ServerType? type, ServerStatus? status,
        int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var query = BuildFilteredSortedServerQuery(search, sortBy, sortDescending, type, status, accessibleIds);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        return (items, totalCount);
    }

    public async Task<(List<ServerDto> Items, int TotalCount)> GetServersPagedProjectedAsync(
        string? search, string? sortBy, bool sortDescending,
        ServerType? type, ServerStatus? status,
        int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = BuildFilteredSortedServerQuery(search, sortBy, sortDescending, type, status, accessibleIds, columnFilters);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);

        // Project to an anonymous type first (Tags is a JSON string that must be
        // deserialized in memory), then map to ServerDto after materialization.
        var projected = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Hostname,
                s.OsDescription,
                s.AgentVersion,
                s.AgentProtocolVersion,
                s.AgentCapabilitiesJson,
                s.AgentUpdateReserved,
                AgentUpdateRequest = s.AgentUpdateRequests
                    .OrderByDescending(request => request.RequestedAt)
                    .Select(request => new AgentUpdateRequestSummaryDto
                    {
                        RequestId = request.Id,
                        ObservedVersion = request.ObservedVersion,
                        TargetVersion = request.TargetVersion,
                        Status = request.Status,
                        BlockingTaskCount = s.Tasks.Count(task =>
                            task.Operation != OperationKind.AgentSelfUpdate
                            && (task.Status == TaskExecutionStatus.Pending
                                || task.Status == TaskExecutionStatus.Assigned
                                || task.Status == TaskExecutionStatus.Running)),
                        RequestedAt = request.RequestedAt,
                        HandoffAt = request.HandoffAt,
                        ConfirmedAt = request.ConfirmedAt,
                        FailureCode = request.FailureCode,
                        FailureDiagnostic = request.FailureDiagnostic
                    })
                    .FirstOrDefault(),
                s.Status,
                s.Type,
                s.LastHeartbeat,
                s.Tags,
                s.CreatedAt,
                s.OrganizationId,
                s.AgentInstalledAt,
                s.ExternalProbe,
                s.ExternalProbeAt,
                s.ExternalProbeTarget,
                s.PipelineRunnerEnabled,
                s.DockerAvailable,
                s.DeploymentTargetAvailable,
                s.RequireContainerIsolation,
                s.InsecureTls,
                s.CapabilityDiagnosticsJson
                ,
                s.ScannerCapabilitiesJson
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var items = projected.Select(s => new ServerDto
        {
            Id = s.Id,
            Name = s.Name,
            Hostname = s.Hostname,
            OsDescription = s.OsDescription,
            AgentVersion = s.AgentVersion,
            AgentProtocolVersion = s.AgentProtocolVersion,
            AgentCapabilities = ServerDataMapper.DeserializeDiagnostics(s.AgentCapabilitiesJson),
            AgentUpdateReserved = s.AgentUpdateReserved,
            AgentUpdateRequest = s.AgentUpdateRequest,
            Status = s.Status,
            Type = s.Type,
            LastHeartbeat = s.LastHeartbeat,
            Tags = TagsHelper.DeserializeTags(s.Tags),
            CreatedAt = s.CreatedAt,
            OrganizationId = s.OrganizationId,
            AgentInstalledAt = s.AgentInstalledAt,
            ExternalProbe = s.ExternalProbe,
            ExternalProbeAt = s.ExternalProbeAt,
            ExternalProbeTarget = s.ExternalProbeTarget,
            PipelineRunnerEnabled = s.PipelineRunnerEnabled,
            DockerAvailable = s.DockerAvailable,
            DeploymentTargetAvailable = s.DeploymentTargetAvailable,
            RequireContainerIsolation = s.RequireContainerIsolation,
            InsecureTls = s.InsecureTls,
            CapabilityDiagnostics = ServerDataMapper.DeserializeDiagnostics(s.CapabilityDiagnosticsJson)
            ,
            ScannerCapabilities = ServerDataMapper.DeserializeDiagnostics(s.ScannerCapabilitiesJson)
        }).ToList();

        return (items, totalCount);
    }

    // Shared filter + sort pipeline for the two paged list overloads (entity vs. projected).
    // Identical WHERE/ORDER BY clauses live here so they cannot drift apart.
    public async Task<List<ServerFilterFact>> GetServerFilterFactsAsync(List<int>? accessibleIds, CancellationToken ct = default)
    {
        var query = db.Servers.AsNoTracking();
        if (accessibleIds is not null)
            query = query.Where(server => accessibleIds.Contains(server.Id));

        var rows = await query
            .Select(server => new { server.Id, server.OsDescription, server.AgentVersion, server.Tags })
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(row => new ServerFilterFact(row.Id, row.OsDescription, row.AgentVersion, TagsHelper.DeserializeTags(row.Tags)))];
    }

    private IQueryable<Server> BuildFilteredSortedServerQuery(
        string? search, string? sortBy, bool sortDescending,
        ServerType? type, ServerStatus? status, List<int>? accessibleIds,
        IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = db.Servers.AsNoTracking().AsQueryable();

        if (accessibleIds is not null)
            query = query.Where(s => accessibleIds.Contains(s.Id));

        // The list's column header filters, before the count.
        query = ServerListQuery.Columns.ApplyFilters(query, columnFilters);

        if (type.HasValue)
            query = query.Where(s => s.Type == type.Value);

        if (status.HasValue)
            query = query.Where(s => s.Status == status.Value);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(s => s.Name.Contains(search)
                || s.Hostname.Contains(search)
                || s.Tags.Contains(search));

        return sortBy?.ToLowerInvariant() switch
        {
            "name" => sortDescending ? query.OrderByDescending(s => s.Name) : query.OrderBy(s => s.Name),
            "status" => sortDescending ? query.OrderByDescending(s => s.Status) : query.OrderBy(s => s.Status),
            "lastheartbeat" => sortDescending ? query.OrderByDescending(s => s.LastHeartbeat) : query.OrderBy(s => s.LastHeartbeat),
            // Default (no column header chosen): online servers first, then most-recent heartbeat first
            // - operators want the live fleet at the top and the freshest signal leading within it.
            _ => query
                .OrderBy(s => s.Status == ServerStatus.Online ? 0 : 1)
                .ThenByDescending(s => s.LastHeartbeat)
        };
    }


    public async Task<Server?> GetServerDetailAsync(int id, CancellationToken ct = default)
    {
        return await db.Servers
            .AsNoTracking()
            .Where(s => s.Id == id)
            .Include(s => s.Services)
            .Include(s => s.DockerContainers)
            .Include(s => s.DockerImages)
            .Include(s => s.DockerComposeStacks)
            .Include(s => s.DockerNetworks)
            .Include(s => s.DockerVolumes)
            .Include(s => s.ApacheState)
            .Include(s => s.ApacheModules)
            .Include(s => s.ApacheVirtualHosts)
            .Include(s => s.CertbotCertificates)
            .Include(s => s.CertbotState)
            .Include(s => s.MailState)
            .Include(s => s.TeamspeakState)
            .Include(s => s.PortsentryState)
            .Include(s => s.RkhunterState)
            .Include(s => s.RkhunterWarnings)
            .Include(s => s.Tasks.OrderByDescending(t => t.CreatedAt).Take(10))
            .Include(s => s.AgentUpdateRequests.OrderByDescending(request => request.RequestedAt).Take(1))
            .Include(s => s.Metrics.OrderByDescending(m => m.Timestamp).Take(1))
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public Task<Server?> GetServerWithCollectionsAsync(int id, CancellationToken ct = default)
        => GetServerDetailAsync(id, ct);

    public async Task<Server?> FindServerAsync(int id, CancellationToken ct = default)
    {
        return await db.Servers.FindAsync([id], ct).ConfigureAwait(false);
    }

    public Task<bool> IsMailStackManagedAsync(int serverId, CancellationToken ct = default) =>
        db.MailStates.AsNoTracking().AnyAsync(state => state.ServerId == serverId && state.IsManagedByAetheus, ct);

    public async Task<Server?> FindServerWithTokensAsync(int id, CancellationToken ct = default)
    {
        // WHERE BEFORE Include (per architecture rules) - this is a single-row
        // load anyway, but stays consistent with the rest of the repo.
        return await db.Servers
            .Where(s => s.Id == id)
            .Include(s => s.Tokens)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    // Permanent deletion belongs to ServerRetirementRepository.PurgeServerAsync and requires an
    // already retired server.



















    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<bool> ServerExistsAsync(int serverId, CancellationToken ct = default) =>
        ServerTaskRepositoryOperations.ServerExistsAsync(db, serverId, ct);

    public async Task<List<Server>> GetStaleOnlineServersAsync(TimeSpan threshold, CancellationToken ct = default)
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - threshold;
        return await db.Servers
            .Where(s => s.Status == ServerStatus.Online && s.LastHeartbeat < cutoff)
            .AsNoTracking()
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> TryMarkOfflineIfStaleAsync(
        int serverId,
        DateTime observedLastHeartbeat,
        TimeSpan threshold,
        CancellationToken ct = default)
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - threshold;
        var candidates = db.Servers.Where(server =>
            server.Id == serverId
            && server.Status == ServerStatus.Online
            && server.LastHeartbeat == observedLastHeartbeat
            && server.LastHeartbeat < cutoff);

        if (db.Database.IsRelational())
        {
            var affected = await candidates.ExecuteUpdateAsync(
                setters => setters.SetProperty(server => server.Status, ServerStatus.Offline),
                ct).ConfigureAwait(false);
            return affected == 1;
        }

        var candidate = await candidates.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (candidate is null) return false;
        candidate.Status = ServerStatus.Offline;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }



    public async Task<List<string>> GetServerNamesAsync(List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var query = db.Servers.AsNoTracking();
        if (accessibleIds is not null) query = query.Where(s => accessibleIds.Contains(s.Id));
        return await query
            .OrderBy(s => s.Name)
            .Select(s => s.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public Task<List<string>> GetServerNamesAsync(CancellationToken ct = default)
        => GetServerNamesAsync(null, ct);

    public async Task<List<(int Id, string Name)>> GetServerIdNamePairsAsync(List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var query = db.Servers.AsNoTracking();
        if (accessibleIds is not null) query = query.Where(s => accessibleIds.Contains(s.Id));
        var rows = await query
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(r => (r.Id, r.Name)).ToList();
    }

    // GetServersForAgentUpdateAsync moved to AgentUpdateRepository: it had no consumer outside the
    // agent-update flow, and serving it from here forced AgentUpdate to depend on Servers.

    public async Task<List<int>> GetProjectIdsForServerAsync(int serverId, CancellationToken ct = default)
    {
        return await ProjectIdsForServer(serverId)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<int>> GetPipelineIdsForServerAsync(int serverId, CancellationToken ct = default)
    {
        return await PipelineIdsForServer(serverId)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<Project>> GetProjectsForServerAsync(int serverId, CancellationToken ct = default)
    {
        return await db.Projects
            .Where(p => ProjectIdsForServer(serverId).Contains(p.Id))
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<Pipeline>> GetPipelinesForServerAsync(int serverId, CancellationToken ct = default)
    {
        // Includes runs so the server pipeline list can resolve LastRunStatus and LastRunAt.
        return await db.Pipelines
            .Where(p => PipelineIdsForServer(serverId).Contains(p.Id))
            .Include(p => p.Project)
            .Include(p => p.Runs)
            .AsNoTracking()
            .AsSplitQuery()
            .OrderBy(p => p.Name)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<VariableLibrary>> GetVariableLibrariesForServerAsync(int serverId, CancellationToken ct = default)
    {
        return await db.VariableLibraries
            .Where(v => (v.ProjectId != null && ProjectIdsForServer(serverId).Contains(v.ProjectId.Value))
                || (v.ProjectServerId != null && v.ProjectServer!.ServerId == serverId)
                || (v.EnvironmentId != null && v.Environment!.Servers.Any(link => link.ServerId == serverId))
                || (v.EnvironmentId != null && v.Environment!.LinkedProjectServers
                    .Any(link => link.ProjectServer.ServerId == serverId)))
            .Include(v => v.Project)
            .Include(v => v.Environment)
            .Include(v => v.ProjectServer)
            .Include(v => v.Entries)
            .AsNoTracking()
            .OrderBy(v => v.Name)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<Vault>> GetVaultsForServerAsync(int serverId, CancellationToken ct = default)
    {
        return await db.Vaults
            .Where(v => (v.ProjectId != null && ProjectIdsForServer(serverId).Contains(v.ProjectId.Value))
                || (v.ProjectServerId != null && v.ProjectServer!.ServerId == serverId)
                || (v.EnvironmentId != null && v.Environment!.Servers.Any(link => link.ServerId == serverId))
                || (v.EnvironmentId != null && v.Environment!.LinkedProjectServers
                    .Any(link => link.ProjectServer.ServerId == serverId)))
            .Include(v => v.Project)
            .Include(v => v.Environment)
            .Include(v => v.ProjectServer)
            .Include(v => v.Secrets)
            .AsNoTracking()
            .OrderBy(v => v.Name)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    // GetReleasesForServerAsync moved to ReleaseRepository: the query is about releases, and keeping
    // it here forced Servers to depend on Pipelines to enrich each release with its source pipeline.


    private IQueryable<int> ProjectIdsForServer(int serverId)
    {
        var executedProjectIds = db.PipelineStepRuns
            .Where(psr => psr.ServerId == serverId)
            .Select(psr => psr.PipelineRun.Pipeline.ProjectId)
            .Where(pid => pid != null)
            .Select(pid => pid!.Value);
        var linkedProjectIds = db.ProjectServers
            .Where(projectServer => projectServer.ServerId == serverId)
            .Select(projectServer => projectServer.ProjectId);
        return executedProjectIds.Union(linkedProjectIds).Distinct();
    }

    private IQueryable<int> PipelineIdsForServer(int serverId)
    {
        return db.PipelineStepRuns
            .Where(psr => psr.ServerId == serverId)
            .Select(psr => psr.PipelineRun.PipelineId)
            .Distinct();
    }

    public async Task<ServiceType?> GetServiceTypeAsync(int serverId, string serviceName, CancellationToken ct = default)
    {
        return await db.ServiceInfos
            .Where(s => s.ServerId == serverId && s.Name == serviceName)
            .Select(s => (ServiceType?)s.Type)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<(List<Project> Items, int Total)> GetProjectsForServerPagedAsync(
        int serverId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null)
    {
        var query = db.Projects
            .Where(project => ProjectIdsForServer(serverId).Contains(project.Id))
            .AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(project =>
                EF.Functions.ILike(project.Name, pattern) ||
                EF.Functions.ILike(project.Description, pattern));
        }

        // The header filters, after the server scope and before the count.
        query = ServerProjectListQuery.Columns.ApplyFilters(query, filters);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy?.Trim().ToLowerInvariant(), sortDescending) switch
        {
            ("status", false) => query.OrderBy(project => project.Status).ThenBy(project => project.Name).ThenBy(project => project.Id),
            ("status", true) => query.OrderByDescending(project => project.Status).ThenBy(project => project.Name).ThenBy(project => project.Id),
            ("createdat", false) => query.OrderBy(project => project.CreatedAt).ThenBy(project => project.Id),
            ("createdat", true) => query.OrderByDescending(project => project.CreatedAt).ThenBy(project => project.Id),
            (_, true) => query.OrderByDescending(project => project.Name).ThenBy(project => project.Id),
            _ => query.OrderBy(project => project.Name).ThenBy(project => project.Id)
        };
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }

    public async Task AddTaskAsync(ServerTask task, CancellationToken ct = default)
    {
        await ServerTaskRepositoryOperations.AddTaskAsync(db, task, ct).ConfigureAwait(false);
    }

    public async Task AddTasksAsync(IEnumerable<ServerTask> tasks, CancellationToken ct = default)
    {
        db.Tasks.AddRange(tasks);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<(List<ServerTask> Items, int TotalCount)> GetTasksPagedAsync(int serverId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.Tasks
            .Where(t => t.ServerId == serverId)
            .AsNoTracking();

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        return (items, totalCount);
    }

    public Task<int> CountActiveNonUpdateTasksAsync(int serverId, CancellationToken ct = default) =>
        db.Tasks.CountAsync(task =>
            task.ServerId == serverId
            && task.Operation != OperationKind.AgentSelfUpdate
            && (task.Status == TaskExecutionStatus.Pending
                || task.Status == TaskExecutionStatus.Assigned
                || task.Status == TaskExecutionStatus.Running), ct);

    public Task<List<ServerDto>> GetServersForCompatibilityAsync(
        List<int>? accessibleIds,
        CancellationToken ct = default) =>
        ServerCompatibilityFacts.ReadAsync(db.Servers, accessibleIds, ct);

    public async Task<(List<TaskLog> Items, int TotalCount)> GetLogsPagedAsync(
        int serverId,
        int page,
        int pageSize,
        IReadOnlyList<GridFilter>? filters = null,
        IReadOnlyList<GridSort>? sorts = null,
        CancellationToken ct = default)
    {
        // A line that is only an agent directive (`##aetheus[pipelinemetric...]`,
        // `##aetheus[setvariable...]`) is a message to the backend, not a log line for a reader.
        var query = db.TaskLogs
            .Where(l => l.Task.ServerId == serverId && !l.Message.StartsWith("##aetheus["))
            .AsNoTracking();
        // The grid's header filters narrow the whole log before the count.
        query = ServerLogQuery.Columns.ApplyFilters(query, filters);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        var ordered = ServerLogQuery.Columns.ApplySorts(query, sorts) is { } sorted
            ? sorted.ThenByDescending(l => l.Timestamp).ThenByDescending(l => l.Id)
            : query.OrderByDescending(l => l.Timestamp).ThenByDescending(l => l.Id);
        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        return (items, totalCount);
    }
}
