// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Notifications;

/// <summary>
/// Notifications as the current user sees them: the deliveries addressed to them, their per-event
/// preferences and the projects they follow, plus the recording of a project event for its subscribers.
/// Every read and write is scoped to the user id the caller passes; nothing here crosses users.
/// </summary>
public interface IUserNotificationService
{
    Task<PaginatedResult<NotificationDeliveryDto>> GetDeliveriesAsync(
        int userId, UserNotificationPageRequest request, CancellationToken ct = default);
    Task<int> GetUnreadCountAsync(int userId, CancellationToken ct = default);

    /// <summary>The values the user's /notifications grid filters offer.</summary>
    Task<UserNotificationFilterValuesDto> GetFilterValuesAsync(int userId, CancellationToken ct = default);

    /// <summary>Marks one of the user's deliveries as read; false when it is not theirs or does not exist.</summary>
    Task<bool> MarkReadAsync(int userId, int deliveryId, CancellationToken ct = default);

    /// <summary>Marks every unread delivery of the user as read and returns how many changed.</summary>
    Task<int> MarkAllReadAsync(int userId, CancellationToken ct = default);

    Task<List<NotificationPreferenceDto>> GetPreferencesAsync(int userId, CancellationToken ct = default);
    Task<List<NotificationPreferenceDto>> UpdatePreferencesAsync(
        int userId, UpdateNotificationPreferencesRequest request, CancellationToken ct = default);

    Task<List<ProjectSubscriptionDto>> GetSubscriptionsAsync(int userId, CancellationToken ct = default);

    /// <summary>Subscribes the user to the project (idempotent); null when the project does not exist.</summary>
    Task<ProjectSubscriptionDto?> SubscribeAsync(int userId, int projectId, CancellationToken ct = default);

    Task<bool> UnsubscribeAsync(int userId, int projectId, CancellationToken ct = default);

    /// <summary>Subscribe to readable projects not yet followed; null <paramref
    /// name="readableProjectIds"/> means a wildcard read grant. Return all current
    /// subscriptions.</summary>
    Task<List<ProjectSubscriptionDto>> FollowProjectsAsync(
        int userId, IReadOnlyCollection<int>? readableProjectIds, CancellationToken ct = default);

    /// <summary> Records <see cref="NotificationEventTypes.ReleaseDeployed"/> for the subscribers
    /// of the release's project. Returns the number of deliveries written; zero for an unknown
    /// release. </summary>
    Task<int> RecordReleaseDeployedAsync(
        int releaseId, int? pipelineRunId, string? stageName, bool isRollback, CancellationToken ct = default);

    /// <summary> Records a server event once per project the server is attached to, with that
    /// project's id in the payload built by <paramref name="payloadForProject"/>. A server attached
    /// to no project records nothing. Returns the number of deliveries written. </summary>
    Task<int> RecordServerEventAsync(
        string eventType, int serverId, Func<int, object> payloadForProject, CancellationToken ct = default);

    /// <summary>Record one delivery per active administrator for platform events, independent of
    /// project subscriptions and preferences. Return the number of deliveries written.</summary>
    Task<int> RecordAdministratorEventAsync(
        string eventType, string subject, string jsonPayload, CancellationToken ct = default);

    /// <summary>
    /// Writes one delivery per subscriber of the event's project whose preferences accept it and who can
    /// still read the project. Returns the number of rows written; zero when the payload names no project.
    /// No transport exists for these deliveries, so they are recorded as NotConfigured and never Sent.
    /// </summary>
    /// <param name="emailOptInEventType">When set, a subscriber is e-mailed only if the preference of that event type is
    /// enabled for them (default off); the in-app delivery is written for every subscriber either way.</param>
    /// <param name="emailOnceMarker">Recette R1-77: when set, a subscriber already e-mailed (Sent) a delivery of the same event
    /// type whose payload contains this fragment is not e-mailed again; the in-app delivery is still written.</param>
    Task<int> RecordProjectEventAsync(
        string eventType, string jsonPayload, CancellationToken ct = default, string? emailOptInEventType = null,
        string? emailOnceMarker = null);
}
