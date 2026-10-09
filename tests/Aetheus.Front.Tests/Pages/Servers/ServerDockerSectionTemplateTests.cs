// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Render tests for ServerDockerSection.razor template - exercises markup branches
/// that are only hit when rendering with specific data shapes.
/// State assertions verify the component fields that drive template branches.
/// </summary>
public class ServerDockerSectionTemplateTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

    public ServerDockerSectionTemplateTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private void SetupApiStubs(int serverId = 1)
    {
        _handler.SetJsonResponse($"api/servers/{serverId}/docker/containers", new List<DockerContainerDto>());
        _handler.SetJsonResponse($"api/servers/{serverId}/docker/action", "{}");
        _handler.SetJsonResponse($"api/servers/{serverId}/docker/containers/logs", "log line 1\nlog line 2");
        _handler.SetJsonResponse($"api/servers/{serverId}/docker/images", new List<DockerImageDto>());
        _handler.SetJsonResponse($"api/servers/{serverId}/docker/images/pull", "{}");
        _handler.SetJsonResponse($"api/servers/{serverId}/docker/compose/action", "{}");
        _handler.SetJsonResponse($"api/servers/{serverId}/docker/prune", "{}");
        _handler.SetJsonResponse($"api/servers/{serverId}/docker/resource-limits", "{}");
        _handler.SetJsonResponse($"api/servers/{serverId}/docker/exec", "{}");
        _handler.SetJsonResponse($"api/servers/{serverId}/docker/build", "{}");
        _handler.SetJsonResponse($"api/servers/{serverId}/docker/containers/browse", "{}");
        _handler.SetJsonResponse($"api/servers/{serverId}/docker/containers/abc123def456ghi/inspect", "{}");
        _handler.SetJsonResponse($"api/servers/{serverId}/docker/containers/abc123def456ghi/env", "{}");
    }

    private static ServerDetailDto BuildDockerServer(int id, DockerDataDto docker) => new()
    {
        Id = id,
        Name = $"docker-srv-{id}",
        Hostname = $"10.0.0.{id}",
        Type = ServerType.Docker,
        Status = ServerStatus.Online,
        MemoryTotalMb = 16384,
        MemoryUsedMb = 8192,
        CpuPercent = 30,
        Services = [],
        Docker = docker
    };

    private static DockerDataDto BuildFullDockerData() => new()
    {
        Containers =
        [
            new DockerContainerDto
            {
                ContainerId = "abc123def456ghi",
                Name = "nginx",
                Image = "nginx:latest",
                State = "running",
                Status = "Up 3 hours",
                CpuPercent = 5.2,
                MemoryUsageMb = 128,
                MemoryLimitMb = 512,
                Ports = "80:80, 443:443",
                Project = "webapp"
            },
            new DockerContainerDto
            {
                ContainerId = "xyz789uvw321rst",
                Name = "myapi",
                Image = "dotnet:8",
                State = "exited",
                Status = "Exited (0) 2h ago",
                CpuPercent = 0,
                MemoryUsageMb = 0,
                MemoryLimitMb = 1024,
                Ports = "",
                Project = "webapp"
            },
            new DockerContainerDto
            {
                ContainerId = "pause123pause456",
                Name = "redis",
                Image = "redis:7",
                State = "paused",
                Status = "Paused",
                CpuPercent = 0.1,
                MemoryUsageMb = 32,
                MemoryLimitMb = 128,
                Ports = "6379:6379",
                Project = "cache"
            }
        ],
        Images =
        [
            new DockerImageDto { ImageId = "img001aaa", Repository = "nginx", Tag = "latest", Size = "142MB", Project = "webapp", Created = DateTime.UtcNow.AddDays(-10) },
            new DockerImageDto { ImageId = "img002bbb", Repository = "dotnet", Tag = "8", Size = "800MB", Project = "webapp", Created = DateTime.UtcNow.AddDays(-5) },
            new DockerImageDto { ImageId = "img003ccc", Repository = "redis", Tag = "7", Size = "120MB", Project = "cache", Created = DateTime.UtcNow.AddMonths(-1) }
        ],
        ComposeStacks =
        [
            new DockerComposeStackDto { Name = "webapp-stack", Status = "running(2)", RunningCount = 2, TotalCount = 2, ConfigFile = "/opt/webapp/docker-compose.yml" },
            new DockerComposeStackDto { Name = "monitoring-stack", Status = "stopped(0)", RunningCount = 0, TotalCount = 3, ConfigFile = "/opt/monitoring/docker-compose.yml" }
        ],
        Networks =
        [
            new DockerNetworkDto { NetworkId = "net001aaa", Name = "webapp_default", Driver = "bridge", Scope = "local", Project = "webapp" },
            new DockerNetworkDto { NetworkId = "net002bbb", Name = "host", Driver = "host", Scope = "local", Project = "" }
        ],
        Volumes =
        [
            new DockerVolumeDto { Name = "pgdata", Driver = "local", Mountpoint = "/var/lib/docker/volumes/pgdata/_data", Project = "webapp" },
            new DockerVolumeDto { Name = "redis_data", Driver = "local", Mountpoint = "/var/lib/docker/volumes/redis_data/_data", Project = "cache" }
        ]
    };

    private IRenderedComponent<ServerDockerSection> RenderDockerSection(int id = 1, DockerDataDto? docker = null)
    {
        SetupApiStubs(id);
        var server = BuildDockerServer(id, docker ?? BuildFullDockerData());
        return Render<ServerDockerSection>(p => p
            .Add(x => x.Server, server)
            .Add(x => x.ServerId, id)
            .Add(x => x.DockerInitialLoaded, true));
    }

    // ── Test 1: Full data render ──────────────────────────────────────────────

    [Fact]
    public void Render_FullDockerData_RendersWithoutException()
    {
        var cut = RenderDockerSection(1);
        // Full data renders the seeded container names in the containers grid (nginx + myapi).
        Assert.Contains("nginx", cut.Markup);
        Assert.Contains("myapi", cut.Markup);
    }

    // ── Test 2: Container name in markup ─────────────────────────────────────

    [Fact]
    public void Render_FullDockerData_ContainersNameInMarkup()
    {
        var cut = RenderDockerSection(2);
        Assert.Contains("nginx", cut.Markup);
    }

    // ── Test 3: Stopped containers ──────────────────────────────────────────

    [Fact]
    public void Render_StoppedContainers_ShowsContainer()
    {
        var docker = new DockerDataDto
        {
            Containers =
            [
                new DockerContainerDto
                {
                    ContainerId = "stopped001",
                    Name = "stopped-app",
                    Image = "myapp:1.0",
                    State = "exited",
                    Status = "Exited (1) 30m ago",
                    Project = "myapp"
                }
            ],
            Images = [],
            ComposeStacks = [],
            Networks = [],
            Volumes = []
        };
        var cut = RenderDockerSection(3, docker);
        Assert.Contains("stopped-app", cut.Markup);
    }

    // ── Test 4: Skeleton shown when not initial-loaded ───────────────────────

    [Fact]
    public void Render_NotInitialLoaded_EmptyContainers_ShowsSkeleton()
    {
        SetupApiStubs(4);
        var server = BuildDockerServer(4, new DockerDataDto { Containers = [], Images = [], ComposeStacks = [], Networks = [], Volumes = [] });
        var cut = Render<ServerDockerSection>(p => p
            .Add(x => x.Server, server)
            .Add(x => x.ServerId, 4)
            .Add(x => x.DockerInitialLoaded, false));

        Assert.Contains("skeleton-row", cut.Markup);
    }

    // ── Test 5: Inspect panel state set via method ───────────────────────────

    [Fact]
    public async Task InspectPanel_StateSetViaMethod()
    {
        var cut = RenderDockerSection(5);
        var tab = cut.FindComponent<DockerContainersTab>();

        var method = typeof(DockerContainersTab).GetMethod("RequestInspectAsync", BF)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(tab.Instance, ["abc123def456ghi"])!);

        var inspectId = (string?)typeof(DockerContainersTab).GetField("_inspectContainerId", BF)!.GetValue(tab.Instance);
        Assert.Equal("abc123def456ghi", inspectId);
    }

    // ── Test 6: Inspect content dispatched via HandleTaskCompleted ────────────

    [Fact]
    public async Task InspectContent_SetViaTaskCompleted()
    {
        var cut = RenderDockerSection(6);
        var tab = cut.FindComponent<DockerContainersTab>();

        var method = typeof(DockerContainersTab).GetMethod("RequestInspectAsync", BF)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(tab.Instance, ["abc123def456ghi"])!);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification
        {
            TaskId = 1,
            ServerId = 6,
            TaskName = "docker inspect abc123",
            Status = TaskExecutionStatus.Success,
            Output = "{\"Id\": \"abc123\"}"
        }));

        var content = (string?)typeof(DockerContainersTab).GetField("_inspectContent", BF)!.GetValue(tab.Instance);
        Assert.NotNull(content);
    }

    // ── Test 7: Shell panel state set via OpenShell ───────────────────────────

    [Fact]
    public async Task ShellPanel_StateSetViaOpenShell()
    {
        var cut = RenderDockerSection(7);
        var tab = cut.FindComponent<DockerContainersTab>();

        var method = typeof(DockerContainersTab).GetMethod("OpenShell", BF)!;
        await cut.InvokeAsync(() => method.Invoke(tab.Instance, ["abc123def456ghi", "nginx"]));

        var shellId = (string?)typeof(DockerContainersTab).GetField("_shellContainerId", BF)!.GetValue(tab.Instance);
        Assert.Equal("abc123def456ghi", shellId);
    }

    // ── Test 8: Env vars state set via RequestEnvVarsAsync ───────────────────

    [Fact]
    public async Task EnvVarsPanel_StateSetViaMethod()
    {
        var cut = RenderDockerSection(8);
        var tab = cut.FindComponent<DockerContainersTab>();

        var method = typeof(DockerContainersTab).GetMethod("RequestEnvVarsAsync", BF)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(tab.Instance, ["abc123def456ghi", "nginx"])!);

        var envId = (string?)typeof(DockerContainersTab).GetField("_envContainerId", BF)!.GetValue(tab.Instance);
        Assert.Equal("abc123def456ghi", envId);
    }

    // ── Test 9: Env vars content dispatched via HandleTaskCompleted ───────────

    [Fact]
    public async Task EnvContent_SetViaTaskCompleted()
    {
        var cut = RenderDockerSection(9);
        var tab = cut.FindComponent<DockerContainersTab>();

        var method = typeof(DockerContainersTab).GetMethod("RequestEnvVarsAsync", BF)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(tab.Instance, ["abc123def456ghi", "nginx"])!);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification
        {
            TaskId = 2,
            ServerId = 9,
            TaskName = "docker env abc123",
            Status = TaskExecutionStatus.Success,
            Output = "[\"PATH=/usr/bin\",\"HOME=/root\"]"
        }));

        var content = (string?)typeof(DockerContainersTab).GetField("_envContent", BF)!.GetValue(tab.Instance);
        Assert.NotNull(content);
    }

    // ── Test 10: Prune dialog state ───────────────────────────────────────────

    [Fact]
    public async Task ConfirmRemoveContainer_OpensConfirmationDialog()
    {
        var cut = RenderDockerSection(10);
        var tab = cut.FindComponent<DockerContainersTab>();

        var method = typeof(DockerContainersTab).GetMethod("ConfirmRemoveContainerAsync", BF)!;
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        await cut.InvokeAsync(() => (Task)method.Invoke(tab.Instance, ["abc123def456ghi", "nginx"])!);

        Assert.Equal(1, dialog.OpenCount);
        Assert.Equal("RemoveContainerConfirm", dialog.LastConfirmMessage);
        Assert.Equal("RemoveContainer", dialog.LastTitle);
    }

    // ── Test 11: Resource limits dialog state ────────────────────────────────

    [Fact]
    public async Task LimitsDialog_OpenedViaMethod_PassesContainerId()
    {
        var cut = RenderDockerSection(11);
        var tab = cut.FindComponent<DockerContainersTab>();

        var method = typeof(DockerContainersTab).GetMethod("OpenResourceLimitsDialog", BF)!;
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        await cut.InvokeAsync(() => (Task)method.Invoke(tab.Instance, ["abc123def456ghi"])!);

        Assert.Equal("abc123def456ghi", dialog.LastParameters!["ContainerId"]);
    }

    // ── Test 12: Compose editor state via OpenComposeEditorAsync ─────────────

    [Fact]
    public async Task ComposeEditor_StateSetViaOpenCompose()
    {
        _handler.SetResponse(
            HttpMethod.Get,
            "api/servers/12/docker/compose/webapp-stack/file",
            System.Net.HttpStatusCode.OK);
        var cut = RenderDockerSection(12).FindComponent<DockerComposeTab>();

        var method = typeof(DockerComposeTab).GetMethod("OpenComposeEditorAsync", BF)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["webapp-stack"])!);

        var visible = (bool)typeof(DockerComposeTab).GetField("_composeEditorVisible", BF)!.GetValue(cut.Instance)!;
        var stack = (string?)typeof(DockerComposeTab).GetField("_composeEditorStack", BF)!.GetValue(cut.Instance);
        Assert.True(visible);
        Assert.Equal("webapp-stack", stack);
    }

    // ── Test 13: Compose file content dispatched ─────────────────────────────

    [Fact]
    public async Task ComposeContent_SetViaTaskCompleted()
    {
        _handler.SetResponse(
            HttpMethod.Get,
            "api/servers/13/docker/compose/webapp-stack/file",
            System.Net.HttpStatusCode.OK);
        var cut = RenderDockerSection(13);
        var composeTab = cut.FindComponent<DockerComposeTab>();

        var openMethod = typeof(DockerComposeTab).GetMethod("OpenComposeEditorAsync", BF)!;
        await cut.InvokeAsync(async () => await (Task)openMethod.Invoke(composeTab.Instance, ["webapp-stack"])!);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification
        {
            TaskId = 3,
            ServerId = 13,
            TaskName = DockerTaskNames.ComposeFile("webapp-stack"),
            Status = TaskExecutionStatus.Success,
            Output = "version: '3'\nservices:\n  web:\n    image: nginx"
        }));

        var content = (string?)typeof(DockerComposeTab).GetField("_composeEditorContent", BF)!.GetValue(composeTab.Instance);
        Assert.NotNull(content);
        Assert.Contains("nginx", content!);
    }

    // ── Test 14: Logs panel state via ToggleLogsAsync ────────────────────────

    [Fact]
    public async Task LogsPanel_StateSetViaToggle()
    {
        var cut = RenderDockerSection(14);
        var tab = cut.FindComponent<DockerContainersTab>();

        var method = typeof(DockerContainersTab).GetMethod("ToggleLogsAsync", BF)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(tab.Instance, ["abc123def456ghi"])!);

        var logsId = (string?)typeof(DockerContainersTab).GetField("_logsContainerId", BF)!.GetValue(tab.Instance);
        Assert.Equal("abc123def456ghi", logsId);
    }

    // ── Test 15: Logs content set from API response ───────────────────────────

    [Fact]
    public async Task LogsContent_SetFromApiResponse()
    {
        _handler.SetJsonResponse("api/servers/15/docker/containers/logs", "log output here");
        var cut = RenderDockerSection(15);
        var tab = cut.FindComponent<DockerContainersTab>();

        var method = typeof(DockerContainersTab).GetMethod("ToggleLogsAsync", BF)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(tab.Instance, ["abc123def456ghi"])!);

        var content = (string?)typeof(DockerContainersTab).GetField("_logsContent", BF)!.GetValue(tab.Instance);
        Assert.NotNull(content);
    }

    // ── Test 16: Browse panel state via OpenFileBrowserAsync ─────────────────

    [Fact]
    public async Task BrowsePanel_StateSetViaOpenFileBrowser()
    {
        var cut = RenderDockerSection(16);
        var tab = cut.FindComponent<DockerContainersTab>();

        var method = typeof(DockerContainersTab).GetMethod("OpenFileBrowserAsync", BF)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(tab.Instance, ["abc123def456ghi", "nginx"])!);

        var browseId = (string?)typeof(DockerContainersTab).GetField("_browseContainerId", BF)!.GetValue(tab.Instance);
        Assert.Equal("abc123def456ghi", browseId);
    }

    // ── Test 17: Browse content dispatched via HandleTaskCompleted ────────────

    [Fact]
    public async Task BrowseContent_SetViaTaskCompleted()
    {
        var cut = RenderDockerSection(17);
        var tab = cut.FindComponent<DockerContainersTab>();

        var method = typeof(DockerContainersTab).GetMethod("OpenFileBrowserAsync", BF)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(tab.Instance, ["abc123def456ghi", "nginx"])!);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification
        {
            TaskId = 4,
            ServerId = 17,
            TaskName = "docker ls /",
            Status = TaskExecutionStatus.Success,
            Output = "bin  etc  usr"
        }));

        var content = (string?)typeof(DockerContainersTab).GetField("_browseContent", BF)!.GetValue(tab.Instance);
        Assert.Equal("bin  etc  usr", content);
    }
}
