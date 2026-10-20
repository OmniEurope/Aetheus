// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Teamspeak;

/// <summary>TeamSpeak server administration.</summary>
public sealed class TeamspeakApi(HttpClient http) : ApiClientBase(http)
{
    public async Task<TeamspeakDataDto> GetTeamspeakStateAsync(int serverId, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<TeamspeakDataDto>($"api/servers/{serverId}/teamspeak", JsonOptions.Web, ct) ?? new();
    }


    public Task<PaginatedResult<TeamspeakClientDto>> GetTeamspeakClientsAsync(
        int serverId, int page, int pageSize, string? search = null, string? sortBy = null,
        bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null) =>
        GetTeamspeakPageAsync<TeamspeakClientDto>(
            $"api/servers/{serverId}/teamspeak/clients", page, pageSize, search, sortBy, sortDescending, filters, ct);


    public Task<PaginatedResult<TeamspeakChannelDto>> GetTeamspeakChannelsAsync(
        int serverId, int page, int pageSize, string? search = null, string? sortBy = null,
        bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null) =>
        GetTeamspeakPageAsync<TeamspeakChannelDto>(
            $"api/servers/{serverId}/teamspeak/channels", page, pageSize, search, sortBy, sortDescending, filters, ct);


    public Task<PaginatedResult<TeamspeakBanDto>> GetTeamspeakBansAsync(
        int serverId, int page, int pageSize, string? search = null, string? sortBy = null,
        bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null) =>
        GetTeamspeakPageAsync<TeamspeakBanDto>(
            $"api/servers/{serverId}/teamspeak/bans", page, pageSize, search, sortBy, sortDescending, filters, ct);


    /// <summary>The platforms the clients grid's Platform filter offers.</summary>
    public async Task<TeamspeakFilterValuesDto> GetTeamspeakFilterValuesAsync(int serverId, CancellationToken ct = default) =>
        await GetJsonAsync<TeamspeakFilterValuesDto>($"api/servers/{serverId}/teamspeak/filter-values", ct).ConfigureAwait(false) ?? new();


