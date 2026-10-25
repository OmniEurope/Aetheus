// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Shared;

/// <summary>The current user's notifications, preferences and project subscriptions (<c>api/notifications/me</c>).</summary>
public sealed class UserNotificationsApi(HttpClient http) : ApiClientBase(http)
{
    private const string Root = "api/notifications/me";

    public async Task<PaginatedResult<NotificationDeliveryDto>> GetMineAsync(
        int page, int pageSize, NotificationDeliveryStatus? status = null, bool unreadOnly = false, bool sortDescending = true,
        string? sortBy = null, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = pageSize.ToString(CultureInfo.InvariantCulture)
        };
        if (status is { } wanted)
            query["status"] = wanted.ToString();
        if (unreadOnly)
            query["unreadOnly"] = "true";
        if (!sortDescending)
            query["sortDescending"] = "false";
        if (!string.IsNullOrWhiteSpace(sortBy))
            query["sortBy"] = sortBy;
        return await GetJsonAsync<PaginatedResult<NotificationDeliveryDto>>(Root, query, ct).ConfigureAwait(false) ?? new();
    }

    /// <summary>The page the /notifications grid asks for, with its header filters and sort.</summary>
    public async Task<PaginatedResult<NotificationDeliveryDto>> GetMineAsync(UserNotificationPageRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = new Dictionary<string, string?>
        {
            ["page"] = request.Page.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = request.PageSize.ToString(CultureInfo.InvariantCulture),
            ["sortDescending"] = request.SortDescending ? "true" : "false"
        };
        if (request.Status is { } status) query["status"] = status.ToString();
        if (request.IsRead is { } isRead) query["isRead"] = isRead ? "true" : "false";
        if (!string.IsNullOrWhiteSpace(request.EventType)) query["eventType"] = request.EventType;
        if (!string.IsNullOrWhiteSpace(request.Subject)) query["subject"] = request.Subject;
        if (!string.IsNullOrWhiteSpace(request.SortBy)) query["sortBy"] = request.SortBy;
        // The grid's column header filters.
        var url = GridColumnFilters.AddTo(QueryHelpers.AddQueryString(Root, query), request.Filters);
        return await GetJsonAsync<PaginatedResult<NotificationDeliveryDto>>(url, ct).ConfigureAwait(false) ?? new();
    }

    /// <summary>The event types the /notifications Event filter offers.</summary>
    public async Task<UserNotificationFilterValuesDto> GetFilterValuesAsync(CancellationToken ct = default) =>
        await GetJsonAsync<UserNotificationFilterValuesDto>($"{Root}/filter-values", ct).ConfigureAwait(false) ?? new();

    public async Task<int> GetUnreadCountAsync(CancellationToken ct = default) =>
        await GetJsonAsync<int>($"{Root}/unread-count", ct).ConfigureAwait(false);

    public async Task<bool> MarkReadAsync(int deliveryId, CancellationToken ct = default)
    {
        var response = await Http.PostAsync($"{Root}/{deliveryId}/read", null, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Marks every unread delivery as read; the number changed, or null when the call failed.</summary>
    public async Task<int?> MarkAllReadAsync(CancellationToken ct = default)
    {
        var response = await Http.PostAsync($"{Root}/read-all", null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<int>(JsonOptions.Web, ct).ConfigureAwait(false);
    }

    public async Task<List<NotificationPreferenceDto>> GetPreferencesAsync(CancellationToken ct = default) =>
        await GetJsonAsync<List<NotificationPreferenceDto>>($"{Root}/preferences", ct).ConfigureAwait(false) ?? [];

    /// <summary>Saves the given preferences and returns the whole list as stored, or null when the call failed.</summary>
    public Task<List<NotificationPreferenceDto>?> SavePreferencesAsync(
        UpdateNotificationPreferencesRequest request, CancellationToken ct = default) =>
        PutJsonAsync<UpdateNotificationPreferencesRequest, List<NotificationPreferenceDto>>($"{Root}/preferences", request, ct);

    public async Task<List<ProjectSubscriptionDto>> GetSubscriptionsAsync(CancellationToken ct = default) =>
        await GetJsonAsync<List<ProjectSubscriptionDto>>($"{Root}/subscriptions", ct).ConfigureAwait(false) ?? [];

    /// <summary>Follows the project; the subscription, or null when refused or not found.</summary>
    public async Task<ProjectSubscriptionDto?> SubscribeAsync(int projectId, CancellationToken ct = default)
    {
        var response = await Http.PutAsync($"{Root}/subscriptions/{projectId}", null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<ProjectSubscriptionDto>(JsonOptions.Web, ct).ConfigureAwait(false);
    }

    /// <summary> Follows every project the user can read; all of the user's subscriptions
    /// afterwards, or null when the call failed. </summary>
    public async Task<List<ProjectSubscriptionDto>?> FollowAllProjectsAsync(CancellationToken ct = default)
    {
        var response = await Http.PostAsync($"{Root}/subscriptions/all", null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<List<ProjectSubscriptionDto>>(JsonOptions.Web, ct).ConfigureAwait(false);
    }

    public Task<ApiStatus> UnsubscribeAsync(int projectId, CancellationToken ct = default) =>
        DeleteAsync($"{Root}/subscriptions/{projectId}", ct);
}
