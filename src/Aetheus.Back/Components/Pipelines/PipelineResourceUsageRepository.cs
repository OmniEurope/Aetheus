// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

public sealed class PipelineResourceUsageRepository(AppDbContext db) : IPipelineResourceUsageRepository
{
    public async Task<List<UsageResourceRow>> GetLibrariesAsync(List<int>? accessibleIds, CancellationToken ct = default)
    {
        var query = db.VariableLibraries.AsNoTracking();
        if (accessibleIds is not null) query = query.Where(library => accessibleIds.Contains(library.Id));
        return await query
            .OrderBy(library => library.Id)
            .Select(library => new UsageResourceRow(
                library.Id,
                library.Name,
                library.ProjectId
                    ?? (library.Environment != null ? library.Environment.ProjectId : null)
                    ?? (library.ProjectServer != null ? (int?)library.ProjectServer.ProjectId : null)))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<UsageResourceRow>> GetVaultsAsync(List<int>? accessibleIds, CancellationToken ct = default)
    {
        var query = db.Vaults.AsNoTracking();
        if (accessibleIds is not null) query = query.Where(vault => accessibleIds.Contains(vault.Id));
        return await query
            .OrderBy(vault => vault.Id)
            .Select(vault => new UsageResourceRow(
                vault.Id,
                vault.Name,
                vault.ProjectId
                    ?? (vault.Environment != null ? vault.Environment.ProjectId : null)
                    ?? (vault.ProjectServer != null ? (int?)vault.ProjectServer.ProjectId : null)))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<UsagePipelineRow>> GetPipelinesAsync(CancellationToken ct = default)
    {
        var pipelines = await db.Pipelines
            .AsNoTracking()
            .OrderBy(pipeline => pipeline.Id)
            .Select(pipeline => new
            {
                pipeline.Id,
                pipeline.Name,
                pipeline.YamlDefinition,
                ProjectId = pipeline.ProjectId
                    ?? (pipeline.Environment != null ? pipeline.Environment.ProjectId : null)
                    ?? (pipeline.ProjectServer != null ? (int?)pipeline.ProjectServer.ProjectId : null)
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var projectIds = pipelines.Select(pipeline => pipeline.ProjectId).OfType<int>().Distinct().ToList();
        var projects = await db.Projects
            .AsNoTracking()
            .Where(project => projectIds.Contains(project.Id))
            .Select(project => new { project.Id, project.Name, project.OrganizationId })
            .ToDictionaryAsync(project => project.Id, ct).ConfigureAwait(false);

        return [.. pipelines.Select(pipeline =>
        {
            var project = pipeline.ProjectId is { } id && projects.TryGetValue(id, out var found) ? found : null;
            return new UsagePipelineRow(
                pipeline.Id, pipeline.Name, pipeline.ProjectId, project?.Name, project?.OrganizationId, pipeline.YamlDefinition);
        })];
    }

    public async Task<List<UsageAnalyticsAppRow>> GetAnalyticsAppsAsync(CancellationToken ct = default) =>
        await db.MonitoredApps
            .AsNoTracking()
            .Where(app => app.AnalyticsVaultName != null && app.AnalyticsVaultName != "")
            .OrderBy(app => app.Id)
            .Select(app => new UsageAnalyticsAppRow(app.Id, app.Name, app.ProjectId, app.AnalyticsVaultName!))
            .ToListAsync(ct).ConfigureAwait(false);
}
