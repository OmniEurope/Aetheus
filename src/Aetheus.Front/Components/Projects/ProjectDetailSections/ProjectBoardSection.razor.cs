// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

public partial class ProjectBoardSection : ProjectWritableSectionBase
{
    private static readonly WorkItemStatus[] Columns =
        [WorkItemStatus.New, WorkItemStatus.Active, WorkItemStatus.Resolved, WorkItemStatus.Closed, WorkItemStatus.Removed];

    private List<WorkItemDto> _items = [];
    private WorkItemDto? _dragged;
    private WorkItemStatus? _dropTarget;

    private string? _assigneeFilter;
    private string? _tagFilter;

    private List<string> Assignees => _items
        .Where(i => !string.IsNullOrEmpty(i.AssigneeName))
        .Select(i => i.AssigneeName!)
        .Distinct().OrderBy(n => n).ToList();

    private List<string> Tags => _items
        .SelectMany(i => i.Tags)
        .Distinct().OrderBy(t => t).ToList();

    protected override async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var board = await Api.Pipelines.GetWorkItemBoardAsync(ProjectId);
            _items = board.SelectMany(c => c.Items).ToList();
        }
        catch (HttpRequestException) { _items = []; }
        finally { _loading = false; }
    }

    private IEnumerable<WorkItemDto> ColumnItems(WorkItemStatus status) => _items
        .Where(i => i.Status == status)
        .Where(i => _assigneeFilter is null || i.AssigneeName == _assigneeFilter)
        .Where(i => _tagFilter is null || i.Tags.Contains(_tagFilter))
        .OrderBy(i => i.Order).ThenByDescending(i => i.Priority);

    private void OnDragStart(WorkItemDto item)
    {
        if (_canWrite) _dragged = item;
    }

    private void OnDragEnd()
    {
        _dragged = null;
        _dropTarget = null;
    }

    private async Task OnDropAsync(WorkItemStatus status)
    {
        var item = _dragged;
        _dropTarget = null;
        _dragged = null;
        if (item is null || !_canWrite || item.Status == status) return;

        // Optimistic local move; the target order is one past the current max in that column.
        var order = _items.Where(i => i.Status == status).Select(i => i.Order).DefaultIfEmpty(-1).Max() + 1;
        var idx = _items.FindIndex(i => i.Id == item.Id);
        if (idx >= 0) _items[idx] = item with { Status = status, Order = order };
        StateHasChanged();

        var result = await Api.Pipelines.MoveWorkItemAsync(item.Id, new MoveWorkItemRequest { Status = status, Order = order });
        if (result is null)
        {
            // Persist failed: reload from the server so the board never shows an unpersisted move.
            Toast.Error("Error", "SaveFailed");
            await LoadAsync();
        }
        else
        {
            Toast.Success("Saved", "Saved");
        }
    }

    private async Task OpenCreateAsync() => await OpenEditAsync(null);

    private async Task OpenEditAsync(WorkItemDto? item)
    {
        var result = await Dialog.OpenAsync<WorkItemEditDialog>(
            item is null ? L["Create"] : L["Edit"],
            new Dictionary<string, object?> { ["ProjectId"] = ProjectId, ["Item"] = item },
            new OmniDialogOptions { Width = "640px", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });
        if (result is true) await LoadAsync();
    }

    private async Task DeleteAsync(WorkItemDto item)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["DeleteWorkItemConfirm"], item.Title), L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await Veil.RunAsync(async () =>
        {
            var status = await Api.Pipelines.DeleteWorkItemAsync(item.Id);
            if (status.Success)
            {
                _items.RemoveAll(i => i.Id == item.Id);
                StateHasChanged();
                Toast.Success("Deleted", "Deleted");
            }
            else Toast.Error("Error", "DeleteFailed");
        });
    }

    private static OmniTone TypeBadge(WorkItemType type) => type switch
    {
        WorkItemType.Bug => OmniTone.Danger,
        WorkItemType.Feature => OmniTone.Success,
        WorkItemType.Epic => OmniTone.Accent,
        _ => OmniTone.Neutral
    };

}