    private async Task<PaginatedResult<T>> GetTeamspeakPageAsync<T>(
        string path, int page, int pageSize, string? search, string? sortBy,
        bool sortDescending, IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters, CancellationToken ct)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        // The grid's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<T>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString(path, query), filters), JsonOptions.Web, ct).ConfigureAwait(false) ?? new();
    }


    public Task<ApiStatus> ExecuteTeamspeakActionAsync(int serverId, TeamspeakActionRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<TeamspeakActionRequest>($"api/servers/{serverId}/teamspeak/action", request, ct);


    public Task<ApiStatus> SetupTeamspeakAsync(int serverId, TeamspeakSetupRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<TeamspeakSetupRequest>($"api/servers/{serverId}/teamspeak/setup", request, ct);


    public Task<ApiStatus> GetTeamspeakLogsAsync(int serverId, TeamspeakLogRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<TeamspeakLogRequest>($"api/servers/{serverId}/teamspeak/logs", request, ct);


    public Task<ApiStatus> KickTeamspeakClientAsync(int serverId, TeamspeakKickRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<TeamspeakKickRequest>($"api/servers/{serverId}/teamspeak/kick", request, ct);


    public Task<ApiStatus> BanTeamspeakClientAsync(int serverId, TeamspeakBanRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<TeamspeakBanRequest>($"api/servers/{serverId}/teamspeak/ban", request, ct);


    // Item #9 tier-1
    public Task<ApiStatus> MoveTeamspeakClientAsync(int serverId, TeamspeakMoveClientRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<TeamspeakMoveClientRequest>($"api/servers/{serverId}/teamspeak/move-client", request, ct);


    public Task<ApiStatus> PokeTeamspeakClientAsync(int serverId, TeamspeakPokeClientRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<TeamspeakPokeClientRequest>($"api/servers/{serverId}/teamspeak/poke", request, ct);


    public Task<ApiStatus> UnbanTeamspeakClientAsync(int serverId, int banId, CancellationToken ct = default)
        => DeleteAsync($"api/servers/{serverId}/teamspeak/bans/{banId}", ct);


    public Task<ApiStatus> CreateTeamspeakChannelAsync(int serverId, TeamspeakCreateChannelRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<TeamspeakCreateChannelRequest>($"api/servers/{serverId}/teamspeak/channels", request, ct);


    public Task<ApiStatus> EditTeamspeakChannelAsync(int serverId, int channelId, TeamspeakEditChannelRequest request, CancellationToken ct = default)
        => PutJsonNoBodyAsync<TeamspeakEditChannelRequest>($"api/servers/{serverId}/teamspeak/channels/{channelId}", request, ct);


    public Task<ApiStatus> DeleteTeamspeakChannelAsync(int serverId, int channelId, CancellationToken ct = default)
        => DeleteAsync($"api/servers/{serverId}/teamspeak/channels/{channelId}", ct);


    public Task<ApiStatus> EditTeamspeakServerAsync(int serverId, TeamspeakServerEditRequest request, CancellationToken ct = default)
        => PutJsonNoBodyAsync<TeamspeakServerEditRequest>($"api/servers/{serverId}/teamspeak/server", request, ct);


    public Task<ApiStatus> SendTeamspeakGlobalMessageAsync(int serverId, TeamspeakGlobalMessageRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<TeamspeakGlobalMessageRequest>($"api/servers/{serverId}/teamspeak/message", request, ct);


    public Task<ApiStatus> GetTeamspeakClientInfoAsync(int serverId, TeamspeakClientInfoRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<TeamspeakClientInfoRequest>($"api/servers/{serverId}/teamspeak/clientinfo", request, ct);


    public Task<ApiStatus> TeamspeakGracefulRestartAsync(int serverId, TeamspeakGracefulRestartRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<TeamspeakGracefulRestartRequest>($"api/servers/{serverId}/teamspeak/graceful-restart", request, ct);


    public Task<ApiStatus> CreateTeamspeakSnapshotAsync(int serverId, TeamspeakSnapshotCreateRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<TeamspeakSnapshotCreateRequest>($"api/servers/{serverId}/teamspeak/snapshots", request, ct);


    public Task<ApiStatus> DeployTeamspeakSnapshotAsync(int serverId, TeamspeakSnapshotDeployRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<TeamspeakSnapshotDeployRequest>($"api/servers/{serverId}/teamspeak/snapshots/deploy", request, ct);


    public async Task<ApiStatus> ListTeamspeakServerGroupsAsync(int serverId, CancellationToken ct = default)
    {
        var response = await Http.GetAsync($"api/servers/{serverId}/teamspeak/server-groups", ct);
        return ApiStatus.From(response);
    }


    public Task<ApiStatus> AddTeamspeakServerGroupClientAsync(int serverId, TeamspeakServerGroupAddRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<TeamspeakServerGroupAddRequest>($"api/servers/{serverId}/teamspeak/server-groups/add", request, ct);


    public Task<ApiStatus> RemoveTeamspeakServerGroupClientAsync(int serverId, TeamspeakServerGroupRemoveRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<TeamspeakServerGroupRemoveRequest>($"api/servers/{serverId}/teamspeak/server-groups/remove", request, ct);


    public async Task<ApiStatus> ListTeamspeakTokensAsync(int serverId, CancellationToken ct = default)
    {
        var response = await Http.GetAsync($"api/servers/{serverId}/teamspeak/tokens", ct);
        return ApiStatus.From(response);
    }


    public Task<ApiStatus> CreateTeamspeakTokenAsync(int serverId, TeamspeakTokenCreateRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<TeamspeakTokenCreateRequest>($"api/servers/{serverId}/teamspeak/tokens", request, ct);


    public async Task<ApiStatus> DeleteTeamspeakTokenAsync(int serverId, TeamspeakTokenDeleteRequest request, CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Delete, $"api/servers/{serverId}/teamspeak/tokens")
        {
            Content = JsonContent.Create(request, options: JsonOptions.Web)
        };
        var response = await Http.SendAsync(msg, ct);
        return ApiStatus.From(response);
    }


    public async Task<ApiStatus> GetTeamspeakServerInfoAsync(int serverId, CancellationToken ct = default)
    {
        var response = await Http.GetAsync($"api/servers/{serverId}/teamspeak/server-info", ct);
        return ApiStatus.From(response);
    }


    public async Task<ApiStatus> ListTeamspeakComplaintsAsync(int serverId, CancellationToken ct = default)
    {
        var response = await Http.GetAsync($"api/servers/{serverId}/teamspeak/complaints", ct);
        return ApiStatus.From(response);
    }


    public async Task<ApiStatus> DeleteTeamspeakComplaintAsync(int serverId, TeamspeakComplaintDeleteRequest request, CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Delete, $"api/servers/{serverId}/teamspeak/complaints")
        {
            Content = JsonContent.Create(request, options: JsonOptions.Web)
        };
        var response = await Http.SendAsync(msg, ct);
        return ApiStatus.From(response);
    }
}
