// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Front follow-up of BACK-012: compose deploy, image build and container exec now take Server.Admin on
/// the backend (image delete and prune already did). A user with only Read/Write on the server was still
/// offered them and got a 403 on click; they are now offered only with Admin on that server.
/// </summary>
public sealed class ServerDockerAdminGatingTests : BunitContext
{
    private const int ServerId = 20;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerDockerAdminGatingTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse($"api/servers/{ServerId}/docker/compose/monitoring/file", "{}");
    }

    [Fact]
    public void WithoutServerAdmin_TheAdminOnlyDockerActionsAreNotOffered()
    {
        var cut = RenderSection();

        Assert.DoesNotContain("DockerPrune", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("DockerBuildImage", cut.Markup, StringComparison.Ordinal);
        Assert.False(ShellOffered(cut));
        Assert.False(ImageDeleteOffered(cut));
        Assert.False(ComposeDeployOffered(cut));
        // What Write still allows stays: pull, stack actions, the compose file itself.
        Assert.Contains("DockerPull", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WithServerAdminOnThatServer_TheyAreOffered()
    {
        GrantAdmin(ServerId);
        var cut = RenderSection();

        Assert.Contains("DockerPrune", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("DockerBuildImage", cut.Markup, StringComparison.Ordinal);
        Assert.True(ShellOffered(cut));
        Assert.True(ImageDeleteOffered(cut));
        Assert.True(ComposeDeployOffered(cut));
    }

    [Fact]
    public void AdminOnAnotherServer_DoesNotOfferThem()
    {
        GrantAdmin(ServerId + 1);
        var cut = RenderSection();

        Assert.DoesNotContain("DockerBuildImage", cut.Markup, StringComparison.Ordinal);
        Assert.False(ComposeDeployOffered(cut));
    }

    [Fact]
    public void PermissionsLoadedAfterTheSectionMounted_OfferThemWithoutANewNavigation()
    {
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("/servers/20/docker?tab=images");
        var cut = RenderSection();
        Assert.DoesNotContain("DockerBuildImage", cut.Markup, StringComparison.Ordinal);

        cut.InvokeAsync(() => GrantAdmin(ServerId));

        cut.WaitForAssertion(() => Assert.Contains("DockerPrune", cut.Markup, StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Contains("DockerBuildImage", cut.Markup, StringComparison.Ordinal));
    }

    private void GrantAdmin(int serverId) =>
        Services.GetRequiredService<PermissionService>().SetPermissions(
        [
            new EffectivePermissionDto { ResourceType = ResourceType.Server, ResourceId = null, Permission = Permission.Write },
            new EffectivePermissionDto { ResourceType = ResourceType.Server, ResourceId = serverId, Permission = Permission.Admin }
        ], isAdmin: false);

    /// <summary>The shell sits behind the running container's "..." menu, whose items exist once it is open.</summary>
    private static bool ShellOffered(IRenderedComponent<ServerDockerSection> cut)
    {
        var tab = cut.FindComponent<DockerContainersTab>();
        tab.Find("[aria-label='MoreActions']").Click();
        return tab.Markup.Contains("DockerShell", StringComparison.Ordinal);
    }

    private static bool ImageDeleteOffered(IRenderedComponent<ServerDockerSection> cut) =>
        cut.FindComponent<DockerImagesTab>().FindAll("button[title='Delete']").Count > 0;

    /// <summary>Opens the compose editor on the stack and delivers its file, as the agent's task would.</summary>
    private static bool ComposeDeployOffered(IRenderedComponent<ServerDockerSection> cut)
    {
        var tab = cut.FindComponent<DockerComposeTab>();
        tab.Find("button[title='EditCompose']").Click();
        tab.InvokeAsync(() => tab.Instance.HandleTaskOutput(DockerTaskNames.ComposeFile("monitoring"), "services: {}"));
        tab.WaitForAssertion(() => Assert.Contains("Cancel", tab.Markup, StringComparison.Ordinal), TimeSpan.FromSeconds(2));
        return tab.FindAll("button").Any(button => button.TextContent.Contains("Deploy", StringComparison.Ordinal));
    }

    private IRenderedComponent<ServerDockerSection> RenderSection() => Render<ServerDockerSection>(p => p
        .Add(x => x.Server, new ServerDetailDto
        {
            Id = ServerId,
            Name = "docker-srv",
            Hostname = "10.0.0.1",
            Type = ServerType.Docker,
            Status = ServerStatus.Online,
            Docker = new DockerDataDto
            {
                Containers = [new DockerContainerDto { ContainerId = "abc123def456", Name = "nginx", Image = "nginx:latest", State = "running", Status = "Up 2h" }],
                Images = [new DockerImageDto { ImageId = "i1", Repository = "nginx", Tag = "latest", Size = "142MB" }],
                ComposeStacks = [new DockerComposeStackDto { Name = "monitoring", Status = "running(3)", RunningCount = 3, TotalCount = 3 }]
            }
        })
        .Add(x => x.ServerId, ServerId)
        .Add(x => x.DockerInitialLoaded, true));
}
