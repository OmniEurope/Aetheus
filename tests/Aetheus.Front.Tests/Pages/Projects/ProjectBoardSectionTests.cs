// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Projects;

public class ProjectBoardSectionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectBoardSectionTests() => _handler = BunitTestHelper.RegisterServices(this);

    private void SeedBoard() => _handler.SetJsonResponse("api/work-items/board/1", new List<WorkItemBoardColumn>
    {
        new()
        {
            Status = WorkItemStatus.New,
            Items = [new WorkItemDto { Id = 1, ProjectId = 1, Title = "Design login", Type = WorkItemType.Feature, Status = WorkItemStatus.New }]
        },
        new() { Status = WorkItemStatus.Active, Items = [] }
    });

    [Fact]
    public void Renders_Columns_AndCard()
    {
        SeedBoard();

        var cut = Render<ProjectBoardSection>(p => p.Add(c => c.ProjectId, 1));

        cut.WaitForState(() => cut.Markup.Contains("Design login"));
        Assert.Contains("Design login", cut.Markup);
        Assert.Contains("Enum_WorkItemStatus_Active", cut.Markup); // column header
    }

    [Fact]
    public void Drop_MovesCard_PersistsViaApi()
    {
        SeedBoard();
        _handler.SetJsonResponse("api/work-items/1/move",
            new WorkItemDto { Id = 1, ProjectId = 1, Title = "Design login", Type = WorkItemType.Feature, Status = WorkItemStatus.Active });

        var cut = Render<ProjectBoardSection>(p => p.Add(c => c.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("Design login"));

        // Start dragging the card, then drop onto the Active column's drop zone.
        cut.FindAll(".kanban-card").First().DragStart();
        cut.FindAll(".kanban-column-drop")[1].Drop();

        // The move endpoint was hit (a null/failed move would trigger a board reload instead).
        cut.WaitForAssertion(() =>
            Assert.Contains(_handler.Requests, r => r.Url.Contains("api/work-items/1/move")));
    }
}
