// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Projects;

public class ProjectTasksSectionRenderTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectTasksSectionRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void Renders_EmptyTasks()
    {
        _handler.SetJsonResponse("api/projects/1/tasks", new PaginatedResult<ServerTaskDto> { Items = [], TotalCount = 0 });
        var cut = Render<ProjectTasksSection>(p => p.Add(x => x.ProjectId, 1));
        // Identify an empty section through grid empty text; the breadcrumb provides its heading
        // and no search box is rendered.
        cut.WaitForState(() => cut.Markup.Contains("NoRecords", StringComparison.Ordinal));
        Assert.DoesNotContain("aetheus-loader-logo", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".pipeline-list-search"));
        Assert.Empty(cut.FindAll(".rz-data-row"));
    }

    [Fact]
    public void Renders_WithTasks()
    {
        _handler.SetJsonResponse("api/projects/1/tasks", new PaginatedResult<ServerTaskDto>
        {
            Items =
            [
                new ServerTaskDto
                {
                    Id = 1,
                    ServerId = 2,
                    ServerName = "runner-02",
                    PipelineRunId = 77,
                    Name = "deploy",
                    Status = TaskExecutionStatus.Success
                }
            ],
            TotalCount = 1
        });
        var cut = Render<ProjectTasksSection>(p => p.Add(x => x.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("deploy"));
        Assert.Contains("deploy", cut.Markup);
        Assert.NotNull(cut.Find("a[href='/pipelines/runs/77']"));
    }
}
