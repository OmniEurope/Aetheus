// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Reads and rewrites the automated-run owner of pipelines.</summary>
public interface IPipelineOwnershipRepository
{
    /// <summary>The pipelines of one scope, tracked so their owner can be changed: exactly one of the
    /// three arguments is set by the caller.</summary>
    Task<List<Pipeline>> GetPipelinesInScopeAsync(
        int? pipelineId, int? projectId, string? ownerUsername, CancellationToken ct = default);

    Task<List<PipelineOwnershipDto>> GetOwnedPipelinesAsync(string ownerUsername, CancellationToken ct = default);

    /// <summary>The stored username of the active user named <paramref name="username"/>, or null.</summary>
    Task<string?> FindActiveUsernameAsync(string username, CancellationToken ct = default);

    Task<List<PipelineOwnerCandidateDto>> GetActiveUsersAsync(CancellationToken ct = default);

    Task<Dictionary<int, string>> GetServerNamesAsync(IReadOnlyCollection<int> serverIds, CancellationToken ct = default);

    Task<Dictionary<int, string>> GetProjectNamesAsync(IReadOnlyCollection<int> projectIds, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IPipelineOwnershipRepository"/>
public sealed class PipelineOwnershipRepository(AppDbContext db) : IPipelineOwnershipRepository
{
    public async Task<List<Pipeline>> GetPipelinesInScopeAsync(
        int? pipelineId, int? projectId, string? ownerUsername, CancellationToken ct = default)
    {
        var query = db.Pipelines.AsQueryable();
        if (pipelineId is { } id)
            query = query.Where(pipeline => pipeline.Id == id);
        else if (projectId is { } project)
            query = query.Where(pipeline => pipeline.ProjectId == project);
        else
            query = query.Where(pipeline => pipeline.CreatedByUsername == ownerUsername);
        return await query.OrderBy(pipeline => pipeline.Name).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<PipelineOwnershipDto>> GetOwnedPipelinesAsync(
        string ownerUsername, CancellationToken ct = default) =>
        await db.Pipelines.AsNoTracking()
            .Where(pipeline => pipeline.CreatedByUsername == ownerUsername)
            .OrderBy(pipeline => pipeline.Name)
            .Select(pipeline => new PipelineOwnershipDto
            {
                PipelineId = pipeline.Id,
                PipelineName = pipeline.Name,
                ProjectId = pipeline.ProjectId,
                ProjectName = pipeline.Project != null ? pipeline.Project.Name : null,
                OwnerUsername = pipeline.CreatedByUsername,
                OwnerInactive = !db.Users.Any(user => user.Username == pipeline.CreatedByUsername && user.IsActive)
            })
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<string?> FindActiveUsernameAsync(string username, CancellationToken ct = default) =>
        await db.Users.AsNoTracking()
            .Where(user => user.Username == username && user.IsActive)
            .Select(user => user.Username)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public async Task<List<PipelineOwnerCandidateDto>> GetActiveUsersAsync(CancellationToken ct = default) =>
        await db.Users.AsNoTracking()
            .Where(user => user.IsActive)
            .OrderBy(user => user.Username)
            .Select(user => new PipelineOwnerCandidateDto(user.Id, user.Username))
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<Dictionary<int, string>> GetServerNamesAsync(
        IReadOnlyCollection<int> serverIds, CancellationToken ct = default) =>
        await db.Servers.AsNoTracking()
            .Where(server => serverIds.Contains(server.Id))
            .ToDictionaryAsync(server => server.Id, server => server.Name, ct).ConfigureAwait(false);

    public async Task<Dictionary<int, string>> GetProjectNamesAsync(
        IReadOnlyCollection<int> projectIds, CancellationToken ct = default) =>
        await db.Projects.AsNoTracking()
            .Where(project => projectIds.Contains(project.Id))
            .ToDictionaryAsync(project => project.Id, project => project.Name, ct).ConfigureAwait(false);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
