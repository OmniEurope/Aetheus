// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Shared;

/// <summary>Sign-in, the liveness probe the login screen uses, registration and personal-access tokens, TOTP enrolment, users and roles.</summary>
public sealed class AuthApi(HttpClient http) : ApiClientBase(http)
{
    // D6KR: anonymous lightweight liveness probe (returns 200 once the process is up). Used by the
    // login screen to surface backend reachability before the user even attempts to sign in.
    public async Task<bool> IsBackendLiveAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync("health/live", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
        // The resilience pipeline reports its own timeout and an open circuit this way. Thrown out of
        // here, they ended the layout's fire-and-forget refresh and left the connection-lost overlay
        // on the last answer that did return: offline.
        catch (Polly.ExecutionRejectedException) { return false; }
    }

    public Task<ApiOutcome<LoginResponse, ApiError>> LoginAsync(LoginRequest request, CancellationToken ct = default)
        => PostForApiErrorOutcomeAsync<LoginRequest, LoginResponse>("api/auth/login", request, ct);


    public Task<PublicDemoInfoDto?> GetPublicDemoInfoAsync(CancellationToken ct = default)
        => GetJsonAsync<PublicDemoInfoDto>("api/auth/public-demo", ct);

    /// <summary> Tells the server the user logs out, while the token is still held, so it forgets
    /// what it keeps in memory for them (the typed mailbox passwords). Best effort: false when the
    /// server could not be told, and the caller logs out anyway. </summary>
    public async Task<bool> NotifyLogoutAsync(CancellationToken ct = default)
    {
        try
        {
            return (await PostNoBodyAsync("api/auth/logout", ct).ConfigureAwait(false)).Success;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
        catch (Polly.ExecutionRejectedException) { return false; }
    }

    /// <summary>Reports why the client ended a session through an anonymous endpoint.</summary>
    public Task<ApiStatus> ReportSessionEndedAsync(SessionEndedReport report, CancellationToken ct = default)
        => PostJsonNoBodyAsync("api/auth/session-ended", report, ct);


    public async Task<List<RegistrationTokenDto>> GetRegistrationTokensAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<RegistrationTokenDto>>("api/auth/registration-tokens", ct).ConfigureAwait(false) ?? [];


    // RTOK: single-token read for the wizard verify-step poll - avoids re-fetching the full list every 3 s.
    public Task<RegistrationTokenDto?> GetRegistrationTokenAsync(int id, CancellationToken ct = default)
        => GetJsonAsync<RegistrationTokenDto>($"api/auth/registration-tokens/{id}", ct);


    public Task<RegistrationTokenDto?> CreateRegistrationTokenAsync(int expirationHours = 24, CancellationToken ct = default)
        => PostJsonAsync<CreateRegistrationTokenRequest, RegistrationTokenDto>(
            "api/auth/registration-tokens",
            new CreateRegistrationTokenRequest { ExpirationHours = expirationHours },

            ct);

    public async Task<PaginatedResult<PersonalAccessTokenDto>> GetPersonalAccessTokensAsync(
        int page, int pageSize, string? search = null, string? sortBy = null,
        bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        // The tokens grid's column header filters.
        return await GetJsonAsync<PaginatedResult<PersonalAccessTokenDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString("api/personal-access-tokens", query), filters), ct).ConfigureAwait(false) ?? new();
    }


    public Task<CreatedPersonalAccessTokenDto?> CreatePersonalAccessTokenAsync(CreatePersonalAccessTokenRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreatePersonalAccessTokenRequest, CreatedPersonalAccessTokenDto>("api/personal-access-tokens", request, ct);


    public Task<ApiStatus> RevokePersonalAccessTokenAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/personal-access-tokens/{id}", ct);

    public async Task<PaginatedResult<UserDto>> GetUsersAsync(
        int page = 1, int pageSize = 25, string? search = null,
        string? sortBy = null, bool sortDescending = false,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null,
        CancellationToken cancellationToken = default)
    {
        var query = PageQuery(page, pageSize, search, sortBy, sortDescending);
        // The list's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<UserDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString("api/users", query), filters), JsonOptions.Web, cancellationToken) ?? new();
    }


    public async Task<UserDto?> GetUserDetailAsync(int id, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<UserDto>($"api/users/{id}", JsonOptions.Web, ct);
    }


    public async Task<UserDto?> GetCurrentUserAsync(CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<UserDto>("api/users/me", JsonOptions.Web, ct);
    }


    public Task<ApiOutcome<UserDto, ApiError>> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default)
        => PostForApiErrorOutcomeAsync<CreateUserRequest, UserDto>("api/users", request, ct);


    public Task<UserDto?> UpdateUserAsync(int id, UpdateUserRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateUserRequest, UserDto>($"api/users/{id}", request, ct);


    public Task<ApiStatus> DeleteUserAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/users/{id}", ct);

    /// <summary>Deletes a pipeline owner, handing the pipelines to <paramref
    /// name="transferPipelinesTo"/> first, or leaving them ownerless when <paramref name="force"/>
    /// is set.</summary>
    public Task<ApiStatus> DeleteUserAsync(int id, string? transferPipelinesTo, bool force, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>();
        if (!string.IsNullOrWhiteSpace(transferPipelinesTo)) query["transferPipelinesTo"] = transferPipelinesTo;
        if (force) query["force"] = "true";
        return DeleteAsync(QueryHelpers.AddQueryString($"api/users/{id}", query), ct);
    }


    public Task<ApiStatus> ChangeUserPasswordAsync(int id, ChangeUserPasswordRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<ChangeUserPasswordRequest>($"api/users/{id}/change-password", request, ct);


    public Task<ApiStatus> ChangeOwnPasswordAsync(ChangeUserPasswordRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<ChangeUserPasswordRequest>("api/users/me/change-password", request, ct);


    public async Task<List<string>> GetRolesAsync()
    {
        return await Http.GetFromJsonAsync<List<string>>("api/users/roles", JsonOptions.Web) ?? [];
    }


    public async Task<PaginatedResult<RoleDto>> GetRoleDtosAsync(
        int page, int pageSize, string? search = null, string? sortBy = null, bool sortDescending = false,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        // The role grids' column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<RoleDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString("api/roles", query), filters), JsonOptions.Web) ?? new();
    }


    public async Task<RoleDto?> GetRoleAsync(int id, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<RoleDto>($"api/roles/{id}", JsonOptions.Web, ct);
    }


    public Task<RoleDto?> CreateRoleAsync(CreateRoleRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateRoleRequest, RoleDto>("api/roles", request, ct);


    public Task<RoleDto?> UpdateRoleAsync(int id, UpdateRoleRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateRoleRequest, RoleDto>($"api/roles/{id}", request, ct);


    public Task<ApiStatus> DeleteRoleAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/roles/{id}", ct);


    public async Task<List<ResourcePermissionDto>> GetRolePermissionsAsync(int roleId)
    {
        return await Http.GetFromJsonAsync<List<ResourcePermissionDto>>($"api/roles/{roleId}/permissions", JsonOptions.Web) ?? [];
    }


    public Task<ApiStatus> SetRolePermissionsAsync(int roleId, SetResourcePermissionsRequest request, CancellationToken ct = default)
        => PutJsonNoBodyAsync<SetResourcePermissionsRequest>($"api/roles/{roleId}/permissions", request, ct);


    public Task<RoleDto?> CloneRoleAsync(int roleId, CancellationToken ct = default)
        => PostNoBodyAsync<RoleDto>($"api/roles/{roleId}/clone", ct);


    public async Task<PaginatedResult<RoleUserDto>> GetRoleUsersAsync(
        int roleId, int page, int pageSize, string? search = null, string? sortBy = null,
        bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        // The role's users grid column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<RoleUserDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString($"api/roles/{roleId}/users", query), filters), JsonOptions.Web, ct) ?? new();
    }


    public async Task<PaginatedResult<RoleUserDto>> GetUsersAvailableForRoleAsync(
        int roleId, int page, int pageSize, string? search = null, string? sortBy = null,
        bool sortDescending = false, CancellationToken ct = default)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        return await Http.GetFromJsonAsync<PaginatedResult<RoleUserDto>>(
            QueryHelpers.AddQueryString($"api/roles/{roleId}/available-users", query), JsonOptions.Web, ct) ?? new();
    }


    public Task<ApiStatus> AddUserToRoleAsync(int roleId, int userId, CancellationToken ct = default)
        => PostJsonNoBodyAsync<AddUserToRoleRequest>($"api/roles/{roleId}/users", new AddUserToRoleRequest { UserId = userId }, ct);


    public Task<ApiStatus> RemoveUserFromRoleAsync(int roleId, int userId, CancellationToken ct = default)
        => DeleteAsync($"api/roles/{roleId}/users/{userId}", ct);


    public async Task<UserPermissionSummaryDto?> GetUserEffectivePermissionsAsync(int userId, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<UserPermissionSummaryDto>($"api/users/{userId}/effective-permissions", JsonOptions.Web, ct);
    }


    public async Task<UserPermissionSummaryDto?> GetMyPermissionsAsync(CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<UserPermissionSummaryDto>("api/users/me/permissions", JsonOptions.Web, ct);
    }

    // Return the enqueued task id (null on failure) so the caller can correlate the resulting
    // TaskCompleted notification by id instead of by task-name suffix (which collides across
    // concurrent operations on the same service).
    public async Task<int?> ExecuteServiceActionAsync(int serverId, ServiceActionRequest request, CancellationToken ct = default)
        => (await PostJsonAsync<ServiceActionRequest, ServiceTaskResponse>($"api/servers/{serverId}/services/action", request, ct).ConfigureAwait(false))?.TaskId;


    public async Task<int?> InstallServiceAsync(int serverId, string serviceName, CancellationToken ct = default)
        => (await PostJsonAsync<ServiceInstallRequest, ServiceTaskResponse>($"api/servers/{serverId}/services/install", new ServiceInstallRequest { ServiceName = serviceName }, ct).ConfigureAwait(false))?.TaskId;


    public async Task<int?> UninstallServiceAsync(int serverId, string serviceName, CancellationToken ct = default)
        => (await PostJsonAsync<ServiceInstallRequest, ServiceTaskResponse>($"api/servers/{serverId}/services/uninstall", new ServiceInstallRequest { ServiceName = serviceName }, ct).ConfigureAwait(false))?.TaskId;

    /// <summary>Recette R3-59: queues the read-only list of every package installed on the server.</summary>
    public async Task<int?> ListInstalledPackagesAsync(int serverId, CancellationToken ct = default)
        => (await PostNoBodyAsync<ServiceTaskResponse>($"api/servers/{serverId}/packages/inventory", ct).ConfigureAwait(false))?.TaskId;

    public async Task<TotpSetupResponse?> SetupTotpAsync(CancellationToken ct = default)
        => await PostNoBodyAsync<TotpSetupResponse>("api/auth/totp/setup", ct).ConfigureAwait(false);


    public async Task<bool> VerifyTotpAsync(string code, CancellationToken ct = default)
    {
        var status = await PostJsonNoBodyAsync("api/auth/totp/verify", new TotpVerifyRequest { Code = code }, ct).ConfigureAwait(false);
        return status.Success;
    }


    public async Task<bool> DisableTotpAsync(string password, CancellationToken ct = default)
    {
        var status = await PostJsonNoBodyAsync("api/auth/totp/disable", new TotpDisableRequest { Password = password }, ct).ConfigureAwait(false);
        return status.Success;
    }
}
