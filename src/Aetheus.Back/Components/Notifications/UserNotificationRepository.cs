// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Notifications;

public sealed class UserNotificationRepository(AppDbContext db) : IUserNotificationRepository
{
    public async Task<(List<NotificationDelivery> Items, int Total)> GetDeliveriesPagedAsync(
        int recipientUserId, int page, int pageSize, UserNotificationPageRequest request, CancellationToken ct = default)
    {
        var query = Filter(db.NotificationDeliveries.AsNoTracking()
            .Where(delivery => delivery.RecipientUserId == recipientUserId), request);
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await Order(query.Include(delivery => delivery.Channel), request.SortBy, request.SortDescending)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }

    private static IQueryable<NotificationDelivery> Filter(IQueryable<NotificationDelivery> query, UserNotificationPageRequest request)
    {
        if (request.Status is { } wanted)
            query = query.Where(delivery => delivery.Status == wanted);
        if (request.UnreadOnly || request.IsRead == false)
            query = query.Where(delivery => delivery.ReadAt == null);
        else if (request.IsRead == true)
            query = query.Where(delivery => delivery.ReadAt != null);
        if (!string.IsNullOrWhiteSpace(request.EventType))
        {
            var eventType = request.EventType.Trim().ToLower();
            query = query.Where(delivery => delivery.EventType.ToLower().Contains(eventType));
        }
        if (!string.IsNullOrWhiteSpace(request.Subject))
        {
            var subject = request.Subject.Trim().ToLower();
            query = query.Where(delivery => delivery.Subject.ToLower().Contains(subject));
        }
        // The grid's header filters, next to the typed parameters older callers send.
        return UserNotificationListQuery.Columns.ApplyFilters(query, request.Filters);
    }

    public async Task<List<string>> GetEventTypesAsync(int recipientUserId, CancellationToken ct = default) =>
        await db.NotificationDeliveries.AsNoTracking()
            .Where(delivery => delivery.RecipientUserId == recipientUserId)
            .Select(delivery => delivery.EventType)
            .Distinct()
            .OrderBy(eventType => eventType)
            .ToListAsync(ct).ConfigureAwait(false);

    private static IOrderedQueryable<NotificationDelivery> Order(
        IQueryable<NotificationDelivery> withChannel, string? sortBy, bool sortDescending)
    {
        // Every column of /notifications sorts; the id breaks ties so paging stays stable.
        IOrderedQueryable<NotificationDelivery> ordered = (sortBy?.ToLowerInvariant(), sortDescending) switch
        {
            ("eventtype", true) => withChannel.OrderByDescending(delivery => delivery.EventType),
            ("eventtype", false) => withChannel.OrderBy(delivery => delivery.EventType),
            ("channelname", true) => withChannel.OrderByDescending(delivery => delivery.Channel!.Name),
            ("channelname", false) => withChannel.OrderBy(delivery => delivery.Channel!.Name),
            ("subject", true) => withChannel.OrderByDescending(delivery => delivery.Subject),
            ("subject", false) => withChannel.OrderBy(delivery => delivery.Subject),
            ("status", true) => withChannel.OrderByDescending(delivery => delivery.Status),
            ("status", false) => withChannel.OrderBy(delivery => delivery.Status),
            (_, true) => withChannel.OrderByDescending(delivery => delivery.CreatedAt),
            (_, false) => withChannel.OrderBy(delivery => delivery.CreatedAt)
        };
        return sortDescending ? ordered.ThenByDescending(delivery => delivery.Id) : ordered.ThenBy(delivery => delivery.Id);
    }

    public Task<int> CountUnreadAsync(int recipientUserId, CancellationToken ct = default) =>
        db.NotificationDeliveries.AsNoTracking()
            .CountAsync(delivery => delivery.RecipientUserId == recipientUserId && delivery.ReadAt == null, ct);

    public Task<NotificationDelivery?> FindDeliveryForRecipientAsync(
        int id, int recipientUserId, CancellationToken ct = default) =>
        db.NotificationDeliveries
            .FirstOrDefaultAsync(delivery => delivery.Id == id && delivery.RecipientUserId == recipientUserId, ct);

    public async Task<int> MarkAllReadAsync(int recipientUserId, DateTime readAt, CancellationToken ct = default)
    {
        var unread = db.NotificationDeliveries
            .Where(delivery => delivery.RecipientUserId == recipientUserId && delivery.ReadAt == null);
        if (db.Database.IsRelational())
        {
            return await unread
                .ExecuteUpdateAsync(setters => setters.SetProperty(delivery => delivery.ReadAt, readAt), ct)
                .ConfigureAwait(false);
        }

        // The InMemory provider used by the unit tests has no bulk update.
        var rows = await unread.ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
            row.ReadAt = readAt;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }

    public async Task AddDeliveriesAsync(IReadOnlyCollection<NotificationDelivery> deliveries, CancellationToken ct = default)
    {
        db.NotificationDeliveries.AddRange(deliveries);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<List<NotificationRecipient>> GetProjectRecipientsAsync(
        int projectId, string eventType, bool enabledByDefault, CancellationToken ct = default) =>
        db.ProjectSubscriptions.AsNoTracking()
            .Where(subscription => subscription.ProjectId == projectId && subscription.User.IsActive)
            .Where(subscription =>
                db.UserNotificationPreferences.Any(preference =>
                    preference.UserId == subscription.UserId
                    && preference.EventType == eventType
                    && preference.IsEnabled)
                || (enabledByDefault
                    && !db.UserNotificationPreferences.Any(preference =>
                        preference.UserId == subscription.UserId && preference.EventType == eventType)))
            .OrderBy(subscription => subscription.UserId)
            .Select(subscription => new NotificationRecipient(subscription.UserId, subscription.User.Username, subscription.User.Email))
            .ToListAsync(ct);

    public Task<string?> GetProjectNameAsync(int projectId, CancellationToken ct = default) =>
        db.Projects.AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => project.Name)
            .FirstOrDefaultAsync(ct);

