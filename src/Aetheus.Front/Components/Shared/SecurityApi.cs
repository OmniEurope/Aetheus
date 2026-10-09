// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Shared;

/// <summary>The security surface of a server: PortSentry, RKHunter, the firewall, fleet patching and application backups.</summary>
public sealed class SecurityApi(HttpClient http) : ApiClientBase(http)
{
    public async Task<PortsentryDataDto> GetPortsentryStateAsync(int serverId, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<PortsentryDataDto>($"api/servers/{serverId}/portsentry", JsonOptions.Web, ct) ?? new();
    }


    public Task<ApiStatus> ExecutePortsentryActionAsync(int serverId, PortsentryActionRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<PortsentryActionRequest>($"api/servers/{serverId}/portsentry/action", request, ct);


    public Task<ApiStatus> SetupPortsentryAsync(int serverId, PortsentrySetupRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<PortsentrySetupRequest>($"api/servers/{serverId}/portsentry/setup", request, ct);


    public Task<ApiStatus> GetPortsentryLogsAsync(int serverId, PortsentryLogRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<PortsentryLogRequest>($"api/servers/{serverId}/portsentry/logs", request, ct);


    public Task<ApiStatus> UnblockPortsentryIpAsync(int serverId, PortsentryUnblockRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<PortsentryUnblockRequest>($"api/servers/{serverId}/portsentry/unblock", request, ct);


    public async Task<ApiStatus> GetPortsentryStatusAsync(int serverId, CancellationToken ct = default)
    {
        var response = await Http.GetAsync($"api/servers/{serverId}/portsentry/status", ct);
        return ApiStatus.From(response);
    }


    public async Task<PaginatedResult<PortsentryBlockedIpDto>> GetPortsentryBlockedIpsAsync(
        int serverId, int page, int pageSize, string? search = null, string? sortBy = null, bool sortDescending = false,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        // Recette R-210 / R-224: the grid's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<PortsentryBlockedIpDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString($"api/servers/{serverId}/portsentry/blocked", query), filters), JsonOptions.Web) ?? new();
    }


    /// <summary>Recette R-210: the protocols the blocked IPs grid's Protocol filter offers.</summary>
    public async Task<PortsentryFilterValuesDto> GetPortsentryFilterValuesAsync(int serverId, CancellationToken ct = default) =>
        await GetJsonAsync<PortsentryFilterValuesDto>($"api/servers/{serverId}/portsentry/filter-values", ct).ConfigureAwait(false) ?? new();


    public async Task<PaginatedResult<PortsentryWhitelistIpDto>> GetPortsentryWhitelistPageAsync(
        int serverId, int page, int pageSize, string? search = null, string? sortBy = null, bool sortDescending = false,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        // Recette R-210 / R-224: the grid's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<PortsentryWhitelistIpDto>>(GridColumnFilters.AddTo(
            QueryHelpers.AddQueryString($"api/servers/{serverId}/portsentry/whitelist", query), filters), JsonOptions.Web) ?? new();
    }


    public async Task<List<PortsentryWhitelistIpDto>> GetPortsentryWhitelistAsync(int serverId)
    {
        const int pageSize = 100;
        var items = new List<PortsentryWhitelistIpDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetPortsentryWhitelistPageAsync(serverId, page, pageSize);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public Task<PortsentryWhitelistIpDto?> AddPortsentryWhitelistIpAsync(int serverId, AddPortsentryWhitelistRequest request, CancellationToken ct = default)
        => PostJsonAsync<AddPortsentryWhitelistRequest, PortsentryWhitelistIpDto>($"api/servers/{serverId}/portsentry/whitelist", request, ct);


    public Task<ApiStatus> RemovePortsentryWhitelistIpAsync(int serverId, int id, CancellationToken ct = default)
        => DeleteAsync($"api/servers/{serverId}/portsentry/whitelist/{id}", ct);

    public async Task<RkhunterDataDto> GetRkhunterStateAsync(int serverId, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<RkhunterDataDto>($"api/servers/{serverId}/rkhunter", JsonOptions.Web, ct) ?? new();
    }


    public Task<ApiStatus> ExecuteRkhunterActionAsync(int serverId, RkhunterActionRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<RkhunterActionRequest>($"api/servers/{serverId}/rkhunter/action", request, ct);


    public Task<ApiStatus> SetupRkhunterAsync(int serverId, RkhunterSetupRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<RkhunterSetupRequest>($"api/servers/{serverId}/rkhunter/setup", request, ct);


    public Task<ApiStatus> GetRkhunterLogsAsync(int serverId, RkhunterLogRequest request, CancellationToken ct = default)
        => PostJsonNoBodyAsync<RkhunterLogRequest>($"api/servers/{serverId}/rkhunter/logs", request, ct);


    public async Task<List<RkhunterWarningDto>> GetRkhunterWarningsAsync(int serverId, bool includeArchived = false)
    {
        return await Http.GetFromJsonAsync<List<RkhunterWarningDto>>(
            $"api/servers/{serverId}/rkhunter/warnings?includeArchived={includeArchived}", JsonOptions.Web) ?? [];
    }


    public async Task<List<RkhunterScanResultDto>> GetRkhunterScanHistoryAsync(int serverId, int limit = 50)
    {
        return await Http.GetFromJsonAsync<List<RkhunterScanResultDto>>(
            $"api/servers/{serverId}/rkhunter/history?limit={limit}", JsonOptions.Web) ?? [];
    }


    public Task<ApiStatus> SetRkhunterScheduleAsync(int serverId, RkhunterScheduleRequest request, CancellationToken ct = default)
        => PutJsonNoBodyAsync<RkhunterScheduleRequest>($"api/servers/{serverId}/rkhunter/schedule", request, ct);

    public Task<ServerSecurityUpdatesDto?> GetSecurityUpdatesAsync(int serverId, CancellationToken ct = default)
        => GetJsonAsync<ServerSecurityUpdatesDto>($"api/servers/{serverId}/security-updates", ct);


    public async Task<int?> UpgradeSystemAsync(int serverId, bool dryRun, CancellationToken ct = default)
        => (await PostJsonAsync<SystemUpgradeRequest, ServiceTaskResponse>($"api/servers/{serverId}/system/upgrade", new SystemUpgradeRequest { DryRun = dryRun }, ct).ConfigureAwait(false))?.TaskId;

    public async Task<PaginatedResult<BackupPolicyDto>> GetBackupPoliciesAsync(
        int page = 1, int pageSize = 25, string? search = null,
        string? sortBy = null, bool sortDescending = false, int? projectId = null,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null)
    {
        var query = PageQuery(page, pageSize, search, sortBy, sortDescending);
        query["projectId"] = projectId?.ToString();
        // Recette R-224: the policies grid's column header filters.
        return await GetJsonAsync<PaginatedResult<BackupPolicyDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString("api/backups", query), filters), ct).ConfigureAwait(false) ?? new();
    }


    public async Task<List<BackupPolicyDto>> GetAllBackupPoliciesAsync(CancellationToken ct = default)
    {
        const int pageSize = 100;
        var items = new List<BackupPolicyDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetBackupPoliciesAsync(page, pageSize, ct: ct).ConfigureAwait(false);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public Task<BackupPolicyDto?> CreateBackupPolicyAsync(CreateBackupPolicyRequest request, CancellationToken ct = default)
        => PostJsonAsync<CreateBackupPolicyRequest, BackupPolicyDto>("api/backups", request, ct);


    public Task<BackupPolicyDto?> UpdateBackupPolicyAsync(int id, UpdateBackupPolicyRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdateBackupPolicyRequest, BackupPolicyDto>($"api/backups/{id}", request, ct);


    public Task<ApiStatus> DeleteBackupPolicyAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/backups/{id}", ct);


    public async Task<PaginatedResult<BackupRunDto>> GetBackupRunsAsync(
        int policyId, int page = 1, int pageSize = 25, string? search = null,
        string? sortBy = null, bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null)
    {
        var query = PageQuery(page, pageSize, search, sortBy, sortDescending);
        // Recette R-224: the run grid's column header filters.
        return await GetJsonAsync<PaginatedResult<BackupRunDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString($"api/backups/{policyId}/runs", query), filters), ct).ConfigureAwait(false) ?? new();
    }


    public async Task<List<BackupRunDto>> GetAllBackupRunsAsync(
        int policyId, CancellationToken ct = default)
    {
        const int pageSize = 100;
        var items = new List<BackupRunDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetBackupRunsAsync(policyId, page, pageSize, ct: ct).ConfigureAwait(false);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public Task<ApiStatus> RunBackupNowAsync(int policyId, CancellationToken ct = default)
        => PostNoBodyAsync($"api/backups/{policyId}/run", ct);

    public Task<ServerFirewallDto?> GetFirewallAsync(int serverId, CancellationToken ct = default)
        => GetJsonAsync<ServerFirewallDto>($"api/servers/{serverId}/firewall", ct);


    public async Task<int?> FirewallAllowAsync(int serverId, FirewallRuleRequest request, CancellationToken ct = default)
        => (await PostJsonAsync<FirewallRuleRequest, ServiceTaskResponse>($"api/servers/{serverId}/firewall/allow", request, ct).ConfigureAwait(false))?.TaskId;


    public async Task<int?> FirewallDenyAsync(int serverId, FirewallRuleRequest request, CancellationToken ct = default)
        => (await PostJsonAsync<FirewallRuleRequest, ServiceTaskResponse>($"api/servers/{serverId}/firewall/deny", request, ct).ConfigureAwait(false))?.TaskId;


    public async Task<int?> FirewallDeleteRuleAsync(int serverId, FirewallRuleRequest request, CancellationToken ct = default)
        => (await PostJsonAsync<FirewallRuleRequest, ServiceTaskResponse>($"api/servers/{serverId}/firewall/delete", request, ct).ConfigureAwait(false))?.TaskId;


    public async Task<int?> FirewallToggleAsync(int serverId, bool enabled, CancellationToken ct = default)
        => (await PostJsonAsync<FirewallToggleRequest, ServiceTaskResponse>($"api/servers/{serverId}/firewall/toggle", new FirewallToggleRequest { Enabled = enabled }, ct).ConfigureAwait(false))?.TaskId;


    public async Task<int?> RequestServiceLogsAsync(int serverId, string serviceName, int lines = 100, bool follow = false, CancellationToken ct = default)
    {
        var result = await PostJsonAsync<ServiceLogsRequest, ServiceLogsResponse>(
            $"api/servers/{serverId}/services/logs",
            new ServiceLogsRequest { ServiceName = serviceName, Lines = lines, Follow = follow }, ct).ConfigureAwait(false);
        return result?.TaskId;
    }


    public async Task<string?> GetAgentServerUrlAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await Http.GetFromJsonAsync<AgentServerUrlResponse>("api/servers/agent-server-url", JsonOptions.Web, ct);
            return result?.Url;
        }
        catch (HttpRequestException) { return null; }
    }


    private sealed record AgentServerUrlResponse(string Url);

    private static Dictionary<string, string?> BuildSystemLogFilters(string? fileName, string? level, string? search, DateTime? dateFrom, DateTime? dateTo)
    {
        var query = new Dictionary<string, string?>();
        if (!string.IsNullOrWhiteSpace(fileName)) query["fileName"] = fileName;
        if (!string.IsNullOrWhiteSpace(level)) query["level"] = level;
        if (!string.IsNullOrWhiteSpace(search)) query["search"] = search;
        if (dateFrom.HasValue) query["dateFrom"] = dateFrom.Value.ToString("O");
        if (dateTo.HasValue) query["dateTo"] = dateTo.Value.ToString("O");
        return query;
    }


    /// <summary>PLAN-003 lot 27 / D27: the API's own timings over the instance's last 24 hours.</summary>
    public Task<ApiPerformanceReportDto?> GetApiPerformanceAsync(CancellationToken ct = default) =>
        Http.GetFromJsonAsync<ApiPerformanceReportDto>("api/admin/performance", JsonOptions.Web, ct);


    public async Task<List<SystemLogFileDto>> GetSystemLogFilesAsync()
    {
        return await Http.GetFromJsonAsync<List<SystemLogFileDto>>("api/system-logs/files", JsonOptions.Web) ?? [];
    }


    public async Task<PaginatedResult<SystemLogEntryDto>> GetSystemLogEntriesAsync(
        string? fileName, string? level, string? search, DateTime? dateFrom, DateTime? dateTo,
        int page = 1, int pageSize = 50,
        string? sortBy = null, bool sortDescending = true,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null)
    {
        var query = BuildSystemLogFilters(fileName, level, search, dateFrom, dateTo);
        query["page"] = page.ToString();
        query["pageSize"] = pageSize.ToString();
        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            query["sortBy"] = sortBy;
            query["sortDescending"] = sortDescending ? "true" : "false";
        }
        // Recette R-453: the virtualized grid's header filters, applied by the API to the whole log.
        return await Http.GetFromJsonAsync<PaginatedResult<SystemLogEntryDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString("api/system-logs/entries", query), filters),
            JsonOptions.Web) ?? new();
    }


    public async Task<byte[]?> DownloadSystemLogFileAsync(string fileName, CancellationToken ct = default)
    {
        var response = await Http.GetAsync($"api/system-logs/download/{Uri.EscapeDataString(fileName)}", ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsByteArrayAsync();
    }


    public async Task<int?> PurgeSystemLogsAsync(int retentionDays = 30, CancellationToken ct = default)
    {
        var response = await Http.DeleteAsync($"api/system-logs/purge?retentionDays={retentionDays}", ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<int>(JsonOptions.Web, ct).ConfigureAwait(false)
            : null;
    }
}
