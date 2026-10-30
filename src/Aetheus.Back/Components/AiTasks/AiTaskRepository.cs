// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AiTasks;

public sealed class AiTaskRepository(AppDbContext db) : IAiTaskRepository
{
    public async Task<(List<AiRunnerProfile> Items, int Total)> GetProfilesPageAsync(
        string? search, int page, int pageSize, CancellationToken ct,
        IReadOnlyList<GridFilter>? columnFilters = null,
        string? sortBy = null, bool sortDescending = false)
    {
        var query = db.AiRunnerProfiles.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(profile => profile.Name.Contains(search));
        // The list's column header filters, before the count.
        query = AiTaskListQuery.ProfileColumns.ApplyFilters(query, columnFilters);
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var ordered = AiTaskListQuery.ProfileColumns.ApplySorts(query,
            [new GridSort { Field = sortBy ?? "priority", Descending = sortDescending }])!;
        var items = await ordered.ThenBy(profile => profile.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }

    public Task<List<AiRunnerProfile>> GetProfilesForOrganizationsAsync(
        List<int> organizationIds, CancellationToken ct) =>
        db.AiRunnerProfiles.AsNoTracking()
            .Where(profile => organizationIds.Contains(profile.OrganizationId))
            .OrderBy(profile => profile.Name)
            .ToListAsync(ct);

    public Task<AiRunnerProfile?> FindProfileAsync(int id, CancellationToken ct) =>
        db.AiRunnerProfiles.FirstOrDefaultAsync(profile => profile.Id == id, ct);

    public Task<List<AiRunnerProfile>> GetOrganizationProfilesByPriorityAsync(
        int organizationId, CancellationToken ct) =>
        db.AiRunnerProfiles.AsNoTracking()
            .Where(profile => profile.OrganizationId == organizationId)
            .OrderBy(profile => profile.Priority)
            .ThenBy(profile => profile.Id)
            .ToListAsync(ct);

    public async Task AddProfileAsync(AiRunnerProfile profile, CancellationToken ct)
    {
        db.AiRunnerProfiles.Add(profile);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveProfileAsync(AiRunnerProfile profile, CancellationToken ct)
    {
        db.AiRunnerProfiles.Remove(profile);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<bool> ProfileHasDefinitionsAsync(int profileId, CancellationToken ct) =>
        db.AiTaskDefinitions.AsNoTracking().AnyAsync(definition => definition.ProfileId == profileId, ct);

    public Task<List<AiRunnerProfile>> GetProfilesDueForModelListAsync(DateTime checkedBefore, CancellationToken ct) =>
        db.AiRunnerProfiles
            .Where(profile => (profile.Provider == AiProviderCatalog.AnthropicToken || profile.Provider == AiProviderCatalog.OpenAiToken)
                && profile.ApiKeyEncrypted != string.Empty
                && (profile.ModelListCheckedAt == null || profile.ModelListCheckedAt < checkedBefore))
            .OrderBy(profile => profile.ModelListCheckedAt)
            .ThenBy(profile => profile.Id)
            .ToListAsync(ct);

    public async Task<(List<AiTaskDefinition> Items, int Total)> GetDefinitionsPageAsync(
        string? search, int page, int pageSize, int? projectId, int? serverId,
        List<int>? accessibleProjectIds, List<int>? accessibleServerIds, CancellationToken ct,
        IReadOnlyList<GridFilter>? columnFilters = null,
        string? sortBy = null, bool sortDescending = false)
    {
        var query = db.AiTaskDefinitions.AsNoTracking();
        if (projectId.HasValue) query = query.Where(definition => definition.ProjectId == projectId);
        if (serverId.HasValue) query = query.Where(definition => definition.ServerId == serverId);
        if (accessibleProjectIds is not null || accessibleServerIds is not null)
        {
            var projectIds = accessibleProjectIds ?? [];
            var serverIds = accessibleServerIds ?? [];
            query = query.Where(definition =>
                (definition.ProjectId.HasValue && projectIds.Contains(definition.ProjectId.Value))
                || (definition.ServerId.HasValue && serverIds.Contains(definition.ServerId.Value)));
        }
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(definition => definition.Name.Contains(search));
        // The list's column header filters, after the scope and before the count.
        query = AiTaskListQuery.DefinitionColumns.ApplyFilters(query, columnFilters);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var detailQuery = query
            .Include(definition => definition.Profile)
            .Include(definition => definition.Project)
            .Include(definition => definition.Server)
            .Include(definition => definition.Triggers);
        var ordered = AiTaskListQuery.DefinitionColumns.ApplySorts(detailQuery,
            [new GridSort { Field = sortBy ?? "Name", Descending = sortDescending }])!;
        var items = await ordered.ThenBy(definition => definition.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }

    public Task<AiTaskDefinition?> FindDefinitionAsync(int id, CancellationToken ct) =>
        db.AiTaskDefinitions
            .Include(definition => definition.Profile)
            .Include(definition => definition.Project)
            .Include(definition => definition.Server)
            .Include(definition => definition.Triggers)
            .FirstOrDefaultAsync(definition => definition.Id == id, ct);

    public async Task AddDefinitionAsync(AiTaskDefinition definition, CancellationToken ct)
    {
        db.AiTaskDefinitions.Add(definition);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveDefinitionAsync(AiTaskDefinition definition, CancellationToken ct)
    {
        db.AiTaskDefinitions.Remove(definition);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<List<AiTaskDefinition>> GetScheduledDefinitionsAsync(CancellationToken ct) =>
        db.AiTaskDefinitions
            .Where(definition => definition.Enabled && definition.Schedule != null)
            .Include(definition => definition.Profile)
            .Include(definition => definition.Triggers)
            .ToListAsync(ct);

    public Task<List<AiTaskDefinition>> GetDefinitionsForEventAsync(
        string eventType, int? projectId, int? serverId, CancellationToken ct) =>
        db.AiTaskDefinitions
            .Where(definition => definition.Enabled
                && definition.Triggers.Any(trigger => trigger.EventType == eventType)
                && ((projectId != null && definition.ProjectId == projectId)
                    || (serverId != null && definition.ServerId == serverId)))
            .Include(definition => definition.Profile)
            .Include(definition => definition.Triggers)
            .ToListAsync(ct);

    public async Task<Server?> ResolveExecutionServerAsync(
        AiTaskDefinition definition, CancellationToken ct)
    {
        if (definition.ServerId.HasValue)
            return await db.Servers.AsNoTracking()
                .FirstOrDefaultAsync(server => server.Id == definition.ServerId, ct)
                .ConfigureAwait(false);

        if (definition.ProjectId is not { } projectId) return null;

        // The servers of the project's environments count as its own (R-83); another project's never do.
        var projectServerIds = db.ServerIdsOfProject(projectId);
        return await db.Servers.AsNoTracking()
            .Where(server => server.Status == ServerStatus.Online && projectServerIds.Contains(server.Id))
            .OrderByDescending(server => server.PipelineRunnerEnabled)
            .ThenBy(server => server.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public Task<int?> GetProjectOrganizationIdAsync(int projectId, CancellationToken ct) =>
        db.Projects.AsNoTracking().Where(project => project.Id == projectId)
            .Select(project => (int?)project.OrganizationId).FirstOrDefaultAsync(ct);

    public Task<int?> GetServerOrganizationIdAsync(int serverId, CancellationToken ct) =>
        db.Servers.AsNoTracking().Where(server => server.Id == serverId)
            .Select(server => (int?)server.OrganizationId).FirstOrDefaultAsync(ct);

    public Task<GitInternalRepo?> GetPrimaryProjectRepositoryAsync(
        int projectId,
        CancellationToken ct) =>
        db.GitInternalRepos.AsNoTracking()
            .Where(repository => repository.ProjectId == projectId && !repository.IsEmpty)
            .OrderBy(repository => repository.Id)
            .FirstOrDefaultAsync(ct);

    public Task<int> GetActiveRunCountAsync(int serverId, CancellationToken ct)
    {
        var active = new[]
        {
            TaskExecutionStatus.Pending,
            TaskExecutionStatus.Assigned,
            TaskExecutionStatus.Running
        };
        return db.Tasks.AsNoTracking().CountAsync(
            task => task.ServerId == serverId
                && task.Operation == OperationKind.AiRun
                && active.Contains(task.Status), ct);
    }

    public async Task<ServerTask> AddTaskAsync(ServerTask task, CancellationToken ct)
    {
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await db.Tasks.Include(item => item.Server)
            .FirstAsync(item => item.Id == task.Id, ct).ConfigureAwait(false);
    }

    public Task<ServerTask?> FindTaskAsync(int id, CancellationToken ct) =>
        db.Tasks.FirstOrDefaultAsync(task => task.Id == id, ct);

    public Task<AiRunResult?> FindResultByTaskAsync(int taskId, CancellationToken ct) =>
        db.AiRunResults.AsNoTracking()
            .FirstOrDefaultAsync(result => result.ServerTaskId == taskId, ct);

    public Task<AiRunResult?> FindResultAsync(int id, CancellationToken ct) =>
        db.AiRunResults
            .Include(result => result.AiTaskDefinition)
            .Include(result => result.PipelineRun!)
                .ThenInclude(run => run.Pipeline)
            .FirstOrDefaultAsync(result => result.Id == id, ct);

    public async Task AddResultAsync(AiRunResult result, CancellationToken ct)
    {
        db.AiRunResults.Add(result);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<(List<AiRunResult> Items, int Total)> GetResultsPageAsync(
        int? definitionId, int? pipelineRunId, int page, int pageSize, CancellationToken ct)
    {
        IQueryable<AiRunResult> query = db.AiRunResults.AsNoTracking()
            .Include(result => result.AiTaskDefinition)
            .Include(result => result.PipelineRun!)
                .ThenInclude(run => run.Pipeline);
        if (definitionId.HasValue)
            query = query.Where(result => result.AiTaskDefinitionId == definitionId);
        if (pipelineRunId.HasValue)
            query = query.Where(result => result.PipelineRunId == pipelineRunId);
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query.OrderByDescending(result => result.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }

    public Task<List<AiProfileConsumptionDto>> GetConsumptionByProfileAsync(
        DateTime since, int? projectId, CancellationToken ct)
    {
        var query = db.AiRunResults.AsNoTracking().Where(result => result.CreatedAt >= since);
        if (projectId.HasValue)
            query = query.Where(result => result.AiTaskDefinition!.ProjectId == projectId);
        return query
            .GroupBy(result => result.ProfileName)
            .OrderBy(group => group.Key)
            .Select(group => new AiProfileConsumptionDto
            {
                ProfileName = group.Key,
                RunCount = group.Count(),
                FailedCount = group.Count(result => !result.Succeeded),
                DurationMs = group.Sum(result => result.DurationMs)
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// Pipeline a run belongs to, for the RBAC check on AI results filtered by run.
    ///
    /// An own-read rather than an injected IPipelineRunService: obtaining one integer put AiTasks
    /// inside the module cycle, and the full run DTO that service returns is discarded anyway. The
    /// null result carries the same meaning the DTO lookup did - no such run - so the caller still
    /// answers NotFound rather than Forbid.
    /// </summary>
    public Task<int?> GetRunPipelineIdAsync(int pipelineRunId, CancellationToken ct) =>
        db.PipelineRuns.AsNoTracking()
            .Where(run => run.Id == pipelineRunId)
            .Select(run => (int?)run.PipelineId)
            .FirstOrDefaultAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
