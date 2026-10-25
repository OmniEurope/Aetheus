// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Notifications;

/// <summary>
/// Persistence of what the notification module produces for users: deliveries, project subscriptions
/// and per-user event preferences. Project names and subscribers are read over the shared entities
/// directly, so this module (layer 1) never injects the Projects module above it.
/// </summary>
public interface IUserNotificationRepository
{
    Task<(List<NotificationDelivery> Items, int Total)> GetDeliveriesPagedAsync(
        int recipientUserId, int page, int pageSize, UserNotificationPageRequest request, CancellationToken ct = default);
    Task<int> CountUnreadAsync(int recipientUserId, CancellationToken ct = default);

    /// <summary>The distinct event types of the user's deliveries.</summary>
    Task<List<string>> GetEventTypesAsync(int recipientUserId, CancellationToken ct = default);

    /// <summary>The delivery when it belongs to <paramref name="recipientUserId"/>; null otherwise (tracked).</summary>
    Task<NotificationDelivery?> FindDeliveryForRecipientAsync(int id, int recipientUserId, CancellationToken ct = default);

    Task<int> MarkAllReadAsync(int recipientUserId, DateTime readAt, CancellationToken ct = default);
    Task AddDeliveriesAsync(IReadOnlyCollection<NotificationDelivery> deliveries, CancellationToken ct = default);

    /// <summary>
    /// Active subscribers of <paramref name="projectId"/> whose preference accepts <paramref name="eventType"/>:
    /// a saved enabled row, or no saved row while <paramref name="enabledByDefault"/> is true.
    /// </summary>
    Task<List<NotificationRecipient>> GetProjectRecipientsAsync(
        int projectId, string eventType, bool enabledByDefault, CancellationToken ct = default);

    Task<string?> GetProjectNameAsync(int projectId, CancellationToken ct = default);

    Task<List<UserNotificationPreference>> GetPreferencesAsync(int userId, CancellationToken ct = default);

    /// <summary>The saved on/off choice of each user among <paramref name="userIds"/> for <paramref name="eventType"/>, in one
    /// query. A user who never saved a choice for it has no entry.</summary>
    Task<Dictionary<int, bool>> GetSavedChoicesAsync(
        string eventType, IReadOnlyCollection<int> userIds, CancellationToken ct = default);

    /// <summary>Recette R1-77: the users among <paramref name="userIds"/> already e-mailed (Sent) a delivery of
    /// <paramref name="eventType"/> whose payload contains <paramref name="payloadMarker"/>.</summary>
    Task<HashSet<int>> GetRecipientsAlreadyEmailedAsync(
        string eventType, string payloadMarker, IReadOnlyCollection<int> userIds, CancellationToken ct = default);

    /// <summary>Inserts or updates one row per entry of <paramref name="preferences"/>; other rows are left alone.</summary>
    Task SavePreferencesAsync(int userId, IReadOnlyDictionary<string, bool> preferences, CancellationToken ct = default);

    Task<List<ProjectSubscription>> GetSubscriptionsAsync(int userId, CancellationToken ct = default);
    Task<ProjectSubscription?> FindSubscriptionAsync(int userId, int projectId, CancellationToken ct = default);
    Task AddSubscriptionAsync(ProjectSubscription subscription, CancellationToken ct = default);

    /// <summary>Removes the subscription; false when there was none.</summary>
    Task<bool> RemoveSubscriptionAsync(int userId, int projectId, CancellationToken ct = default);

    /// <summary> Subscribes the user to every project of <paramref name="projectIds"/> (every
    /// project when null) not followed yet, and returns how many subscriptions were added.
    /// </summary>
    Task<int> AddMissingSubscriptionsAsync(int userId, IReadOnlyCollection<int>? projectIds, CancellationToken ct = default);

    /// <summary>Returns active users holding the Admin role.</summary>
    Task<List<NotificationRecipient>> GetActiveAdministratorsAsync(CancellationToken ct = default);

    /// <summary>The distinct projects the server is attached to (agent server links).</summary>
    Task<List<int>> GetServerProjectIdsAsync(int serverId, CancellationToken ct = default);

    /// <summary>The project and version of a release, or null when it does not exist.</summary>
    Task<(int ProjectId, string Version)?> GetReleaseProjectAsync(int releaseId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
