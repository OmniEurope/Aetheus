// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerProjectsSectionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerProjectsSectionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void Renders_EmptyProjects_ShowsEmptyText_WithoutTitleHeading()
    {
        _handler.SetJsonResponse("api/servers/1/projects", new PaginatedResult<ProjectDto>());
        var cut = Render<ServerProjectsSection>(p => p.Add(x => x.ServerId, 1));
        cut.WaitForAssertion(() => Assert.Contains("NoRecords", cut.Markup));
        // The title heading is gone: the page header already shows it.
        Assert.DoesNotContain(">Projects<", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_WithProjects_ShowsProjectNameAndLink()
    {
        _handler.SetJsonResponse("api/servers/1/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new() { Id = 7, Name = "MyProject", Status = ProjectStatus.Active }],
            TotalCount = 1
        });
        var cut = Render<ServerProjectsSection>(p => p.Add(x => x.ServerId, 1));
        cut.WaitForAssertion(() => Assert.Contains("MyProject", cut.Markup));
        // The project name renders as a link to the project overview, and the Active status badge shows.
        Assert.Contains("/projects/7/overview", cut.Markup);
        Assert.Contains("Active", cut.Markup);
        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("page=1", StringComparison.Ordinal) &&
            request.Url.Contains("pageSize=25", StringComparison.Ordinal));
    }

    [Fact]
    public void ServerIdChange_ReloadsProjectsOnSameInstance()
    {
        _handler.SetJsonResponse("api/servers/1/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "first-project" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/servers/2/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 2, Name = "second-project" }],
            TotalCount = 1
        });
        var cut = Render<ServerProjectsSection>(p => p.Add(x => x.ServerId, 1));

        cut.Render(p => p.Add(x => x.ServerId, 2));

        cut.WaitForAssertion(() => Assert.Contains("second-project", cut.Markup));
        Assert.DoesNotContain("first-project", cut.Markup);
    }

    [Fact]
    public void RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        _handler.SetJsonResponse("api/servers/1/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new() { Id = 7, Name = "MyProject", Status = ProjectStatus.Active }],
            TotalCount = 1
        });

        var cut = Render<ServerProjectsSection>(p => p.Add(x => x.ServerId, 1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 1);
    }
}
