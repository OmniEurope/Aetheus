// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.Email;

namespace Aetheus.Back.Components.Notifications;

public sealed class UserNotificationService(
    IUserNotificationRepository repo,
    IResourceAuthorizationService authorization,
    TimeProvider timeProvider,
    Aetheus.Back.Services.IUserChangeNotifier? changeNotifier = null,
    ILogger<UserNotificationService>? logger = null,
    INotificationEmailer? emailer = null) : IUserNotificationService
{
    private const int SubjectMaxLength = 300;
    private const int ErrorMessageMaxLength = 2000;

    public async Task<PaginatedResult<NotificationDeliveryDto>> GetDeliveriesAsync(
        int userId, UserNotificationPageRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, PaginationDefaults.MaximumPageSize);
        var (items, total) = await repo.GetDeliveriesPagedAsync(
            userId, page, pageSize, request, ct).ConfigureAwait(false);
        return new PaginatedResult<NotificationDeliveryDto>
        {
            Items = items.Select(ToDto).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public Task<int> GetUnreadCountAsync(int userId, CancellationToken ct = default) =>
        repo.CountUnreadAsync(userId, ct);

    public async Task<UserNotificationFilterValuesDto> GetFilterValuesAsync(int userId, CancellationToken ct = default) =>
        new() { EventTypes = await repo.GetEventTypesAsync(userId, ct).ConfigureAwait(false) };

    public async Task<bool> MarkReadAsync(int userId, int deliveryId, CancellationToken ct = default)
    {
        var delivery = await repo.FindDeliveryForRecipientAsync(deliveryId, userId, ct).ConfigureAwait(false);
        if (delivery is null) return false;
        if (delivery.ReadAt is null)
        {
            delivery.ReadAt = timeProvider.GetUtcNow().UtcDateTime;
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
            await PushChangedAsync([userId], ct).ConfigureAwait(false);
        }
        return true;
    }

    public async Task<int> MarkAllReadAsync(int userId, CancellationToken ct = default)
    {
        var changed = await repo.MarkAllReadAsync(userId, timeProvider.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        if (changed > 0)
            await PushChangedAsync([userId], ct).ConfigureAwait(false);
        return changed;
    }

    public async Task<List<NotificationPreferenceDto>> GetPreferencesAsync(int userId, CancellationToken ct = default)
    {
        var saved = await repo.GetPreferencesAsync(userId, ct).ConfigureAwait(false);
        var byType = saved.ToDictionary(preference => preference.EventType, preference => preference.IsEnabled, StringComparer.Ordinal);
        return NotificationEventTypes.All
            .Select(descriptor => new NotificationPreferenceDto
            {
                EventType = descriptor.EventType,
                IsEnabled = byType.TryGetValue(descriptor.EventType, out var enabled) ? enabled : descriptor.DefaultEnabled,
                DefaultEnabled = descriptor.DefaultEnabled,
                IsSaved = byType.ContainsKey(descriptor.EventType),
                CarriesProjectId = descriptor.CarriesProjectId
            })
            .ToList();
    }

    public async Task<List<NotificationPreferenceDto>> UpdatePreferencesAsync(
        int userId, UpdateNotificationPreferencesRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var unknown = request.Preferences
            .Select(item => item.EventType)
            .Where(eventType => NotificationEventTypes.Find(eventType) is null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
            throw new BadRequestException($"Unknown notification event type(s): {string.Join(", ", unknown)}.");

        // Last entry wins when the same type is sent twice.
        var preferences = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in request.Preferences)
            preferences[item.EventType] = item.IsEnabled;
        await repo.SavePreferencesAsync(userId, preferences, ct).ConfigureAwait(false);
        return await GetPreferencesAsync(userId, ct).ConfigureAwait(false);
    }

    public async Task<List<ProjectSubscriptionDto>> GetSubscriptionsAsync(int userId, CancellationToken ct = default)
    {
        var subscriptions = await repo.GetSubscriptionsAsync(userId, ct).ConfigureAwait(false);
        return subscriptions.Select(ToDto).ToList();
    }

    public async Task<ProjectSubscriptionDto?> SubscribeAsync(int userId, int projectId, CancellationToken ct = default)
    {
        var existing = await repo.FindSubscriptionAsync(userId, projectId, ct).ConfigureAwait(false);
        if (existing is not null) return ToDto(existing);

        var projectName = await repo.GetProjectNameAsync(projectId, ct).ConfigureAwait(false);
        if (projectName is null) return null;

        var subscription = new ProjectSubscription { UserId = userId, ProjectId = projectId };
        await repo.AddSubscriptionAsync(subscription, ct).ConfigureAwait(false);
        return new ProjectSubscriptionDto
        {
            ProjectId = projectId,
            ProjectName = projectName,
            CreatedAt = subscription.CreatedAt
        };
    }

    public Task<bool> UnsubscribeAsync(int userId, int projectId, CancellationToken ct = default) =>
        repo.RemoveSubscriptionAsync(userId, projectId, ct);

    public async Task<List<ProjectSubscriptionDto>> FollowProjectsAsync(
        int userId, IReadOnlyCollection<int>? readableProjectIds, CancellationToken ct = default)
    {
        // An empty grant follows nothing; only null (wildcard) reaches every project.
        if (readableProjectIds is not { Count: 0 })
            await repo.AddMissingSubscriptionsAsync(userId, readableProjectIds, ct).ConfigureAwait(false);
        return await GetSubscriptionsAsync(userId, ct).ConfigureAwait(false);
    }

    public async Task<int> RecordReleaseDeployedAsync(
        int releaseId, int? pipelineRunId, string? stageName, bool isRollback, CancellationToken ct = default)
    {
        if (await repo.GetReleaseProjectAsync(releaseId, ct).ConfigureAwait(false) is not { } release) return 0;
        var payload = JsonSerializer.Serialize(new
        {
            ReleaseId = releaseId,
            release.ProjectId,
            release.Version,
            PipelineRunId = pipelineRunId,
            StageName = stageName,
            IsRollback = isRollback
        });
        return await RecordProjectEventAsync(NotificationEventTypes.ReleaseDeployed, payload, ct).ConfigureAwait(false);
    }

    public async Task<int> RecordServerEventAsync(
        string eventType, int serverId, Func<int, object> payloadForProject, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payloadForProject);
        var projectIds = await repo.GetServerProjectIdsAsync(serverId, ct).ConfigureAwait(false);
        if (projectIds.Count == 0)
        {
            logger?.LogDebug(
                "[UserNotifications] {EventType} on server {ServerId} not recorded: the server is attached to no project",
                eventType, serverId);
            return 0;
        }

        var written = 0;
        foreach (var projectId in projectIds)
        {
            written += await RecordProjectEventAsync(
                eventType, JsonSerializer.Serialize(payloadForProject(projectId)), ct).ConfigureAwait(false);
        }
        return written;
    }

    public async Task<int> RecordAdministratorEventAsync(
        string eventType, string subject, string jsonPayload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        var administrators = await repo.GetActiveAdministratorsAsync(ct).ConfigureAwait(false);
        if (administrators.Count == 0) return 0;

        var boundedSubject = subject.Length <= SubjectMaxLength ? subject : subject[..SubjectMaxLength];
        var deliveries = administrators.Select(administrator => new NotificationDelivery
        {
            RecipientUserId = administrator.UserId,
            ChannelId = null,
            EventType = eventType,
            Subject = boundedSubject,
            PayloadJson = jsonPayload,
            // Until the e-mail below says otherwise: recorded for the administrator, nothing sent.
            Status = NotificationDeliveryStatus.NotConfigured,
            ErrorMessage = null,
            SentAt = null
        }).ToList();
        await EmailDeliveriesAsync(deliveries, [.. administrators.Select(administrator => administrator.Email)], ct).ConfigureAwait(false);
        await repo.AddDeliveriesAsync(deliveries, ct).ConfigureAwait(false);
        await PushChangedAsync([.. deliveries.Select(delivery => delivery.RecipientUserId)], ct).ConfigureAwait(false);
        return deliveries.Count;
    }

    public async Task<int> RecordProjectEventAsync(
        string eventType, string jsonPayload, CancellationToken ct = default, string? emailOptInEventType = null,
        string? emailOnceMarker = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        var projectId = ReadProjectId(jsonPayload);
        if (projectId is null) return 0;

        // A type missing from the catalogue has no preference a user could have set: nobody receives it.
        if (NotificationEventTypes.Find(eventType) is not { } descriptor) return 0;

        var projectName = await repo.GetProjectNameAsync(projectId.Value, ct).ConfigureAwait(false);
        if (projectName is null) return 0;

        var candidates = await repo.GetProjectRecipientsAsync(
            projectId.Value, eventType, descriptor.DefaultEnabled, ct).ConfigureAwait(false);
        var subject = BuildSubject(eventType, projectName);
        var deliveries = new List<NotificationDelivery>(candidates.Count);
        var addresses = new List<string?>(candidates.Count);
        foreach (var candidate in candidates)
        {
            // A subscriber who lost access to the project must not keep learning about it.
            if (!await authorization.HasPermissionAsync(
                    candidate.Username, ResourceType.Project, projectId.Value, Permission.Read, ct).ConfigureAwait(false))
                continue;

            deliveries.Add(new NotificationDelivery
            {
                RecipientUserId = candidate.UserId,
                ChannelId = null,
                EventType = eventType,
                Subject = subject,
                PayloadJson = jsonPayload,
                // Until the e-mail below says otherwise: recorded for the user, nothing sent.
                Status = NotificationDeliveryStatus.NotConfigured,
                ErrorMessage = null,
                SentAt = null
            });
            addresses.Add(candidate.Email);
        }

        if (deliveries.Count > 0)
        {
            if (descriptor.SentByEmail)
                await EmailOptedInDeliveriesAsync(deliveries, addresses, emailOptInEventType, emailOnceMarker, ct).ConfigureAwait(false);
            await repo.AddDeliveriesAsync(deliveries, ct).ConfigureAwait(false);
            await PushChangedAsync([.. deliveries.Select(delivery => delivery.RecipientUserId)], ct).ConfigureAwait(false);
        }
        return deliveries.Count;
    }

    /// <summary> Recette R1-77: without an opt-in event type every delivery is e-mailed. With one, only the
    /// recipients who enabled that preference (off unless they saved it on) are; the others keep their in-app
    /// delivery, recorded as NotConfigured with the reason, so a child pipeline result never mails by default.
    /// With a once-marker, a recipient already e-mailed this result (a retried run ending the same way again)
    /// is not e-mailed a second time; their in-app delivery says so.
    /// </summary>
    private async Task EmailOptedInDeliveriesAsync(
        IReadOnlyList<NotificationDelivery> deliveries, IReadOnlyList<string?> addresses, string? optInEventType,
        string? onceMarker, CancellationToken ct)
    {
        var descriptor = optInEventType is null ? null : NotificationEventTypes.Find(optInEventType);
        var alreadyEmailed = onceMarker is null || deliveries.Count == 0
            ? []
            : await repo.GetRecipientsAlreadyEmailedAsync(
                deliveries[0].EventType, onceMarker, [.. deliveries.Select(delivery => delivery.RecipientUserId)], ct).ConfigureAwait(false);
        var mailed = new List<NotificationDelivery>();
        var mailedAddresses = new List<string?>();
        for (var i = 0; i < deliveries.Count; i++)
        {
            if (optInEventType is not null)
            {
                var saved = await repo.GetPreferencesAsync(deliveries[i].RecipientUserId, ct).ConfigureAwait(false);
                var preference = saved.FirstOrDefault(item => item.EventType == optInEventType);
                if (!(preference?.IsEnabled ?? descriptor?.DefaultEnabled ?? false))
                {
                    deliveries[i].ErrorMessage = $"E-mail not sent: the recipient did not enable '{optInEventType}'.";
                    continue;
                }
            }
            if (alreadyEmailed.Contains(deliveries[i].RecipientUserId))
            {
                deliveries[i].ErrorMessage = "E-mail not sent: this result was already e-mailed to the recipient.";
                continue;
            }
            mailed.Add(deliveries[i]);
            mailedAddresses.Add(addresses[i]);
        }

        await EmailDeliveriesAsync(mailed, mailedAddresses, ct).ConfigureAwait(false);
    }

    /// <summary> Every notification also leaves by e-mail to the recipient's account address, and
    /// its row records the real outcome: Sent once the SMTP server accepted it, Failed with the
    /// server's text, NotConfigured with the reason when no sender or no address exists. </summary>
    private async Task EmailDeliveriesAsync(
        IReadOnlyList<NotificationDelivery> deliveries, IReadOnlyList<string?> addresses, CancellationToken ct)
    {
        if (emailer is null || deliveries.Count == 0) return;
        var emails = deliveries
            .Select((delivery, index) => new NotificationEmail(addresses[index], delivery.Subject, delivery.EventType, delivery.PayloadJson))
            .ToList();
        var results = await emailer.SendAsync(emails, ct).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        for (var i = 0; i < deliveries.Count; i++)
        {
            var delivery = deliveries[i];
            var result = results[i];
            delivery.Status = result.Status switch
            {
                EmailSendStatus.Sent => NotificationDeliveryStatus.Sent,
                EmailSendStatus.Failed => NotificationDeliveryStatus.Failed,
                _ => NotificationDeliveryStatus.NotConfigured
            };
            delivery.SentAt = result.Status == EmailSendStatus.Sent ? now : null;
            delivery.ErrorMessage = result.Status == EmailSendStatus.Sent ? null : Bound(result.Message);
        }
    }

    private static string Bound(string message) =>
        message.Length <= ErrorMessageMaxLength ? message : message[..ErrorMessageMaxLength];

    /// <summary> Tells the recipients' open tabs that their notifications changed. Best effort: the
    /// row is saved already, and a tab that misses the push still catches up on its next poll.
    /// </summary>
    private async Task PushChangedAsync(IReadOnlyCollection<int> userIds, CancellationToken ct)
    {
        if (changeNotifier is null) return;
        try
        {
            await changeNotifier.NotifyNotificationsChangedAsync(userIds, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best effort by design (see the summary): the change is saved, only the push failed.
            logger?.LogWarning(exception, "[UserNotifications] push to the recipients failed");
        }
    }

    /// <summary>The integer <c>ProjectId</c> at the root of the payload, or null when it is absent or null.</summary>
    internal static int? ReadProjectId(string jsonPayload)
    {
        if (string.IsNullOrWhiteSpace(jsonPayload)) return null;
        try
        {
            using var document = JsonDocument.Parse(jsonPayload);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "ProjectId", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out var projectId))
                    return projectId;
            }
        }
        catch (JsonException)
        {
            // A payload that is not JSON names no project.
        }
        return null;
    }

    internal static string BuildSubject(string eventType, string projectName)
    {
        var subject = $"{eventType} in project {projectName}";
        return subject.Length <= SubjectMaxLength ? subject : subject[..SubjectMaxLength];
    }

    private static NotificationDeliveryDto ToDto(NotificationDelivery delivery) => new()
    {
        Id = delivery.Id,
        ChannelId = delivery.ChannelId,
        ChannelName = delivery.Channel?.Name,
        EventType = delivery.EventType,
        Subject = delivery.Subject,
        PayloadJson = delivery.PayloadJson,
        Status = delivery.Status,
        ErrorMessage = delivery.ErrorMessage,
        CreatedAt = delivery.CreatedAt,
        SentAt = delivery.SentAt,
        ReadAt = delivery.ReadAt
    };

    private static ProjectSubscriptionDto ToDto(ProjectSubscription subscription) => new()
    {
        ProjectId = subscription.ProjectId,
        ProjectName = subscription.Project?.Name ?? string.Empty,
        CreatedAt = subscription.CreatedAt
    };
}
