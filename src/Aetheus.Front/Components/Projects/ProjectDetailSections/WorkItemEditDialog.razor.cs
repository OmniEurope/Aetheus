// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

public partial class WorkItemEditDialog : EntityEditDialogBase
{
    [Parameter] public int ProjectId { get; set; }
    [Parameter] public WorkItemDto? Item { get; set; }

    private bool IsEdit => Item is not null;
    private EditModel _model = new();

    private List<OmniOption<WorkItemType>> TypeOptions => Enum.GetValues<WorkItemType>()
        .Select(t => new OmniOption<WorkItemType>(t, L.Localize(t))).ToList();

    private List<OmniOption<WorkItemStatus>> StatusOptions => Enum.GetValues<WorkItemStatus>()
        .Select(s => new OmniOption<WorkItemStatus>(s, L.Localize(s))).ToList();

    protected override void OnInitialized()
    {
        if (Item is { } i)
        {
            _model = new EditModel
            {
                Title = i.Title,
                Type = i.Type,
                Status = i.Status,
                Priority = i.Priority,
                Description = i.Description,
                TagsCsv = string.Join(", ", i.Tags)
            };
        }
    }

    private Task SubmitAsync() => RunBusyAsync(async () =>
    {
        var tags = (_model.TagsCsv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct().ToList();

        if (IsEdit)
        {
            await Ui.RunAsync(
                () => Api.Pipelines.UpdateWorkItemAsync(Item!.Id, new UpdateWorkItemRequest
                {
                    Type = _model.Type,
                    Title = _model.Title,
                    Description = _model.Description,
                    Status = _model.Status,
                    AssigneeUserId = Item.AssigneeUserId,
                    ParentId = Item.ParentId,
                    Priority = _model.Priority,
                    Order = Item.Order,
                    Tags = tags,
                    LinkedPipelineRunId = Item.LinkedPipelineRunId
                }),
                "Updated",
                _ => CloseAfterSuccessAsync(),
                successTitleKey: "Updated");
        }
        else
        {
            await Ui.RunAsync(
                () => Api.Pipelines.CreateWorkItemAsync(new CreateWorkItemRequest
                {
                    ProjectId = ProjectId,
                    Type = _model.Type,
                    Title = _model.Title,
                    Description = _model.Description,
                    Status = _model.Status,
                    Priority = _model.Priority,
                    Tags = tags
                }),
                "Created",
                _ => CloseAfterSuccessAsync(),
                successTitleKey: "Created");
        }
    });

    private sealed class EditModel
    {
        [Required]
        [StringLength(300)]
        public string Title { get; set; } = string.Empty;

        public WorkItemType Type { get; set; } = WorkItemType.Task;
        public WorkItemStatus Status { get; set; } = WorkItemStatus.New;
        public int Priority { get; set; }

        [StringLength(4000)]
        public string? Description { get; set; }

        public string? TagsCsv { get; set; }
    }
}
