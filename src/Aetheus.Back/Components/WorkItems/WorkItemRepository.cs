// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.WorkItems;

public class WorkItemRepository(AppDbContext db) : IWorkItemRepository
{
    public async Task<(List<WorkItem> Items, int TotalCount)> GetWorkItemsPagedAsync(
        int? projectId, string? search, string? sortBy, bool sortDescending,
        int page, int pageSize, WorkItemType? type = null,
        WorkItemStatus? status = null, int? assigneeUserId = null,
        CancellationToken ct = default)
    {
        var query = FilterWorkItems(projectId, search, type, status, assigneeUserId);
        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await OrderWorkItems(query, sortBy, sortDescending)
            .Include(w => w.Assignee)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        await HideOutsideAssigneesAsync(items, ct).ConfigureAwait(false);
        return (items, totalCount);
    }

    private IQueryable<WorkItem> FilterWorkItems(
        int? projectId, string? search, WorkItemType? type,
        WorkItemStatus? status, int? assigneeUserId)
    {
        var query = db.WorkItems.AsNoTracking().AsQueryable();
        if (projectId.HasValue)
            query = query.Where(w => w.ProjectId == projectId.Value);
        if (type.HasValue)
            query = query.Where(w => w.Type == type.Value);
        if (status.HasValue)
            query = query.Where(w => w.Status == status.Value);
        if (assigneeUserId.HasValue)
            query = query.Where(w => w.AssigneeUserId == assigneeUserId.Value);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(w => w.Title.Contains(search));
        return query;
    }

    private static IOrderedQueryable<WorkItem> OrderWorkItems(
        IQueryable<WorkItem> query, string? sortBy, bool sortDescending) =>
        sortBy?.ToLowerInvariant() switch
        {
            "priority" => sortDescending ? query.OrderByDescending(w => w.Priority) : query.OrderBy(w => w.Priority),
            "status" => sortDescending ? query.OrderByDescending(w => w.Status) : query.OrderBy(w => w.Status),
            "type" => sortDescending ? query.OrderByDescending(w => w.Type) : query.OrderBy(w => w.Type),
            "createdat" => sortDescending ? query.OrderByDescending(w => w.CreatedAt) : query.OrderBy(w => w.CreatedAt),
            _ => sortDescending ? query.OrderByDescending(w => w.Order) : query.OrderBy(w => w.Order)
        };

    public async Task<WorkItem?> GetWorkItemDetailAsync(int id, CancellationToken ct = default)
    {
        var item = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Id == id)
            .Include(w => w.Assignee)
            .Include(w => w.Children)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (item is not null)
            await HideOutsideAssigneesAsync([item], ct).ConfigureAwait(false);
        return item;
    }

    public async Task<WorkItem?> FindWorkItemAsync(int id, CancellationToken ct = default)
    {
        return await db.WorkItems.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task AddWorkItemAsync(WorkItem workItem, CancellationToken ct = default)
    {
        db.WorkItems.Add(workItem);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveWorkItemAsync(WorkItem workItem, CancellationToken ct = default)
    {
        db.WorkItems.Remove(workItem);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // The kanban board is intrinsically unpaginated (it renders every column at once), but a hard
    // safety cap keeps one abnormally large project from materializing an unbounded result set (audit).
    private const int BoardItemsSafetyCap = 5000;

    public async Task<List<WorkItem>> GetBoardItemsAsync(int projectId, CancellationToken ct = default)
    {
        var items = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.ProjectId == projectId)
            .Include(w => w.Assignee)
            .OrderBy(w => w.Order)
            .Take(BoardItemsSafetyCap)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        await HideOutsideAssigneesAsync(items, ct).ConfigureAwait(false);
        return items;
    }

    public async Task<bool> IsActiveProjectOrganizationMemberAsync(
        int projectId, int userId, CancellationToken ct = default) =>
        await db.Projects.AsNoTracking()
            .Where(project => project.Id == projectId)
            .AnyAsync(project => db.OrganizationMembers.Any(member =>
                member.OrganizationId == project.OrganizationId
                && member.UserId == userId
                && member.User.IsActive), ct)
            .ConfigureAwait(false);

    public async Task<bool> PipelineRunBelongsToProjectAsync(
        int runId, int projectId, CancellationToken ct = default) =>
        await db.PipelineRuns.AsNoTracking()
            .Where(run => run.Id == runId)
            .AnyAsync(run => run.Pipeline.ProjectId == projectId
                || run.Pipeline.Environment != null && run.Pipeline.Environment.ProjectId == projectId
                || run.Pipeline.ProjectServer != null && run.Pipeline.ProjectServer.ProjectId == projectId, ct)
            .ConfigureAwait(false);

    /// <summary>Clears the loaded assignee of every item whose assignee is not (or no longer) an active
    /// member of the item's project organization, so a list or a detail never names a user of another
    /// organization (multi-organization isolation audit).</summary>
    private async Task HideOutsideAssigneesAsync(IReadOnlyCollection<WorkItem> items, CancellationToken ct)
    {
        var assigned = items.Where(item => item.Assignee is not null).Select(item => item.Id).ToList();
        if (assigned.Count == 0) return;
        var visible = (await db.WorkItems.AsNoTracking()
            .Where(item => assigned.Contains(item.Id))
            .Where(item => db.OrganizationMembers.Any(member =>
                member.OrganizationId == item.Project.OrganizationId
                && member.UserId == item.AssigneeUserId
                && member.User.IsActive))
            .Select(item => item.Id)
            .ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        foreach (var item in items)
            if (item.Assignee is not null && !visible.Contains(item.Id))
                item.Assignee = null;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
