// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.WorkItems;

public class WorkItemService(IWorkItemRepository repo, IAuditService audit, TimeProvider timeProvider) : IWorkItemService
{
    public async Task<PaginatedResult<WorkItemDto>> GetWorkItemsAsync(WorkItemPaginationRequest request, CancellationToken ct = default)
    {
        var pageSize = PaginationDefaults.Clamp(request.PageSize);
        var (items, totalCount) = await repo.GetWorkItemsPagedAsync(
            request.ProjectId, request.Search, request.SortBy, request.SortDescending,
            request.Page, pageSize, request.Type, request.Status, request.AssigneeUserId, ct).ConfigureAwait(false);

        return new PaginatedResult<WorkItemDto>
        {
            Items = items.Select(MapToDto).ToList(),
            TotalCount = totalCount,
            Page = request.Page,
            PageSize = pageSize
        };
    }

    public async Task<WorkItemDetailDto?> GetWorkItemDetailAsync(int id, CancellationToken ct = default)
    {
        var entity = await repo.GetWorkItemDetailAsync(id, ct).ConfigureAwait(false);
        if (entity is null) return null;

        return new WorkItemDetailDto
        {
            Id = entity.Id,
            ProjectId = entity.ProjectId,
            Type = entity.Type,
            Title = entity.Title,
            Description = entity.Description,
            Status = entity.Status,
            AssigneeUserId = entity.AssigneeUserId,
            AssigneeName = entity.Assignee?.Username,
            ParentId = entity.ParentId,
            Priority = entity.Priority,
            Order = entity.Order,
            Tags = TagsHelper.DeserializeTags(entity.Tags),
            LinkedPipelineRunId = entity.LinkedPipelineRunId,
            ExternalId = entity.ExternalId,
            ExternalUrl = entity.ExternalUrl,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            Children = entity.Children.Select(MapToDto).ToList()
        };
    }

    public async Task<WorkItemDto> CreateWorkItemAsync(CreateWorkItemRequest request, CancellationToken ct = default)
    {
        if (request.ParentId.HasValue)
        {
            var parent = await repo.FindWorkItemAsync(request.ParentId.Value, ct).ConfigureAwait(false);
            if (parent is null || parent.ProjectId != request.ProjectId)
                throw new BadRequestException("Parent work item must belong to the same project.");
        }
        await ValidateReferencesAsync(request.ProjectId, request.AssigneeUserId, request.LinkedPipelineRunId, ct)
            .ConfigureAwait(false);

        var entity = new WorkItem
        {
            ProjectId = request.ProjectId,
            Type = request.Type,
            Title = request.Title,
            Description = request.Description,
            Status = request.Status,
            AssigneeUserId = request.AssigneeUserId,
            ParentId = request.ParentId,
            Priority = request.Priority,
            Tags = SerializeTags(request.Tags),
            LinkedPipelineRunId = request.LinkedPipelineRunId,
            ExternalId = request.ExternalId,
            ExternalUrl = request.ExternalUrl
        };

        await repo.AddWorkItemAsync(entity, ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "WorkItem", entity.Id, $"{request.Type}: {request.Title}", ct).ConfigureAwait(false);
        return MapToDto(entity);
    }

