// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Projects;

public class ProjectLogsSectionRenderTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectLogsSectionRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void Renders_EmptyLogs()
    {
        _handler.SetJsonResponse("api/projects/1/logs", new PaginatedResult<TaskLogDto> { Items = [], TotalCount = 0 });
        var cut = Render<ProjectLogsSection>(p => p.Add(x => x.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("Logs"));
        Assert.Contains("Logs", cut.Markup);
        Assert.Contains("NoLogs", cut.Markup);
    }

    [Fact]
    public void Renders_WithLogs()
    {
        _handler.SetJsonResponse("api/projects/1/logs", new PaginatedResult<TaskLogDto>
        {
            Items =
            [
                new TaskLogDto { Id = 1, Message = "Deployment log", Level = TaskLogLevel.Info, Timestamp = DateTime.UtcNow }
            ],
            TotalCount = 1
        });
        var cut = Render<ProjectLogsSection>(p => p.Add(x => x.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("Deployment log"));
        Assert.Contains("Deployment log", cut.Markup);
    }
}
