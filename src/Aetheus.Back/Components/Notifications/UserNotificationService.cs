// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Notifications.MailTemplates;
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

    public async Task<PipelineFailureMailPreferenceDto?> GetPipelineFailureMailPreferenceAsync(
        int userId, int pipelineId, CancellationToken ct = default)
    {
        if (await repo.GetPipelineMailInfoAsync(pipelineId, ct).ConfigureAwait(false) is not { } pipeline) return null;
        var saved = await repo.GetSavedPipelineChoicesAsync(
            NotificationEventTypes.PipelineFailed, pipelineId, [userId], ct).ConfigureAwait(false);
        return ToDto(pipeline, saved.TryGetValue(userId, out var enabled) ? enabled : null);
    }

    public async Task<PipelineFailureMailPreferenceDto?> SetPipelineFailureMailPreferenceAsync(
        int userId, int pipelineId, bool isEnabled, CancellationToken ct = default)
    {
        if (await repo.GetPipelineMailInfoAsync(pipelineId, ct).ConfigureAwait(false) is not { } pipeline) return null;
        await repo.SavePipelineChoiceAsync(userId, NotificationEventTypes.PipelineFailed, pipelineId, isEnabled, ct)
            .ConfigureAwait(false);
        return ToDto(pipeline, isEnabled);
    }

    public async Task<List<PipelineFailureMailChoiceDto>> GetPipelineFailureMailChoicesAsync(
        int userId, IReadOnlyCollection<int>? readablePipelineIds, CancellationToken ct = default)
    {
        var rows = await repo.GetPipelineChoicesAsync(userId, NotificationEventTypes.PipelineFailed, ct).ConfigureAwait(false);
        return rows
            .Where(row => readablePipelineIds is null || readablePipelineIds.Contains(row.PipelineId))
            .Select(row => new PipelineFailureMailChoiceDto
            {
                PipelineId = row.PipelineId,
                PipelineName = row.PipelineName,
                IsEnabled = row.IsEnabled,
                DefaultEnabled = !row.IsScheduled,
                IsScheduled = row.IsScheduled
            })
            .ToList();
    }

    private static PipelineFailureMailPreferenceDto ToDto(PipelineMailInfo pipeline, bool? saved) => new()
    {
        PipelineId = pipeline.Id,
        IsEnabled = saved ?? !pipeline.IsScheduled,
        DefaultEnabled = !pipeline.IsScheduled,
        IsSaved = saved is not null,
        IsScheduled = pipeline.IsScheduled
    };

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
        // An event about several projects at once (recette R-26) has no project of its own: each follower gets one delivery.
        if (projectId is null) return await RecordProjectDigestAsync(eventType, jsonPayload, ct).ConfigureAwait(false);

        // A type missing from the catalogue has no preference a user could have set: nobody receives it.
        if (NotificationEventTypes.Find(eventType) is not { } descriptor) return 0;

        var projectName = await repo.GetProjectNameAsync(projectId.Value, ct).ConfigureAwait(false);
        if (projectName is null) return 0;

        var candidates = await repo.GetProjectRecipientsAsync(
            projectId.Value, eventType, descriptor.DefaultEnabled, ct).ConfigureAwait(false);
        var subject = BuildSubject(eventType, projectName);
        var deliveries = new List<NotificationDelivery>(candidates.Count);
        var addresses = new List<string?>(candidates.Count);
        // A subscriber who lost access to the project must not keep learning about it. One query for every
        // subscriber rather than one per subscriber (RBAC audit, finding 12).
        var readers = candidates.Count == 0
            ? []
            : await authorization.GetUsernamesWithPermissionAsync(
                [.. candidates.Select(candidate => candidate.Username)], ResourceType.Project, projectId.Value,
                Permission.Read, ct).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            if (!readers.Contains(candidate.Username))
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
            {
                // The mail of a project's event is written with its organization's own template when it has one.
                var organizationId = await repo.GetProjectOrganizationIdAsync(projectId.Value, ct).ConfigureAwait(false);
                await EmailOptedInDeliveriesAsync(deliveries, addresses, emailOptInEventType, emailOnceMarker, organizationId, ct).ConfigureAwait(false);
            }
            await repo.AddDeliveriesAsync(deliveries, ct).ConfigureAwait(false);
            await PushChangedAsync([.. deliveries.Select(delivery => delivery.RecipientUserId)], ct).ConfigureAwait(false);
        }
        return deliveries.Count;
    }

    /// <summary>
    /// Recette R-26: an event that lists several projects (<see cref="ProjectDigestPayload"/>) writes ONE delivery per
    /// follower, whatever the number of projects they follow, narrowed to the projects they follow and can still read,
    /// and e-mails it once. The payload without any project, or with none a follower can read, writes nothing.
    /// </summary>
    private async Task<int> RecordProjectDigestAsync(string eventType, string jsonPayload, CancellationToken ct)
    {
        if (NotificationEventTypes.Find(eventType) is not { } descriptor) return 0;
        var projectIds = ProjectDigestPayload.ProjectIds(jsonPayload);
        if (projectIds.Count == 0) return 0;

        var followed = new Dictionary<int, (NotificationRecipient Recipient, HashSet<int> Projects)>();
        foreach (var projectId in projectIds)
        {
            var candidates = await repo.GetProjectRecipientsAsync(projectId, eventType, descriptor.DefaultEnabled, ct).ConfigureAwait(false);
            if (candidates.Count == 0) continue;
            var readers = await authorization.GetUsernamesWithPermissionAsync(
                [.. candidates.Select(candidate => candidate.Username)], ResourceType.Project, projectId,
                Permission.Read, ct).ConfigureAwait(false);
            foreach (var candidate in candidates.Where(candidate => readers.Contains(candidate.Username)))
            {
                if (!followed.TryGetValue(candidate.UserId, out var entry))
                    followed[candidate.UserId] = entry = (candidate, []);
                entry.Projects.Add(projectId);
            }
        }

        var deliveries = new List<(NotificationDelivery Delivery, string? Address, int? OrganizationId)>();
        foreach (var (userId, (recipient, projects)) in followed.OrderBy(pair => pair.Key))
        {
            if (ProjectDigestPayload.Narrow(jsonPayload, projects) is not { } narrowed) continue;
            deliveries.Add((new NotificationDelivery
            {
                RecipientUserId = userId,
                ChannelId = null,
                EventType = eventType,
                Subject = BuildDigestSubject(eventType, projects.Count),
                PayloadJson = narrowed,
                // Until the e-mail below says otherwise: recorded for the user, nothing sent.
                Status = NotificationDeliveryStatus.NotConfigured,
                ErrorMessage = null,
                SentAt = null
            }, recipient.Email, ProjectDigestPayload.SingleOrganizationId(narrowed)));
        }

        if (deliveries.Count == 0) return 0;
        if (descriptor.SentByEmail)
        {
            // The template of an organization applies to a mail that is only about its projects.
            foreach (var group in deliveries.GroupBy(item => item.OrganizationId))
                await EmailOptedInDeliveriesAsync(
                    [.. group.Select(item => item.Delivery)], [.. group.Select(item => item.Address)], null, null, group.Key, ct).ConfigureAwait(false);
        }

        await repo.AddDeliveriesAsync([.. deliveries.Select(item => item.Delivery)], ct).ConfigureAwait(false);
        await PushChangedAsync([.. deliveries.Select(item => item.Delivery.RecipientUserId)], ct).ConfigureAwait(false);
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
        string? onceMarker, int? organizationId, CancellationToken ct)
    {
        var descriptor = optInEventType is null ? null : NotificationEventTypes.Find(optInEventType);
        var alreadyEmailed = onceMarker is null || deliveries.Count == 0
            ? []
            : await repo.GetRecipientsAlreadyEmailedAsync(
                deliveries[0].EventType, onceMarker, [.. deliveries.Select(delivery => delivery.RecipientUserId)], ct).ConfigureAwait(false);
        // One query for every recipient's saved choice, not one per delivery.
        var savedChoices = optInEventType is null || deliveries.Count == 0
            ? []
            : await repo.GetSavedChoicesAsync(
                optInEventType, [.. deliveries.Select(delivery => delivery.RecipientUserId)], ct).ConfigureAwait(false);
        var pipelineRefusals = await PipelineFailureMailRefusalsAsync(deliveries, ct).ConfigureAwait(false);
        var mailed = new List<NotificationDelivery>();
        var mailedAddresses = new List<string?>();
        for (var i = 0; i < deliveries.Count; i++)
        {
            if (pipelineRefusals.TryGetValue(deliveries[i].RecipientUserId, out var refusal))
            {
                deliveries[i].ErrorMessage = refusal;
                continue;
            }
            if (optInEventType is not null)
            {
                if (!(savedChoices.TryGetValue(deliveries[i].RecipientUserId, out var enabled)
                        ? enabled
                        : descriptor?.DefaultEnabled ?? false))
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

        await EmailDeliveriesAsync(mailed, mailedAddresses, ct, organizationId).ConfigureAwait(false);
    }

    /// <summary>The recipients of a pipeline failure who must not be e-mailed it, with the reason their in-app delivery
    /// carries (the delivery itself is written either way). A recipient is mailed when they saved "on" for the pipeline,
    /// or saved nothing and the pipeline is not started by a schedule: a nightly does not mail by default, a user who
    /// wants it turns it on. Any other event, or a payload without a known pipeline, refuses nobody.</summary>
    private async Task<Dictionary<int, string>> PipelineFailureMailRefusalsAsync(
        IReadOnlyList<NotificationDelivery> deliveries, CancellationToken ct)
    {
        if (deliveries.Count == 0 || deliveries[0].EventType != NotificationEventTypes.PipelineFailed) return [];
        if (ReadInt32(deliveries[0].PayloadJson, "PipelineId") is not { } pipelineId) return [];
        if (await repo.GetPipelineMailInfoAsync(pipelineId, ct).ConfigureAwait(false) is not { } pipeline) return [];
        var saved = await repo.GetSavedPipelineChoicesAsync(
            NotificationEventTypes.PipelineFailed, pipelineId, [.. deliveries.Select(delivery => delivery.RecipientUserId)], ct)
            .ConfigureAwait(false);

        var refusals = new Dictionary<int, string>();
        foreach (var delivery in deliveries)
        {
            var userId = delivery.RecipientUserId;
            var hasChoice = saved.TryGetValue(userId, out var enabled);
            if (hasChoice ? enabled : !pipeline.IsScheduled) continue;
            refusals[userId] = hasChoice
                ? "E-mail not sent: the recipient turned off failure e-mails for this pipeline."
                : "E-mail not sent: the pipeline is started by a schedule, so its failure e-mails are off unless the recipient turned them on.";
        }
        return refusals;
    }

    /// <summary> Every notification also leaves by e-mail to the recipient's account address, and
    /// its row records the real outcome: Sent once the SMTP server accepted it, Failed with the
    /// server's text, NotConfigured with the reason when no sender or no address exists. </summary>
    private async Task EmailDeliveriesAsync(
        IReadOnlyList<NotificationDelivery> deliveries, IReadOnlyList<string?> addresses, CancellationToken ct,
        int? organizationId = null)
    {
        if (emailer is null || deliveries.Count == 0) return;
        var emails = deliveries
            .Select((delivery, index) => new NotificationEmail(
                addresses[index], delivery.Subject, delivery.EventType, delivery.PayloadJson, organizationId))
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
    internal static int? ReadProjectId(string jsonPayload) => ReadInt32(jsonPayload, "ProjectId");

    /// <summary>The integer property <paramref name="name"/> (any case) at the root of the payload, or null when it is
    /// absent, null or not an integer.</summary>
    private static int? ReadInt32(string jsonPayload, string name)
    {
        if (string.IsNullOrWhiteSpace(jsonPayload)) return null;
        try
        {
            using var document = JsonDocument.Parse(jsonPayload);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out var value))
                    return value;
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

    /// <summary>The subject of an event about several projects: it names their number, never one of them.</summary>
    internal static string BuildDigestSubject(string eventType, int projectCount) =>
        projectCount == 1 ? $"{eventType} in 1 project" : $"{eventType} in {projectCount} projects";

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
