// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary>The project board opens the work item dialog for a new item and for a clicked card; only a dialog
/// that saved reloads the board.</summary>
public sealed class ProjectBoardEditDialogTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialogs;

    public ProjectBoardEditDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialogs = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse("api/work-items/board/1", new List<WorkItemBoardColumn>
        {
            new()
            {
                Status = WorkItemStatus.New,
                Items = [new WorkItemDto { Id = 1, ProjectId = 1, Title = "Design login", Type = WorkItemType.Feature, Status = WorkItemStatus.New }]
            },
        });
    }

    private IRenderedComponent<ProjectBoardSection> RenderLoaded()
    {
        var cut = Render<ProjectBoardSection>(parameters => parameters.Add(section => section.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("Design login"));
        return cut;
    }

    private int BoardReads() => _handler.Requests.Count(request => request.Url.Contains("api/work-items/board/1", StringComparison.Ordinal));

    [Fact]
    public async Task ACreatedItem_ReloadsTheBoard()
    {
        var cut = RenderLoaded();
        var before = BoardReads();
        _dialogs.OpenResult = true;

        await cut.FindAll("button").First(button => button.TextContent.Contains("Create", StringComparison.Ordinal)).ClickAsync(new());

        Assert.Equal(typeof(WorkItemEditDialog), _dialogs.LastComponent);
        Assert.Equal("Create", _dialogs.LastTitle);
        Assert.Equal(1, _dialogs.LastParameters!["ProjectId"]);
        Assert.Null(_dialogs.LastParameters["Item"]);
        Assert.Equal(before + 1, BoardReads());
    }

    [Fact]
    public async Task AClickedCard_OpensItsItem_AndADismissedDialogKeepsTheBoard()
    {
        var cut = RenderLoaded();
        var before = BoardReads();
        _dialogs.OpenResult = false;

        await cut.Find("div[draggable]").ClickAsync(new());

        Assert.Equal("Edit", _dialogs.LastTitle);
        Assert.Equal(1, Assert.IsType<WorkItemDto>(_dialogs.LastParameters!["Item"]).Id);
        Assert.Equal(before, BoardReads());
    }
}