    public Task<List<UserNotificationPreference>> GetPreferencesAsync(int userId, CancellationToken ct = default) =>
        db.UserNotificationPreferences.AsNoTracking()
            .Where(preference => preference.UserId == userId)
            .ToListAsync(ct);

    public async Task<Dictionary<int, bool>> GetSavedChoicesAsync(
        string eventType, IReadOnlyCollection<int> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.UserNotificationPreferences.AsNoTracking()
            .Where(preference => preference.EventType == eventType && ids.Contains(preference.UserId))
            .ToDictionaryAsync(preference => preference.UserId, preference => preference.IsEnabled, ct)
            .ConfigureAwait(false);
    }

    public async Task<HashSet<int>> GetRecipientsAlreadyEmailedAsync(
        string eventType, string payloadMarker, IReadOnlyCollection<int> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var emailed = await db.NotificationDeliveries.AsNoTracking()
            .Where(delivery => ids.Contains(delivery.RecipientUserId)
                && delivery.EventType == eventType
                && delivery.ChannelId == null
                && delivery.Status == NotificationDeliveryStatus.Sent
                && delivery.PayloadJson.Contains(payloadMarker))
            .Select(delivery => delivery.RecipientUserId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return [.. emailed];
    }

    public async Task SavePreferencesAsync(
        int userId, IReadOnlyDictionary<string, bool> preferences, CancellationToken ct = default)
    {
        var eventTypes = preferences.Keys.ToList();
        var existing = await db.UserNotificationPreferences
            .Where(preference => preference.UserId == userId && eventTypes.Contains(preference.EventType))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var (eventType, isEnabled) in preferences)
        {
            var row = existing.FirstOrDefault(preference => preference.EventType == eventType);
            if (row is null)
                db.UserNotificationPreferences.Add(new UserNotificationPreference
                {
                    UserId = userId,
                    EventType = eventType,
                    IsEnabled = isEnabled
                });
            else
                row.IsEnabled = isEnabled;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<List<ProjectSubscription>> GetSubscriptionsAsync(int userId, CancellationToken ct = default) =>
        db.ProjectSubscriptions.AsNoTracking()
            .Where(subscription => subscription.UserId == userId)
            .Include(subscription => subscription.Project)
            .OrderBy(subscription => subscription.Project.Name)
            .ThenBy(subscription => subscription.ProjectId)
            .ToListAsync(ct);

    public Task<ProjectSubscription?> FindSubscriptionAsync(int userId, int projectId, CancellationToken ct = default) =>
        db.ProjectSubscriptions.AsNoTracking()
            .Include(subscription => subscription.Project)
            .FirstOrDefaultAsync(subscription => subscription.UserId == userId && subscription.ProjectId == projectId, ct);

    public async Task AddSubscriptionAsync(ProjectSubscription subscription, CancellationToken ct = default)
    {
        db.ProjectSubscriptions.Add(subscription);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveSubscriptionAsync(int userId, int projectId, CancellationToken ct = default)
    {
        var subscription = await db.ProjectSubscriptions
            .FirstOrDefaultAsync(item => item.UserId == userId && item.ProjectId == projectId, ct)
            .ConfigureAwait(false);
        if (subscription is null) return false;
        db.ProjectSubscriptions.Remove(subscription);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<int> AddMissingSubscriptionsAsync(
        int userId, IReadOnlyCollection<int>? projectIds, CancellationToken ct = default)
    {
        var projects = db.Projects.AsNoTracking().AsQueryable();
        if (projectIds is not null)
            projects = projects.Where(project => projectIds.Contains(project.Id));
        var missing = await projects
            .Where(project => !db.ProjectSubscriptions.Any(subscription =>
                subscription.UserId == userId && subscription.ProjectId == project.Id))
            .Select(project => project.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        if (missing.Count == 0) return 0;
        db.ProjectSubscriptions.AddRange(missing.Select(projectId =>
            new ProjectSubscription { UserId = userId, ProjectId = projectId }));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return missing.Count;
    }

    public Task<List<NotificationRecipient>> GetActiveAdministratorsAsync(CancellationToken ct = default) =>
        db.Users.AsNoTracking()
            .Where(user => user.IsActive && user.UserRoles.Any(link => link.Role.Name == "Admin"))
            .OrderBy(user => user.Id)
            .Select(user => new NotificationRecipient(user.Id, user.Username, user.Email))
            .ToListAsync(ct);

    public Task<List<int>> GetServerProjectIdsAsync(int serverId, CancellationToken ct = default) =>
        db.ProjectServers.AsNoTracking()
            .Where(link => link.ServerId == serverId)
            .Select(link => link.ProjectId)
            .Distinct()
            .OrderBy(projectId => projectId)
            .ToListAsync(ct);

    public async Task<(int ProjectId, string Version)?> GetReleaseProjectAsync(int releaseId, CancellationToken ct = default)
    {
        var release = await db.Releases.AsNoTracking()
            .Where(item => item.Id == releaseId)
            .Select(item => new { item.ProjectId, item.Version })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return release is null ? null : (release.ProjectId, release.Version);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
}
