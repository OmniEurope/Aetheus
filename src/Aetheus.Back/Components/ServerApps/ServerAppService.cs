// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.ServerApps;

/// <summary>Announces each application's port on writes so the port registry can enforce
/// reservations and answer availability checks.</summary>
public class ServerAppService(
    IServerAppRepository repo,
    IAuditService audit,
    TimeProvider timeProvider,
    Services.DomainEvents.IDomainEventDispatcher domainEvents) : IServerAppService
{
    public async Task<List<ServerAppDto>> GetByServerIdAsync(int serverId, CancellationToken ct = default)
    {
        var apps = await repo.GetByServerIdAsync(serverId, ct).ConfigureAwait(false);
        return apps.Select(MapToDto).ToList();
    }

    public async Task<PaginatedResult<ServerAppDto>> GetPageAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetPageAsync(
            serverId, request.Search, request.SortBy, request.SortDescending,
            page, pageSize, ct, request.Filters).ConfigureAwait(false);
        return new PaginatedResult<ServerAppDto>
        {
            Items = items.Select(MapToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>What the applications grid's checkable Source filter offers.</summary>
    public async Task<ServerAppFilterValuesDto> GetFilterValuesAsync(int serverId, CancellationToken ct = default) =>
        new() { Sources = await repo.GetSourcesAsync(serverId, ct).ConfigureAwait(false) };

    public async Task<ServerAppDto?> GetByIdAsync(int serverId, int id, CancellationToken ct = default)
    {
        var app = await repo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (app is null || app.ServerId != serverId) return null;
        return MapToDto(app);
    }

    public async Task<ServerAppDto> CreateAsync(int serverId, CreateServerAppRequest request, CancellationToken ct = default)
    {
        var app = new ServerApp
        {
            ServerId = serverId,
            Name = request.Name,
            Version = request.Version,
            Type = request.Type,
            Port = request.Port,
            Path = request.Path,
            Source = request.Source
        };

        await repo.AddAsync(app, ct).ConfigureAwait(false);
        await SyncPortAsync(app, ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "ServerApp", app.Id, app.Name, ct).ConfigureAwait(false);
        return MapToDto(app);
    }

    public async Task<ServerAppDto?> UpdateAsync(int serverId, int id, UpdateServerAppRequest request, CancellationToken ct = default)
    {
        var app = await repo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (app is null || app.ServerId != serverId) return null;

        app.Name = request.Name;
        app.Version = request.Version;
        app.Status = request.Status;
        app.Port = request.Port;
        app.Path = request.Path;
        app.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await SyncPortAsync(app, ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "ServerApp", app.Id, app.Name, ct).ConfigureAwait(false);
        return MapToDto(app);
    }

    public async Task<bool> DeleteAsync(int serverId, int id, CancellationToken ct = default)
    {
        var app = await repo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (app is null || app.ServerId != serverId) return false;

        var name = app.Name;
        // Released before the row goes: the registry must not keep defending a port for an app that
        // no longer exists.
        await domainEvents.DispatchStrictAsync(
            new Events.ServerAppPortChangedEvent(app.Id, null, null, name), ct).ConfigureAwait(false);
        await repo.RemoveAsync(app, ct).ConfigureAwait(false);
        await audit.LogAsync("Deleted", "ServerApp", id, name, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Announced rather than written here: the registry lives on the same layer as this module, so the
    /// side that owns "a port belongs to somebody" listens from above. Dispatched strictly, so a
    /// refusal reaches the caller instead of leaving an app claiming a port the registry says is taken.
    /// </summary>
    private Task SyncPortAsync(ServerApp app, CancellationToken ct) =>
        domainEvents.DispatchStrictAsync(
            new Events.ServerAppPortChangedEvent(app.Id, app.ServerId, app.Port, app.Name), ct);

    private static ServerAppDto MapToDto(ServerApp a) => new()
    {
        Id = a.Id,
        ServerId = a.ServerId,
        Name = a.Name,
        Version = a.Version,
        Type = a.Type,
        Status = a.Status,
        Port = a.Port,
        Path = a.Path,
        Source = a.Source,
        InstalledAt = a.InstalledAt,
        UpdatedAt = a.UpdatedAt
    };
}
