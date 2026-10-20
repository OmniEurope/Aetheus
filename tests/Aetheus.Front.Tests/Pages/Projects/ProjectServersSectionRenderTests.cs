// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Projects;

public class ProjectServersSectionRenderTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectServersSectionRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void Renders_EmptyServers()
    {
        _handler.SetPaginatedJsonResponse("api/projects/1/servers", new List<ProjectServerDto>());
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("NoProjectServers"));
        Assert.Contains("NoProjectServers", cut.Markup);
        Assert.Contains("AddServer", cut.Markup);
    }

    [Fact]
    public void Renders_WithServers()
    {
        _handler.SetPaginatedJsonResponse("api/projects/1/servers", new List<ProjectServerDto>
        {
            new()
            {
                Id = 1,
                ProjectId = 1,
                ServerId = 1,
                DisplayName = "web-01",
                Host = "web-01.local",
                Type = ProjectServerType.AgentServer,
                ServerStatus = ServerStatus.Online
            }
        });
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 1));
        // One server present → the data grid renders (its "Host"/"Port" column headers only exist on
        // the non-empty branch), not the EmptyState.
        cut.WaitForState(() => cut.Markup.Contains("Host"));
        Assert.Contains("Host", cut.Markup);
        Assert.Contains("Port", cut.Markup);
    }
}
