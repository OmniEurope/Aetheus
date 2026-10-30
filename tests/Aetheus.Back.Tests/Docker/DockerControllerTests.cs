// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Docker;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class DockerControllerTests
{
    private readonly IDockerService _service = Substitute.For<IDockerService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly DockerController _sut;

    public DockerControllerTests()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _sut = new DockerController(_service, _authz);
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
            }
        };
    }

    [Fact]
    public async Task GetContainers_ReturnsOk()
    {
        var containers = new List<DockerContainerDto> { new() { ContainerId = "c1", Name = "nginx" } };
        _service.GetContainersAsync(1, TestContext.Current.CancellationToken).Returns(containers);

        var result = await _sut.GetContainers(1, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<List<DockerContainerDto>>(ok.Value);
        Assert.Single(data);
    }

    [Fact]
    public async Task ExecuteAction_ReturnsOk()
    {
        var request = new DockerActionRequest { ContainerId = "c1", Action = DockerContainerAction.Start };

        var result = await _sut.ExecuteAction(1, request, TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
        await _service.Received(1).ExecuteActionAsync(1, request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAction_WithoutWritePermission_ForbidsAndDoesNotExecute()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);
        var request = new DockerActionRequest { ContainerId = "c1", Action = DockerContainerAction.Start };

        var result = await _sut.ExecuteAction(1, request, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
        await _service.DidNotReceive().ExecuteActionAsync(Arg.Any<int>(), Arg.Any<DockerActionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteShellCommand_WithoutAdminPermission_ForbidsAndDoesNotExec()
    {
        // docker exec is RCE-equivalent and Admin-gated; without Admin the controller must Forbid and
        // never reach the service. (Closes the negative-authz coverage gap flagged for the Docker module.)
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);
        var request = new DockerExecRequest { ContainerId = "c1", Command = "ls" };

        var result = await _sut.ExecuteShellCommand(1, request, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
        await _service.DidNotReceive().ExecuteShellCommandAsync(Arg.Any<int>(), Arg.Any<DockerExecRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetContainerLogs_WithContent_ReturnsOk()
    {
        var request = new DockerContainerLogsRequest { ContainerId = "c1", Tail = 100 };
        _service.GetContainerLogsAsync(1, request, TestContext.Current.CancellationToken).Returns("log line 1\nlog line 2");

        var result = await _sut.GetContainerLogs(1, request, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("log line 1\nlog line 2", ok.Value);
    }

    [Fact]
    public async Task GetContainerLogs_Empty_ReturnsNotFound()
    {
        var request = new DockerContainerLogsRequest { ContainerId = "c1", Tail = 100 };
        _service.GetContainerLogsAsync(1, request, TestContext.Current.CancellationToken).Returns(string.Empty);

        var result = await _sut.GetContainerLogs(1, request, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetImages_ReturnsOk()
    {
        var images = new List<DockerImageDto> { new() { ImageId = "i1", Repository = "nginx" } };
        _service.GetImagesAsync(1, TestContext.Current.CancellationToken).Returns(images);

        var result = await _sut.GetImages(1, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single((List<DockerImageDto>)ok.Value!);
    }

    [Fact]
    public async Task PullImage_ReturnsOk()
    {
        var request = new DockerPullImageRequest { Image = "nginx:latest" };

        var result = await _sut.PullImage(1, request, TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task RemoveImage_ReturnsOk()
    {
        var result = await _sut.RemoveImage(1, "sha256:abc123", TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task GetComposeStacks_ReturnsOk()
    {
        _service.GetComposeStacksAsync(1, TestContext.Current.CancellationToken)
            .Returns([new DockerComposeStackDto { Name = "stack1" }]);

        var result = await _sut.GetComposeStacks(1, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single((List<DockerComposeStackDto>)ok.Value!);
    }

    [Fact]
    public async Task ExecuteComposeAction_ReturnsOk()
    {
        var request = new DockerComposeActionRequest { StackName = "monitoring", Action = DockerComposeAction.Up };

        var result = await _sut.ExecuteComposeAction(1, request, TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task GetNetworks_ReturnsOk()
    {
        _service.GetNetworksAsync(1, TestContext.Current.CancellationToken).Returns([]);

        var result = await _sut.GetNetworks(1, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty((List<DockerNetworkDto>)ok.Value!);
    }

    [Fact]
    public async Task GetVolumes_ReturnsOk()
    {
        _service.GetVolumesAsync(1, TestContext.Current.CancellationToken).Returns([]);

        var result = await _sut.GetVolumes(1, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty((List<DockerVolumeDto>)ok.Value!);
    }

    [Fact]
    public async Task Prune_ReturnsOk()
    {
        var request = new DockerPruneRequest { Containers = true, Images = true };

        var result = await _sut.Prune(1, request, TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task UpdateResourceLimits_ReturnsOk()
    {
        var request = new DockerResourceLimitsRequest { ContainerId = "c1", CpuLimit = 1.5, MemoryLimitMb = 512 };

        var result = await _sut.UpdateResourceLimits(1, request, TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task InspectContainer_ReturnsOk()
    {
        var result = await _sut.InspectContainer(1, "abc123def456", TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task GetComposeFile_ReturnsOk()
    {
        var result = await _sut.GetComposeFile(1, "monitoring", TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task SaveComposeFileAndBuild_WithoutAdminPermission_ForbidAndQueueNothing()
    {
        // BACK-012: a Compose file or a Dockerfile is arbitrary code on the server, gated like exec.
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var compose = await _sut.SaveComposeFile(1, new DockerComposeFileSaveRequest { StackName = "web", Content = "services: {}" },
            TestContext.Current.CancellationToken);
        var build = await _sut.BuildImage(1, new DockerBuildRequest { ImageTag = "myapp:latest", DockerfileContent = "FROM alpine" },
            TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(compose);
        Assert.IsType<ForbidResult>(build);
        await _service.DidNotReceive().SaveComposeFileAsync(Arg.Any<int>(), Arg.Any<DockerComposeFileSaveRequest>(), Arg.Any<CancellationToken>());
        await _service.DidNotReceive().BuildImageAsync(Arg.Any<int>(), Arg.Any<DockerBuildRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveComposeFile_ReturnsOk()
    {
        var request = new DockerComposeFileSaveRequest { StackName = "monitoring", Content = "version: '3'" };

        var result = await _sut.SaveComposeFile(1, request, TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task ExecuteShellCommand_ReturnsOk()
    {
        var request = new DockerExecRequest { ContainerId = "c1", Command = "ls -la" };

        var result = await _sut.ExecuteShellCommand(1, request, TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task GetContainerEnvVars_ReturnsOk()
    {
        var result = await _sut.GetContainerEnvVars(1, "abc123def456", TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
    }

    // R-11: Server Read no longer reads a container's environment; it is a permission of its own.
    [Fact]
    public async Task GetContainerEnvVars_WithServerReadButWithoutTheEnvPermission_IsForbidden()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), PermissionKeys.ServerDockerContainerEnvView, 1, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetContainerEnvVars(1, "abc123def456", TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
        await _service.DidNotReceiveWithAnyArgs().GetContainerEnvVarsAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ListContainerFiles_ReturnsOk()
    {
        var request = new DockerBrowseRequest { ContainerId = "c1", Path = "/" };

        var result = await _sut.ListContainerFiles(1, request, TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task BuildImage_ReturnsOk()
    {
        var request = new DockerBuildRequest { ImageTag = "myapp:latest", DockerfileContent = "FROM alpine" };

        var result = await _sut.BuildImage(1, request, TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
    }
}
