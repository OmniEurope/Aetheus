// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary>
/// Recette R-83: a server the project reaches only through one of its environments is listed, says so, and offers
/// neither the edit nor the detach action, because there is no project server link behind the line.
/// </summary>
public class ProjectServersSectionEnvironmentRowTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectServersSectionEnvironmentRowTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void EnvironmentServerRow_SaysWhichEnvironmentAndOffersNoEditOrDetach()
    {
        _handler.SetPaginatedJsonResponse("api/projects/1/servers", new List<ProjectServerDto>
        {
            new() { Id = 0, ProjectId = 1, ServerId = 10, Type = ProjectServerType.AgentServer,
                DisplayName = "env-only-server", Host = "10.0.0.1", ServerStatus = ServerStatus.Online,
                ViaEnvironmentId = 5, ViaEnvironmentName = "prod" }
        });

        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 1));

        cut.WaitForAssertion(() => Assert.Contains("env-only-server", cut.Markup));
        Assert.Contains("ProjectServerViaEnvironment", cut.Markup);
        Assert.DoesNotContain("title=\"Edit\"", cut.Markup);
        Assert.DoesNotContain("title=\"Delete\"", cut.Markup);
    }

    [Fact]
    public void ProjectServerRow_KeepsItsEditAndDetachActions()
    {
        _handler.SetPaginatedJsonResponse("api/projects/1/servers", new List<ProjectServerDto>
        {
            new() { Id = 7, ProjectId = 1, ServerId = 11, Type = ProjectServerType.AgentServer,
                DisplayName = "linked-server", Host = "10.0.0.2", ServerStatus = ServerStatus.Online }
        });

        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 1));

        cut.WaitForAssertion(() => Assert.Contains("linked-server", cut.Markup));
        Assert.DoesNotContain("ProjectServerViaEnvironment", cut.Markup);
        Assert.Contains("title=\"Edit\"", cut.Markup);
        Assert.Contains("title=\"Delete\"", cut.Markup);
    }
}
