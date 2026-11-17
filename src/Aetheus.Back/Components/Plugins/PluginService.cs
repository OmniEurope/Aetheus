// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Plugins;

public class PluginService(IPluginRepository repo, IAuditService audit, IOrganizationRepository orgRepo, TimeProvider timeProvider, IAdminChangeNotifier notifier) : IPluginService
{
    public async Task<List<PluginRegistrationDto>> GetPluginsAsync(CancellationToken ct = default)
    {
        var plugins = await repo.GetAllAsync(ct).ConfigureAwait(false);
        return plugins.Select(MapToDto).ToList();
    }

    public async Task<PluginFilterValuesDto> GetFilterValuesAsync(CancellationToken ct = default) =>
        new() { Authors = await repo.GetAuthorsAsync(ct).ConfigureAwait(false) };

    public async Task<PaginatedResult<PluginRegistrationDto>> GetPluginsPageAsync(
        PaginationRequest request, CancellationToken ct = default, int? organizationId = null)
    {
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetPageAsync(
            request.Search, request.SortBy, request.SortDescending,
            page, pageSize, ct, request.Filters, organizationId).ConfigureAwait(false);
        return new PaginatedResult<PluginRegistrationDto>
        {
            Items = items.Select(MapToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PluginRegistrationDto?> GetPluginAsync(int id, CancellationToken ct = default)
    {
        var entity = await repo.FindAsync(id, ct).ConfigureAwait(false);
        return entity is null ? null : MapToDto(entity);
    }

    public async Task<PluginRegistrationDto> RegisterPluginAsync(RegisterPluginRequest request, CancellationToken ct = default)
    {
        var orgId = request.OrganizationId
            ?? await orgRepo.GetDefaultOrganizationIdAsync(ct).ConfigureAwait(false)
            ?? throw new BadRequestException("No organization available; create an organization first.");
        var entity = new PluginRegistration
        {
            Name = request.Name,
            Description = request.Description,
            Version = request.Version,
            Author = request.Author,
            Type = request.Type,
            EntryPoint = request.EntryPoint,
            ConfigurationJson = request.ConfigurationJson,
            OrganizationId = orgId
        };

        await repo.AddAsync(entity, ct).ConfigureAwait(false);
        await audit.LogAsync("Registered", "Plugin", entity.Id, $"{request.Name} v{request.Version}", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.Plugin, entity.Id, EntityChangeOps.Created, ct).ConfigureAwait(false);
        return MapToDto(entity);
    }

    public async Task<PluginRegistrationDto?> UpdatePluginAsync(int id, UpdatePluginRequest request, CancellationToken ct = default)
    {
        var entity = await repo.FindAsync(id, ct).ConfigureAwait(false);
        if (entity is null) return null;

        entity.Description = request.Description;
        entity.Status = request.Status;
        entity.EntryPoint = request.EntryPoint;
        entity.ConfigurationJson = request.ConfigurationJson;
        entity.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "Plugin", id, $"Status: {request.Status}", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.Plugin, id, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        return MapToDto(entity);
    }

    public async Task<bool> UnregisterPluginAsync(int id, CancellationToken ct = default)
    {
        var entity = await repo.FindAsync(id, ct).ConfigureAwait(false);
        if (entity is null) return false;

        await repo.RemoveAsync(entity, ct).ConfigureAwait(false);
        await audit.LogAsync("Unregistered", "Plugin", id, null, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(AdminEntities.Plugin, id, EntityChangeOps.Deleted, ct).ConfigureAwait(false);
        return true;
    }

    private static PluginRegistrationDto MapToDto(PluginRegistration p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Description = p.Description,
        Version = p.Version,
        Author = p.Author,
        Type = p.Type,
        Status = p.Status,
        EntryPoint = p.EntryPoint,
        ConfigurationJson = p.ConfigurationJson,
        OrganizationId = p.OrganizationId,
        CreatedAt = p.CreatedAt
    };
}
