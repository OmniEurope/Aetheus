// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AgentPools;

/// <summary>
/// The agent pools. R-12 (decision 7 of 2026-10-28, every resource belongs to an organization): a pool is
/// owned by one organization and holds only that organization's servers.
/// </summary>
public class AgentPoolService(
    IAgentPoolRepository repo,
    IAuditService audit,
    IEntityChangeNotifier notifier,
    TimeProvider timeProvider) : IAgentPoolService
{
    public async Task<PaginatedResult<AgentPoolDto>> GetPoolsAsync(PaginationRequest request, List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetPoolsPagedAsync(
            request.Search, page, pageSize, accessibleIds, ct).ConfigureAwait(false);

        return new PaginatedResult<AgentPoolDto>
        {
            Items = items.Select(MapToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<AgentPoolDto?> GetPoolAsync(int id, CancellationToken ct = default)
    {
        var pool = await repo.GetPoolWithServersAsync(id, ct).ConfigureAwait(false);
        return pool is null ? null : MapToDto(pool);
    }

    public async Task<AgentPoolDto> CreatePoolAsync(CreateAgentPoolRequest request, CancellationToken ct = default)
    {
        var organizationId = request.OrganizationId
            ?? await repo.GetDefaultOrganizationIdAsync(ct).ConfigureAwait(false)
            ?? throw new BadRequestException("No organization exists yet: create one before an agent pool.");
        await EnsureValidAsync(request, organizationId, exceptPoolId: null, ct).ConfigureAwait(false);

        var pool = new AgentPool
        {
            Name = request.Name,
            Description = request.Description,
            MaxConcurrency = request.MaxConcurrency,
            OrganizationId = organizationId,
            Servers = request.ServerIds.Distinct().Select(sid => new AgentPoolServer { ServerId = sid }).ToList()
        };

        await repo.AddPoolAsync(pool, ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "AgentPool", pool.Id, pool.Name, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.AgentPool, pool.Id, EntityChangeOps.Created, ct, organizationId).ConfigureAwait(false);

        var result = await repo.GetPoolWithServersAsync(pool.Id, ct).ConfigureAwait(false);
        return MapToDto(result!);
    }

    public async Task<AgentPoolDto?> UpdatePoolAsync(int id, UpdateAgentPoolRequest request, CancellationToken ct = default)
    {
        var pool = await repo.FindPoolAsync(id, ct).ConfigureAwait(false);
        if (pool is null) return null;
        await EnsureValidAsync(request, pool.OrganizationId, exceptPoolId: id, ct).ConfigureAwait(false);

        pool.Name = request.Name;
        pool.Description = request.Description;
        pool.MaxConcurrency = request.MaxConcurrency;
        pool.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        pool.Servers.Clear();
        pool.Servers.AddRange(request.ServerIds.Distinct().Select(sid => new AgentPoolServer { AgentPoolId = id, ServerId = sid }));

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "AgentPool", pool.Id, pool.Name, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.AgentPool, pool.Id, EntityChangeOps.Updated, ct, pool.OrganizationId).ConfigureAwait(false);

        var result = await repo.GetPoolWithServersAsync(id, ct).ConfigureAwait(false);
        return MapToDto(result!);
    }

    public async Task<bool> DeletePoolAsync(int id, CancellationToken ct = default)
    {
        var pool = await repo.FindPoolAsync(id, ct).ConfigureAwait(false);
        if (pool is null) return false;

        var name = pool.Name;
        var organizationId = pool.OrganizationId;
        await repo.RemovePoolAsync(pool, ct).ConfigureAwait(false);
        await audit.LogAsync("Deleted", "AgentPool", id, name, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.AgentPool, id, EntityChangeOps.Deleted, ct, organizationId).ConfigureAwait(false);
        return true;
    }

    /// <summary>The name is free (it is unique on the platform, so the answer never says which organization holds
    /// it), and every server belongs to the pool's organization: a pool never spans two organizations.</summary>
    private async Task EnsureValidAsync(AgentPoolMutationRequest request, int organizationId, int? exceptPoolId, CancellationToken ct)
    {
        if (await repo.NameTakenAsync(request.Name, exceptPoolId, ct).ConfigureAwait(false))
            throw new ConflictException($"An agent pool named '{request.Name}' already exists.");
        if (!await repo.AllServersInOrganizationAsync(organizationId, request.ServerIds, ct).ConfigureAwait(false))
            throw new BadRequestException("Every server of an agent pool must belong to the pool's organization.");
    }

    // A pool shows only the servers of its own organization: a link left from before R-12 to a server of another
    // organization is never shown (nor used: the pipeline resolver keeps the run's organization).
    private static AgentPoolDto MapToDto(AgentPool p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Description = p.Description,
        MaxConcurrency = p.MaxConcurrency,
        OrganizationId = p.OrganizationId,
        Servers = p.Servers
            .Where(ps => ps.Server is not null && ps.Server.OrganizationId == p.OrganizationId)
            .Select(ps => new AgentPoolServerDto
            {
                ServerId = ps.ServerId,
                ServerName = ps.Server.Name,
                ServerStatus = ps.Server.Status
            }).ToList(),
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt
    };
}
