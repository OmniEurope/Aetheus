// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Notifications;

public class NotificationRepository(AppDbContext db) : INotificationRepository
{
    public async Task<(List<NotificationChannel> Items, int Total)> GetChannelsPagedAsync(
        string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null)
    {
        // The grid's column header filters, before the count.
        var query = NotificationAdminListQuery.ChannelColumns.ApplyFilters(BuildChannelQuery(search), columnFilters);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy?.Trim().ToLowerInvariant(), sortDescending) switch
        {
            ("type", false) => query.OrderBy(channel => channel.Type).ThenBy(channel => channel.Name).ThenBy(channel => channel.Id),
            ("type", true) => query.OrderByDescending(channel => channel.Type).ThenBy(channel => channel.Name).ThenBy(channel => channel.Id),
            ("isenabled", false) => query.OrderBy(channel => channel.IsEnabled).ThenBy(channel => channel.Name).ThenBy(channel => channel.Id),
            ("isenabled", true) => query.OrderByDescending(channel => channel.IsEnabled).ThenBy(channel => channel.Name).ThenBy(channel => channel.Id),
            ("rulecount", false) => query.OrderBy(channel => channel.Rules.Count).ThenBy(channel => channel.Name).ThenBy(channel => channel.Id),
            ("rulecount", true) => query.OrderByDescending(channel => channel.Rules.Count).ThenBy(channel => channel.Name).ThenBy(channel => channel.Id),
            ("createdat", false) => query.OrderBy(channel => channel.CreatedAt).ThenBy(channel => channel.Id),
            ("createdat", true) => query.OrderByDescending(channel => channel.CreatedAt).ThenBy(channel => channel.Id),
            (_, true) => query.OrderByDescending(channel => channel.Name).ThenBy(channel => channel.Id),
            _ => query.OrderBy(channel => channel.Name).ThenBy(channel => channel.Id)
        };

        var items = await query
            .Include(channel => channel.Rules)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }

    private IQueryable<NotificationChannel> BuildChannelQuery(string? search)
    {
        var query = db.NotificationChannels.AsNoTracking().AsQueryable();
        if (string.IsNullOrWhiteSpace(search)) return query;

        var pattern = $"%{search.Trim()}%";
        return query.Where(channel => EF.Functions.ILike(channel.Name, pattern));
    }

    public async Task<NotificationChannel?> GetChannelWithRulesAsync(int id, CancellationToken ct = default)
    {
        return await db.NotificationChannels
            .Include(c => c.Rules)
            .FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<NotificationChannel?> FindChannelAsync(int id, CancellationToken ct = default)
    {
        return await db.NotificationChannels.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task AddChannelAsync(NotificationChannel channel, CancellationToken ct = default)
    {
        db.NotificationChannels.Add(channel);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveChannelAsync(NotificationChannel channel, CancellationToken ct = default)
    {
        db.NotificationChannels.Remove(channel);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<NotificationRule>> GetRulesForEventAsync(string eventType, CancellationToken ct = default)
    {
        return await db.NotificationRules
            .AsNoTracking()
            .Where(r => r.IsEnabled && r.EventType == eventType && r.Channel != null && r.Channel.IsEnabled)
            .Include(r => r.Channel)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<(List<string> EventTypes, List<string> Channels)> GetRuleFilterValuesAsync(CancellationToken ct = default)
    {
        var eventTypes = await db.NotificationRules.AsNoTracking()
            .Select(rule => rule.EventType).Distinct().OrderBy(eventType => eventType)
            .ToListAsync(ct).ConfigureAwait(false);
        var channels = await db.NotificationChannels.AsNoTracking()
            .Select(channel => channel.Name).Distinct().OrderBy(name => name)
            .ToListAsync(ct).ConfigureAwait(false);
        return (eventTypes, channels);
    }

    public async Task<(List<NotificationRule> Items, int Total)> GetRulesPagedAsync(
        string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = db.NotificationRules
            .AsNoTracking()
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(rule =>
                EF.Functions.ILike(rule.EventType, pattern) ||
                EF.Functions.ILike(rule.Channel.Name, pattern));
        }
        // The grid's column header filters, before the count.
        query = NotificationAdminListQuery.RuleColumns.ApplyFilters(query, columnFilters);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy?.Trim().ToLowerInvariant(), sortDescending) switch
        {
            ("channelname", false) => query.OrderBy(rule => rule.Channel.Name).ThenBy(rule => rule.EventType).ThenBy(rule => rule.Id),
            ("channelname", true) => query.OrderByDescending(rule => rule.Channel.Name).ThenBy(rule => rule.EventType).ThenBy(rule => rule.Id),
            ("isenabled", false) => query.OrderBy(rule => rule.IsEnabled).ThenBy(rule => rule.EventType).ThenBy(rule => rule.Id),
            ("isenabled", true) => query.OrderByDescending(rule => rule.IsEnabled).ThenBy(rule => rule.EventType).ThenBy(rule => rule.Id),
            ("createdat", false) => query.OrderBy(rule => rule.CreatedAt).ThenBy(rule => rule.Id),
            ("createdat", true) => query.OrderByDescending(rule => rule.CreatedAt).ThenBy(rule => rule.Id),
            (_, true) => query.OrderByDescending(rule => rule.EventType).ThenBy(rule => rule.Id),
            _ => query.OrderBy(rule => rule.EventType).ThenBy(rule => rule.Id)
        };

        var items = await query
            .Include(rule => rule.Channel)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }

    public async Task<NotificationRule?> FindRuleAsync(int id, CancellationToken ct = default)
    {
        return await db.NotificationRules
            .Include(r => r.Channel)
            .FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
    }

    public async Task AddRuleAsync(NotificationRule rule, CancellationToken ct = default)
    {
        db.NotificationRules.Add(rule);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveRuleAsync(NotificationRule rule, CancellationToken ct = default)
    {
        db.NotificationRules.Remove(rule);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<string?> GetUserEmailAsync(int userId, CancellationToken ct = default) =>
        db.Users.AsNoTracking()
            .Where(user => user.Id == userId && user.Email != string.Empty)
            .Select(user => (string?)user.Email)
            .FirstOrDefaultAsync(ct);

    public Task<List<string>> GetActiveAdministratorEmailsAsync(CancellationToken ct = default) =>
        db.Users.AsNoTracking()
            .Where(user => user.IsActive && user.Email != string.Empty
                && user.UserRoles.Any(link => link.Role.Name == "Admin"))
            .OrderBy(user => user.Id)
            .Select(user => user.Email)
            .ToListAsync(ct);

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
