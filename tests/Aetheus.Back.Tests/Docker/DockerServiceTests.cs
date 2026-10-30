// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Docker;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class DockerServiceTests
{
    private readonly IDockerRepository _repo = Substitute.For<IDockerRepository>();
    private readonly Aetheus.Back.Components.Tasks.ITaskService _taskService = Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>();
    private readonly DockerService _sut;

    public DockerServiceTests()
    {
        _sut = new DockerService(_repo, Substitute.For<IAuditService>(), _taskService);
    }

    // --- GetContainersAsync ---

    [Fact]
    public async Task GetContainersAsync_MapsEntities()
    {
        _repo.GetContainersAsync(1, TestContext.Current.CancellationToken).Returns(
        [
            new DockerContainer
            {
                ServerId = 1, ContainerId = "abc123def456", Name = "web",
                Image = "nginx:latest", State = "running", Status = "Up 2h",
                Ports = "80/tcp", CpuPercent = 1.5, MemoryUsageMb = 128, MemoryLimitMb = 512
            }
        ]);

        var result = await _sut.GetContainersAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("abc123def456", result[0].ContainerId);
        Assert.Equal("web", result[0].Name);
        Assert.Equal(1.5, result[0].CpuPercent);
    }

    [Fact]
    public async Task GetContainersAsync_EmptyServer_ReturnsEmpty()
    {
        _repo.GetContainersAsync(99, TestContext.Current.CancellationToken).Returns([]);

        var result = await _sut.GetContainersAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    // --- ExecuteActionAsync ---

    [Fact]
    public async Task ExecuteActionAsync_ValidId_CreatesTask()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.ExecuteActionAsync(1, new DockerActionRequest
        {
            ContainerId = "abc123def456",
            Action = DockerContainerAction.Stop
        }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            IsOperation(t, OperationKind.DockerStopContainer, "abc123def456") &&
            t.Status == TaskExecutionStatus.Pending), TestContext.Current.CancellationToken);
        await _taskService.Received(1).NotifyTaskQueuedAsync(Arg.Any<ServerTask>(), ct: Arg.Any<CancellationToken>());
    }

    // R-119: a state-changing Docker action reaches the agent as a typed operation whose kind it maps to its cached
    // Docker inventory, so the inventory is collected again and sent at once. A plain shell task carries no kind:
    // the page kept showing the container running after it had been stopped. The target is the only variable, never
    // a docker command line.
    private static bool IsOperation(ServerTask task, OperationKind kind, string target) =>
        task.Executor == ExecutorType.Operation
        && task.Operation == kind
        && task.Command == target;

    private static Dictionary<string, string> Environment(ServerTask task) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(task.EnvironmentVariables)!;

    [Fact]
    public async Task ExecuteActionAsync_InvalidContainerId_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(1, new DockerActionRequest
        {
            ContainerId = "invalid; rm -rf /",
            Action = DockerContainerAction.Stop
        }, ct: TestContext.Current.CancellationToken));

        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken);
    }

    // --- PullImageAsync ---

    [Fact]
    public async Task PullImageAsync_ValidImage_CreatesTask()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.PullImageAsync(1, new DockerPullImageRequest { Image = "nginx:latest" }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            IsOperation(t, OperationKind.DockerPullImage, "nginx:latest") && t.TimeoutSeconds == 300),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PullImageAsync_InvalidImageName_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.PullImageAsync(1, new DockerPullImageRequest { Image = "; echo pwned" }, ct: TestContext.Current.CancellationToken));
    }

    // --- RemoveImageAsync ---

    [Fact]
    public async Task RemoveImageAsync_InvalidId_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.RemoveImageAsync(1, "invalid&&bad", ct: TestContext.Current.CancellationToken));
    }

    // --- PruneAsync ---

    [Fact]
    public async Task PruneAsync_AllFlags_BuildsCompoundCommand()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.PruneAsync(1, new DockerPruneRequest
        {
            Containers = true,
            Images = true,
            Volumes = true
        }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            IsOperation(t, OperationKind.DockerPrune, "containers,images,volumes")), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PruneAsync_NoFlags_Throws()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.PruneAsync(1, new DockerPruneRequest(), ct: TestContext.Current.CancellationToken));
    }

    // --- UpdateResourceLimitsAsync ---

    [Fact]
    public async Task UpdateResourceLimitsAsync_ValidRequest_CreatesTask()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.UpdateResourceLimitsAsync(1, new DockerResourceLimitsRequest
        {
            ContainerId = "abc123def456",
            CpuLimit = 2.0,
            MemoryLimitMb = 512
        }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            IsOperation(t, OperationKind.DockerUpdateResources, "abc123def456")
            && Environment(t)["AETHEUS_DOCKER_CPUS"] == "2.00"
            && Environment(t)["AETHEUS_DOCKER_MEMORY_MB"] == "512"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UpdateResourceLimitsAsync_CpuOnly_SendsNoMemoryLimit()
    {
        await _sut.UpdateResourceLimitsAsync(1, new DockerResourceLimitsRequest
        {
            ContainerId = "abc123def456",
            CpuLimit = 0.5
        }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            IsOperation(t, OperationKind.DockerUpdateResources, "abc123def456")
            && Environment(t).Count == 1
            && Environment(t)["AETHEUS_DOCKER_CPUS"] == "0.50"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UpdateResourceLimitsAsync_ZeroLimits_Throws()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.UpdateResourceLimitsAsync(1, new DockerResourceLimitsRequest
            {
                ContainerId = "abc123def456",
                CpuLimit = 0,
                MemoryLimitMb = 0
            }, ct: TestContext.Current.CancellationToken));
    }

    // --- ExecuteComposeActionAsync ---

    [Fact]
    public async Task ExecuteComposeActionAsync_InvalidName_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.ExecuteComposeActionAsync(1, new DockerComposeActionRequest
            {
                StackName = "; echo pwned",
                Action = DockerComposeAction.Up
            }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteComposeActionAsync_ValidRequest_CreatesTask()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.ExecuteComposeActionAsync(1, new DockerComposeActionRequest
        {
            StackName = "my-stack",
            Action = DockerComposeAction.Up
        }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            IsOperation(t, OperationKind.DockerComposeProjectUp, "my-stack")), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteComposeActionAsync_StackNameComposeRefuses_RejectedBeforeQueueing()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.ExecuteComposeActionAsync(1, new DockerComposeActionRequest { StackName = "Web.Stack", Action = DockerComposeAction.Down },
                ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    // --- GetImagesAsync ---

    [Fact]
    public async Task GetImagesAsync_MapsEntities()
    {
        _repo.GetImagesAsync(1, TestContext.Current.CancellationToken).Returns(
        [
            new DockerImage
            {
                ServerId = 1, ImageId = "sha256abc123", Repository = "nginx",
                Tag = "latest", Size = "142MB"
            }
        ]);

        var result = await _sut.GetImagesAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("nginx", result[0].Repository);
    }

    // --- GetContainerLogsAsync ---

    [Fact]
    public async Task GetContainerLogsAsync_ValidId_QueuesTailTask()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        var result = await _sut.GetContainerLogsAsync(1, new DockerContainerLogsRequest
        {
            ContainerId = "abc123def456",
            Tail = 100
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("Log retrieval task queued.", result);
        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Command.Contains("docker logs --tail 100 abc123def456")), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetContainerLogsAsync_InvalidId_ReturnsEmpty()
    {
        var result = await _sut.GetContainerLogsAsync(1, new DockerContainerLogsRequest
        {
            ContainerId = "bad;rm -rf /",
            Tail = 10
        }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, result);
    }

    // --- GetComposeStacksAsync ---

    [Fact]
    public async Task GetComposeStacksAsync_MapsEntities()
    {
        _repo.GetComposeStacksAsync(1, TestContext.Current.CancellationToken).Returns(
        [
            new DockerComposeStack { ServerId = 1, Name = "monitoring", Status = "running(2)", ConfigFile = "/opt/compose.yml", RunningCount = 2, TotalCount = 2 }
        ]);

        var result = await _sut.GetComposeStacksAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("monitoring", result[0].Name);
        Assert.Equal(2, result[0].RunningCount);
    }

    // --- GetNetworksAsync ---

    [Fact]
    public async Task GetNetworksAsync_MapsEntities()
    {
        _repo.GetNetworksAsync(1, TestContext.Current.CancellationToken).Returns(
        [
            new DockerNetwork { ServerId = 1, NetworkId = "net1", Name = "bridge", Driver = "bridge", Scope = "local" }
        ]);

        var result = await _sut.GetNetworksAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("bridge", result[0].Name);
        Assert.Equal("local", result[0].Scope);
    }

    // --- GetVolumesAsync ---

    [Fact]
    public async Task GetVolumesAsync_MapsEntities()
    {
        _repo.GetVolumesAsync(1, TestContext.Current.CancellationToken).Returns(
        [
            new DockerVolume { ServerId = 1, Name = "data", Driver = "local", Mountpoint = "/var/lib/docker/volumes/data" }
        ]);

        var result = await _sut.GetVolumesAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("data", result[0].Name);
    }

    // --- RemoveImageAsync ---

    [Fact]
    public async Task RemoveImageAsync_ValidId_CreatesTask()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.RemoveImageAsync(1, "sha256:abcdef1234560000000000000000000000000000000000000000000000000000", ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            IsOperation(t, OperationKind.DockerRemoveImage, "sha256:abcdef1234560000000000000000000000000000000000000000000000000000")),
            TestContext.Current.CancellationToken);
    }

    // --- ExecuteComposeActionAsync (more actions) ---

    [Fact]
    public async Task ExecuteComposeAction_Down_CreatesCorrectCommand()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.ExecuteComposeActionAsync(1, new DockerComposeActionRequest
        {
            StackName = "my-stack",
            Action = DockerComposeAction.Down
        }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            IsOperation(t, OperationKind.DockerComposeProjectDown, "my-stack")), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteComposeAction_Restart_CreatesCorrectCommand()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.ExecuteComposeActionAsync(1, new DockerComposeActionRequest
        {
            StackName = "web",
            Action = DockerComposeAction.Restart
        }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            IsOperation(t, OperationKind.DockerComposeProjectRestart, "web")), TestContext.Current.CancellationToken);
    }

    // --- PruneAsync (partial flags) ---

    [Fact]
    public async Task PruneAsync_OnlyContainers_SingleCommand()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.PruneAsync(1, new DockerPruneRequest { Containers = true }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            IsOperation(t, OperationKind.DockerPrune, "containers")), TestContext.Current.CancellationToken);
    }

    // --- InspectContainerAsync ---

    [Fact]
    public async Task InspectContainerAsync_ValidId_CreatesTask()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.InspectContainerAsync(1, "abc123def456", ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Command.Contains("docker inspect abc123def456")), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InspectContainerAsync_InvalidId_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.InspectContainerAsync(1, "bad;cmd", ct: TestContext.Current.CancellationToken));
    }

    // --- GetComposeFileAsync ---

    [Fact]
    public async Task GetComposeFileAsync_ValidName_CreatesTask()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.GetComposeFileAsync(1, "my-stack", ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Command.Contains("docker compose -p my-stack config")), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetComposeFileAsync_InvalidName_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.GetComposeFileAsync(1, "; echo pwned", ct: TestContext.Current.CancellationToken));
    }

    // --- SaveComposeFileAsync ---

    [Fact]
    public async Task SaveComposeFileAsync_Valid_CreatesTask()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.SaveComposeFileAsync(1, new DockerComposeFileSaveRequest
        {
            StackName = "web",
            Content = "version: '3'\nservices:\n  web:\n    image: nginx"
        }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            IsPayloadOperation(t, OperationKind.DockerComposeUp, "web", "AETHEUS_DOCKER_COMPOSE_B64",
                "version: '3'\nservices:\n  web:\n    image: nginx")), TestContext.Current.CancellationToken);
    }

    // BACK-012: the payload travels in an env var of a typed operation, not in a shell string the
    // agent's command validator refuses (it rejected every pipe of the former printf | base64 form).
    private static bool IsPayloadOperation(ServerTask task, OperationKind kind, string target, string variable, string payload)
    {
        var env = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(task.EnvironmentVariables)!;
        return task.Executor == ExecutorType.Operation
            && task.Operation == kind
            && task.Command == target
            && env.TryGetValue(variable, out var encoded)
            && System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)) == payload;
    }

    [Fact]
    public async Task SaveComposeFileAsync_StackNameComposeRefuses_RejectedBeforeQueueing()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.SaveComposeFileAsync(1, new DockerComposeFileSaveRequest { StackName = "Web.Stack", Content = "services: {}" },
                ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveComposeFileAsync_EmptyContent_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.SaveComposeFileAsync(1, new DockerComposeFileSaveRequest
            {
                StackName = "web",
                Content = ""
            }, ct: TestContext.Current.CancellationToken));
    }

    // --- ExecuteShellCommandAsync ---

    [Fact]
    public async Task ExecuteShellCommandAsync_Valid_CreatesTask()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.ExecuteShellCommandAsync(1, new DockerExecRequest
        {
            ContainerId = "abc123def456",
            Command = "ls -la"
        }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            IsPayloadOperation(t, OperationKind.DockerExecScript, "abc123def456", "AETHEUS_DOCKER_EXEC_B64", "ls -la")),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteShellCommandAsync_EmptyCommand_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.ExecuteShellCommandAsync(1, new DockerExecRequest
            {
                ContainerId = "abc123def456",
                Command = ""
            }, ct: TestContext.Current.CancellationToken));
    }

    // --- GetContainerEnvVarsAsync ---

    [Fact]
    public async Task GetContainerEnvVarsAsync_Valid_CreatesTask()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.GetContainerEnvVarsAsync(1, "abc123def456", ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Command.Contains("docker inspect") && t.Command.Contains("abc123def456")), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetContainerEnvVarsAsync_InvalidId_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.GetContainerEnvVarsAsync(1, "bad;cmd", ct: TestContext.Current.CancellationToken));
    }

    // --- ListContainerFilesAsync ---

    [Fact]
    public async Task ListContainerFilesAsync_Valid_CreatesTask()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.ListContainerFilesAsync(1, new DockerBrowseRequest
        {
            ContainerId = "abc123def456",
            Path = "/var/log"
        }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Command.Contains("abc123def456")), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ListContainerFilesAsync_EmptyPath_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.ListContainerFilesAsync(1, new DockerBrowseRequest
            {
                ContainerId = "abc123def456",
                Path = ""
            }, ct: TestContext.Current.CancellationToken));
    }

    // --- BuildImageAsync ---

    [Fact]
    public async Task BuildImageAsync_Valid_CreatesTask()
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.BuildImageAsync(1, new DockerBuildRequest
        {
            ImageTag = "myapp:latest",
            DockerfileContent = "FROM nginx:alpine"
        }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            IsPayloadOperation(t, OperationKind.DockerBuildImage, "myapp:latest", "AETHEUS_DOCKER_DOCKERFILE_B64", "FROM nginx:alpine")),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BuildImageAsync_InvalidTag_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.BuildImageAsync(1, new DockerBuildRequest
            {
                ImageTag = "; echo pwned",
                DockerfileContent = "FROM nginx"
            }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BuildImageAsync_EmptyDockerfile_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.BuildImageAsync(1, new DockerBuildRequest
            {
                ImageTag = "myapp:latest",
                DockerfileContent = ""
            }, ct: TestContext.Current.CancellationToken));
    }

    // --- UpdateResourceLimitsAsync (more scenarios) ---

    [Fact]
    public async Task UpdateResourceLimitsAsync_InvalidId_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.UpdateResourceLimitsAsync(1, new DockerResourceLimitsRequest
            {
                ContainerId = "bad;cmd",
                CpuLimit = 1
            }, ct: TestContext.Current.CancellationToken));
    }

    // --- ExecuteActionAsync (all actions) ---

    [Theory]
    [InlineData(DockerContainerAction.Start, OperationKind.DockerStartContainer)]
    [InlineData(DockerContainerAction.Stop, OperationKind.DockerStopContainer)]
    [InlineData(DockerContainerAction.Restart, OperationKind.DockerRestartContainer)]
    [InlineData(DockerContainerAction.Remove, OperationKind.DockerRemoveContainer)]
    public async Task ExecuteActionAsync_AllActions_QueueTheTypedOperation(DockerContainerAction action, OperationKind expected)
    {
        _repo.ServerExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);

        await _sut.ExecuteActionAsync(1, new DockerActionRequest
        {
            ContainerId = "abc123def456",
            Action = action
        }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            IsOperation(t, expected, "abc123def456") && t.TimeoutSeconds == 30), TestContext.Current.CancellationToken);
    }

    // --- PullImageAsync ---

}
