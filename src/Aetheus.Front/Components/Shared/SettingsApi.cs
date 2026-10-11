// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Shared;

/// <summary>Application settings, service connections and plugins.</summary>
public sealed class SettingsApi(HttpClient http) : ApiClientBase(http)
{
    public async Task<PaginatedResult<ServiceConnectionDto>> GetServiceConnectionsAsync(
        int page = 1, int pageSize = 20, string? search = null, int? projectId = null,
        string? sortBy = null, bool sortDescending = false, IReadOnlyList<GridFilter>? filters = null)
    {
        var query = PageQuery(page, pageSize, search, sortBy, sortDescending);
        if (projectId.HasValue) query["projectId"] = projectId.Value.ToString();
        // Recette R-224: the list's column header filters.
        return await Http.GetFromJsonAsync<PaginatedResult<ServiceConnectionDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString("api/service-connections", query), filters), JsonOptions.Web)
            ?? new PaginatedResult<ServiceConnectionDto>();
    }

    /// <summary>Recette R-224: the project names the list's Project column filter offers.</summary>
    public async Task<ServiceConnectionFilterValuesDto> GetServiceConnectionFilterValuesAsync(CancellationToken ct = default) =>
        await GetJsonAsync<ServiceConnectionFilterValuesDto>("api/service-connections/filter-values", ct) ?? new();


    public async Task<ServiceConnectionDetailDto?> GetServiceConnectionAsync(int id)
    {
        var response = await Http.GetAsync($"api/service-connections/{id}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ServiceConnectionDetailDto>(JsonOptions.Web);
    }


    public async Task<ServiceConnectionDto?> CreateServiceConnectionAsync(CreateServiceConnectionRequest request)
    {
        var response = await Http.PostAsJsonAsync("api/service-connections", request, JsonOptions.Web);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ServiceConnectionDto>(JsonOptions.Web);
    }


    public async Task<ServiceConnectionDto?> UpdateServiceConnectionAsync(int id, UpdateServiceConnectionRequest request)
    {
        var response = await Http.PutAsJsonAsync($"api/service-connections/{id}", request, JsonOptions.Web);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ServiceConnectionDto>(JsonOptions.Web);
    }


    public async Task<bool> DeleteServiceConnectionAsync(int id)
    {
        var response = await Http.DeleteAsync($"api/service-connections/{id}");
        return response.IsSuccessStatusCode;
    }


    public async Task<ServiceConnectionTestResultDto?> TestServiceConnectionAsync(int id)
    {
        var response = await Http.PostAsync($"api/service-connections/{id}/test", null);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ServiceConnectionTestResultDto>(JsonOptions.Web);
    }

    public async Task<List<AppSettingDto>> GetSettingsAsync()
    {
        return await Http.GetFromJsonAsync<List<AppSettingDto>>("api/settings", JsonOptions.Web) ?? [];
    }


    public async Task<ApiStatus> UpdateSettingAsync(string key, string value)
    {
        var response = await Http.PutAsJsonAsync($"api/settings/{Uri.EscapeDataString(key)}", new AppSettingDto { Key = key, Value = value });
        return ApiStatus.From(response);
    }


    /// <summary>Recette R1-16: the platform e-mail sender (default no-reply account or custom SMTP server).</summary>
    public Task<EmailSenderSettingsDto?> GetEmailSenderAsync(CancellationToken ct = default) =>
        GetJsonAsync<EmailSenderSettingsDto>("api/settings/email-sender", ct);

    public Task<ApiOutcome<EmailSenderSettingsDto, ApiError>> UpdateEmailSenderAsync(
        UpdateEmailSenderSettingsRequest request, CancellationToken ct = default) =>
        PutForOutcomeAsync<EmailSenderSettingsDto, ApiError>("api/settings/email-sender", request, ct);

    /// <summary>Creates the no-reply mailbox, or gives it a new password, kept in the secret store.</summary>
    public Task<ApiOutcome<EmailSenderSettingsDto, ApiError>> PrepareNoReplySenderAsync(CancellationToken ct = default) =>
        PostForOutcomeAsync<EmailSenderSettingsDto, ApiError>("api/mail/no-reply-sender", null, ct);

    /// <summary>The daily recap e-mail: settings, preview of yesterday's recap, and a send to the caller now.</summary>
    public Task<DailyRecapSettingsDto?> GetDailyRecapAsync(CancellationToken ct = default) =>
        GetJsonAsync<DailyRecapSettingsDto>("api/settings/daily-recap", ct);

    public Task<ApiOutcome<DailyRecapSettingsDto, ApiError>> UpdateDailyRecapAsync(
        UpdateDailyRecapSettingsRequest request, CancellationToken ct = default) =>
        PutForOutcomeAsync<DailyRecapSettingsDto, ApiError>("api/settings/daily-recap", request, ct);

    public Task<DailyRecapPreviewDto?> GetDailyRecapPreviewAsync(CancellationToken ct = default) =>
        GetJsonAsync<DailyRecapPreviewDto>("api/settings/daily-recap/preview", ct);

    public Task<ApiOutcome<DailyRecapSendResultDto, ApiError>> SendDailyRecapNowAsync(CancellationToken ct = default) =>
        PostForOutcomeAsync<DailyRecapSendResultDto, ApiError>("api/settings/daily-recap/send-now", null, ct);

    public async Task<List<SecretDto>> GetSecretsAsync()
    {
        return await Http.GetFromJsonAsync<List<SecretDto>>("api/settings/secrets", JsonOptions.Web) ?? [];
    }


    public async Task<SecretDto?> CreateSecretAsync(CreateSecretRequest request)
    {
        var response = await Http.PostAsJsonAsync("api/settings/secrets", request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<SecretDto>(JsonOptions.Web)
            : null;
    }


    public async Task<ApiStatus> DeleteSecretAsync(int id)
    {
        var response = await Http.DeleteAsync($"api/settings/secrets/{id}");
        return ApiStatus.From(response);
    }


    public async Task<PaginatedResult<PluginRegistrationDto>> GetPluginsPageAsync(
        int page = 1, int pageSize = 25, string? search = null,
        string? sortBy = null, bool sortDescending = false, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null)
    {
        var query = BuildPaginationQuery(page, pageSize, search, sortBy, sortDescending);
        // Recette R-224: the list's column header filters.
        return await GetJsonAsync<PaginatedResult<PluginRegistrationDto>>(
            GridColumnFilters.AddTo(QueryHelpers.AddQueryString("api/plugins", query), filters), ct).ConfigureAwait(false) ?? new();
    }

    /// <summary>Recette R-224: the authors the plugins list's Author column filter offers.</summary>
    public async Task<PluginFilterValuesDto> GetPluginFilterValuesAsync(CancellationToken ct = default) =>
        await GetJsonAsync<PluginFilterValuesDto>("api/plugins/filter-values", ct).ConfigureAwait(false) ?? new();


    public async Task<List<PluginRegistrationDto>> GetPluginsAsync(CancellationToken ct = default)
    {
        const int pageSize = 100;
        var items = new List<PluginRegistrationDto>();
        for (var page = 1; ; page++)
        {
            var result = await GetPluginsPageAsync(page, pageSize, ct: ct).ConfigureAwait(false);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0) return items;
        }
    }


    public async Task<PluginRegistrationDto?> GetPluginAsync(int id, CancellationToken ct = default)
    {
        return await Http.GetFromJsonAsync<PluginRegistrationDto>($"api/plugins/{id}", JsonOptions.Web, ct);
    }


    public Task<PluginRegistrationDto?> RegisterPluginAsync(RegisterPluginRequest request, CancellationToken ct = default)
        => PostJsonAsync<RegisterPluginRequest, PluginRegistrationDto>("api/plugins", request, ct);


    public Task<PluginRegistrationDto?> UpdatePluginAsync(int id, UpdatePluginRequest request, CancellationToken ct = default)
        => PutJsonAsync<UpdatePluginRequest, PluginRegistrationDto>($"api/plugins/{id}", request, ct);


    public Task<ApiStatus> UnregisterPluginAsync(int id, CancellationToken ct = default)
        => DeleteAsync($"api/plugins/{id}", ct);
}