    public async Task<WorkItemDto?> UpdateWorkItemAsync(int id, UpdateWorkItemRequest request, CancellationToken ct = default)
    {
        var entity = await repo.FindWorkItemAsync(id, ct).ConfigureAwait(false);
        if (entity is null) return null;
        // An unchanged reference was accepted when it was set; only a new one is checked.
        await ValidateReferencesAsync(
            entity.ProjectId,
            request.AssigneeUserId == entity.AssigneeUserId ? null : request.AssigneeUserId,
            request.LinkedPipelineRunId == entity.LinkedPipelineRunId ? null : request.LinkedPipelineRunId,
            ct).ConfigureAwait(false);

        entity.Type = request.Type;
        entity.Title = request.Title;
        entity.Description = request.Description;
        entity.Status = request.Status;
        entity.AssigneeUserId = request.AssigneeUserId;

        if (request.ParentId.HasValue && request.ParentId != entity.ParentId)
        {
            if (request.ParentId.Value == entity.Id)
                throw new BadRequestException("A work item cannot be its own parent.");

            var parent = await repo.FindWorkItemAsync(request.ParentId.Value, ct).ConfigureAwait(false);
            if (parent is null || parent.ProjectId != entity.ProjectId)
                throw new BadRequestException("Parent work item must belong to the same project.");

            // Cycle guard: walk the proposed parent's ancestry; if it passes through this item, the
            // re-parent would create a loop (corrupting the hierarchy / risking infinite traversal).
            var ancestor = parent;
            var depth = 0;
            while (ancestor?.ParentId is { } ancestorParentId && depth++ < 1000)
            {
                if (ancestorParentId == entity.Id)
                    throw new BadRequestException("Parent work item cannot be a descendant (would create a cycle).");
                ancestor = await repo.FindWorkItemAsync(ancestorParentId, ct).ConfigureAwait(false);
            }
        }
        entity.ParentId = request.ParentId;
        entity.Priority = request.Priority;
        entity.Order = request.Order;
        entity.Tags = SerializeTags(request.Tags);
        entity.LinkedPipelineRunId = request.LinkedPipelineRunId;
        entity.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "WorkItem", id, null, ct).ConfigureAwait(false);
        return MapToDto(entity);
    }

    public async Task<WorkItemDto?> MoveWorkItemAsync(int id, MoveWorkItemRequest request, CancellationToken ct = default)
    {
        var entity = await repo.FindWorkItemAsync(id, ct).ConfigureAwait(false);
        if (entity is null) return null;

        entity.Status = request.Status;
        entity.Order = request.Order;
        entity.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Moved", "WorkItem", id, $"-> {request.Status}", ct).ConfigureAwait(false);
        return MapToDto(entity);
    }

    public async Task<bool> DeleteWorkItemAsync(int id, CancellationToken ct = default)
    {
        var entity = await repo.FindWorkItemAsync(id, ct).ConfigureAwait(false);
        if (entity is null) return false;

        await repo.RemoveWorkItemAsync(entity, ct).ConfigureAwait(false);
        await audit.LogAsync("Deleted", "WorkItem", id, null, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<int?> GetProjectIdForItemAsync(int workItemId, CancellationToken ct = default)
    {
        var e = await repo.FindWorkItemAsync(workItemId, ct).ConfigureAwait(false);
        return e?.ProjectId;
    }

    public async Task<List<WorkItemBoardColumn>> GetBoardAsync(int projectId, CancellationToken ct = default)
    {
        var items = await repo.GetBoardItemsAsync(projectId, ct).ConfigureAwait(false);
        var dtos = items.Select(MapToDto).ToList();

        return Enum.GetValues<WorkItemStatus>()
            .Select(status => new WorkItemBoardColumn
            {
                Status = status,
                Items = dtos.Where(d => d.Status == status).ToList()
            })
            .ToList();
    }

    // Multi-organization isolation audit: an assignee outside the project's organization would be named
    // to every reader of the item, and a run of another project would be linked to it.
    private async Task ValidateReferencesAsync(
        int projectId, int? assigneeUserId, int? linkedPipelineRunId, CancellationToken ct)
    {
        if (assigneeUserId is { } assignee
            && !await repo.IsActiveProjectOrganizationMemberAsync(projectId, assignee, ct).ConfigureAwait(false))
            throw new BadRequestException("The assignee must be an active member of the project's organization.");
        if (linkedPipelineRunId is { } runId
            && !await repo.PipelineRunBelongsToProjectAsync(runId, projectId, ct).ConfigureAwait(false))
            throw new BadRequestException("The linked pipeline run must belong to the same project.");
    }

    private static WorkItemDto MapToDto(WorkItem w) => new()
    {
        Id = w.Id,
        ProjectId = w.ProjectId,
        Type = w.Type,
        Title = w.Title,
        Description = w.Description,
        Status = w.Status,
        AssigneeUserId = w.AssigneeUserId,
        AssigneeName = w.Assignee?.Username,
        ParentId = w.ParentId,
        Priority = w.Priority,
        Order = w.Order,
        Tags = TagsHelper.DeserializeTags(w.Tags),
        LinkedPipelineRunId = w.LinkedPipelineRunId,
        ExternalUrl = w.ExternalUrl,
        CreatedAt = w.CreatedAt,
        UpdatedAt = w.UpdatedAt
    };

    private static string SerializeTags(List<string> tags)
    {
        return tags.Count == 0 ? string.Empty : JsonSerializer.Serialize(tags);
    }
}
