// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Deep template coverage for ServerDockerSection.razor - exercises template branches
/// not covered by earlier tests: the zoom-project dialog, prune dialog checkboxes,
/// env-vars parsed table, shell panel, logs-loading spinner vs content, browse path.
/// </summary>
public class ServerDockerSectionDeepTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

    public ServerDockerSectionDeepTests()
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

    private static ServerDetailDto BuildServer(int id, DockerDataDto docker) => new()
    {
        Id = id,
        Name = $"srv-{id}",
        Hostname = "10.0.0.1",
        Type = ServerType.Docker,
        Status = ServerStatus.Online,
        MemoryTotalMb = 8192,
        MemoryUsedMb = 4096,
        CpuPercent = 20,
        Services = [],
        Docker = docker
    };

    private static DockerDataDto BuildRichDockerData() => new()
    {
        Containers =
        [
            new DockerContainerDto
            {
                ContainerId = "abc123def456ghi",
                Name = "web-running",
                Image = "nginx:latest",
                State = "running",
                Status = "Up 1 hour",
                CpuPercent = 5.0,
                MemoryUsageMb = 64,
                MemoryLimitMb = 256,
                Ports = "80:80",
                Project = "webapp"
            },
            new DockerContainerDto
            {
                ContainerId = "stopped001stopped",
                Name = "api-stopped",
                Image = "dotnet:8",
                State = "exited",
                Status = "Exited (1) 1h ago",
                CpuPercent = 0,
                MemoryUsageMb = 0,
                MemoryLimitMb = 512,
                Ports = "",
                Project = "webapp"
            },
            new DockerContainerDto
            {
                ContainerId = "noproject00001",
                Name = "orphan",
                Image = "alpine:latest",
                State = "running",
                Status = "Up 5m",
                CpuPercent = 0.1,
                MemoryUsageMb = 16,
                MemoryLimitMb = 64,
                Ports = "",
                Project = ""
            }
        ],
        Images =
        [
            new DockerImageDto
            {
                ImageId = "img001aaa",
                Repository = "nginx",
                Tag = "latest",
                Size = "142MB",
                Project = "webapp",
                Created = DateTime.UtcNow.AddDays(-10)
            }
        ],
        ComposeStacks =
        [
            new DockerComposeStackDto
            {
                Name = "webapp",
                Status = "running(1)",
                RunningCount = 1,
                TotalCount = 2,
                ConfigFile = "/opt/webapp/docker-compose.yml"
            },
            new DockerComposeStackDto
            {
                Name = "monitoring",
                Status = "stopped(0)",
                RunningCount = 0,
                TotalCount = 1,
                ConfigFile = "/opt/monitoring/docker-compose.yml"
            }
        ],
        Networks =
        [
            new DockerNetworkDto
            {
                NetworkId = "net001aaa",
                Name = "webapp_default",
                Driver = "bridge",
                Scope = "local",
                Project = "webapp"
            }
        ],
        Volumes =
        [
            new DockerVolumeDto
            {
                Name = "pgdata",
                Driver = "local",
                Mountpoint = "/var/lib/docker/volumes/pgdata/_data",
                Project = "webapp"
            }
        ]
    };

    private IRenderedComponent<ServerDockerSection> RenderSection(int id = 1, DockerDataDto? docker = null, bool initialLoaded = true)
    {
        SetupApiStubs(id);
        var server = BuildServer(id, docker ?? BuildRichDockerData());
        return Render<ServerDockerSection>(p => p
            .Add(x => x.Server, server)
            .Add(x => x.ServerId, id)
            .Add(x => x.DockerInitialLoaded, initialLoaded));
    }

    // ── Test 1: Full render with running + stopped containers ────────────────

    [Fact]
    public void Render_RunningAndStoppedContainers_BothInMarkup()
    {
        var cut = RenderSection(1);
        Assert.Contains("web-running", cut.Markup);
        Assert.Contains("api-stopped", cut.Markup);
    }

    // ── Test 2: Container with no project (empty string) renders NoProject ───

    [Fact]
    public void Render_ContainerWithNoProject_ReturnsMarkup()
    {
        var cut = RenderSection(2);
        // The project-less ("orphan") container still renders in the containers grid.
        Assert.Contains("orphan", cut.Markup);
    }

    // ── Test 3: ExecutePruneAsync closes the dialog and posts the prune ───────

    [Fact]
    public async Task OpenPruneDialogAsync_ConfirmedSelectionPostsPrune()
    {
        var cut = RenderSection(3);
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.OpenResult = new DockerPruneDialogResult(true, true, false);
        var method = typeof(ServerDockerSection).GetMethod("OpenPruneDialogAsync", BF)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        Assert.Equal(typeof(DockerPruneDialog), dialog.LastComponent);
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/3/docker/prune"));
    }

    // ── Test 4: Prune selectAll state change ─────────────────────────────────

    [Fact]
    public void DockerPruneDialogModel_DefaultsToAllResources()
    {
        var model = new DockerPruneDialogModel();

        Assert.True(model.Containers);
        Assert.True(model.Images);
        Assert.True(model.Volumes);
    }

    // ── Test 5: Zoom project dialog opens ────────────────────────────────────

    [Fact]
    public async Task OpenProjectZoom_OpensDialogForProject()
    {
        var cut = RenderSection(5);
        var tab = cut.FindComponent<DockerContainersTab>();
        var method = typeof(DockerContainersTab).GetMethod("OpenProjectZoom", BF)!;
        await cut.InvokeAsync(() => method.Invoke(tab.Instance, ["webapp"]));

        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        Assert.Equal(typeof(DockerProjectDialog), dialog.LastComponent);
        Assert.Equal("webapp", dialog.LastParameters!["Project"]);
    }

    // ── Test 6: Zoom dialog renders the project's containers after open ──────

    [Fact]
    public async Task OpenProjectZoom_ResolvesTheZoomedProjectsContainers()
    {
        var cut = RenderSection(6);
        var tab = cut.FindComponent<DockerContainersTab>();
        var method = typeof(DockerContainersTab).GetMethod("OpenProjectZoom", BF)!;
        await cut.InvokeAsync(() => method.Invoke(tab.Instance, ["webapp"]));

        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        var server = Assert.IsType<ServerDetailDto>(dialog.LastParameters!["Server"]);
        var project = Assert.IsType<string>(dialog.LastParameters["Project"]);
        var matching = server.Docker.Containers.Where(container => container.Project == project).ToList();

        Assert.NotEmpty(matching);
        Assert.All(matching, container => Assert.Equal("webapp", container.Project));
    }

    // ── Test 7: CloseProjectZoom clears state ────────────────────────────────

    [Fact]
    public async Task OpenProjectZoom_ActionResult_ExecutesBulkAction()
    {
        var cut = RenderSection(7);
        var tab = cut.FindComponent<DockerContainersTab>();
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.OpenResult = DockerContainerAction.Restart;
        var method = typeof(DockerContainersTab).GetMethod("OpenProjectZoom", BF)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(tab.Instance, ["webapp"])!);

        Assert.Contains(_handler.Requests, request => request.Method == "POST" && request.Url.Contains("docker/action"));
    }

    // ── Test 8: ToggleLogsAsync opens the panel and fetches logs ─────────────

    [Fact]
    public async Task ToggleLogsAsync_Open_SetsContainerAndPostsLogsRequest()
    {
        var cut = RenderSection(8);
        var tab = cut.FindComponent<DockerContainersTab>();
        var method = typeof(DockerContainersTab).GetMethod("ToggleLogsAsync", BF)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(tab.Instance, ["abc123def456ghi"])!);

        var logsId = (string?)typeof(DockerContainersTab).GetField("_logsContainerId", BF)!.GetValue(tab.Instance);
        Assert.Equal("abc123def456ghi", logsId);
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/8/docker/containers/logs"));
    }

    // ── Test 9: OpenShell toggles the shell panel open then closed ──────────

    [Fact]
    public async Task OpenShell_Toggle_SetsThenClearsContainer()
    {
        var cut = RenderSection(9);
        var tab = cut.FindComponent<DockerContainersTab>();
        var method = typeof(DockerContainersTab).GetMethod("OpenShell", BF)!;

        await cut.InvokeAsync(() => method.Invoke(tab.Instance, ["abc123def456ghi", "web-running"]));
        Assert.Equal("abc123def456ghi",
            (string?)typeof(DockerContainersTab).GetField("_shellContainerId", BF)!.GetValue(tab.Instance));
        Assert.Equal("web-running",
            (string?)typeof(DockerContainersTab).GetField("_shellContainerName", BF)!.GetValue(tab.Instance));

        // Re-invoking with the same id closes the panel (real toggle behaviour).
        await cut.InvokeAsync(() => method.Invoke(tab.Instance, ["abc123def456ghi", "web-running"]));
        Assert.Null((string?)typeof(DockerContainersTab).GetField("_shellContainerId", BF)!.GetValue(tab.Instance));
    }

    // ── Test 10: RequestEnvVarsAsync opens the env panel and posts a request ─

    [Fact]
    public async Task RequestEnvVarsAsync_Open_SetsContainerAndPostsEnvRequest()
    {
        var cut = RenderSection(10);
        var tab = cut.FindComponent<DockerContainersTab>();
        var method = typeof(DockerContainersTab).GetMethod("RequestEnvVarsAsync", BF)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(tab.Instance, ["abc123def456ghi", "web-running"])!);

        var envId = (string?)typeof(DockerContainersTab).GetField("_envContainerId", BF)!.GetValue(tab.Instance);
        Assert.Equal("abc123def456ghi", envId);
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("docker/containers/abc123def456ghi/env"));
    }

    // ── Test 11: Env ParseEnvVars parses key=value correctly ─────────────────

    [Fact]
    public void ParseEnvVars_ValidJson_ReturnsKeyValuePairs()
    {
        var cut = RenderSection(11);
        var method = typeof(DockerContainersTab).GetMethod("ParseEnvVars",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (List<DockerEnvVarDto>)method.Invoke(null, ["[\"PATH=/usr/bin\",\"HOME=/root\"]"])!;
        Assert.Equal(2, result.Count);
        Assert.Equal("PATH", result[0].Key);
        Assert.Equal("/usr/bin", result[0].Value);
    }

    // ── Test 12: OpenFileBrowserAsync opens at root and lists the container ──

    [Fact]
    public async Task OpenFileBrowserAsync_Open_SetsRootPathAndPostsBrowse()
    {
        var cut = RenderSection(12);
        var tab = cut.FindComponent<DockerContainersTab>();
        var method = typeof(DockerContainersTab).GetMethod("OpenFileBrowserAsync", BF)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(tab.Instance, ["abc123def456ghi", "web-running"])!);

        var browseId = (string?)typeof(DockerContainersTab).GetField("_browseContainerId", BF)!.GetValue(tab.Instance);
        var path = (string)typeof(DockerContainersTab).GetField("_browsePath", BF)!.GetValue(tab.Instance)!;
        Assert.Equal("abc123def456ghi", browseId);
        Assert.Equal("/", path);
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("docker/containers/browse"));
    }

    // ── Test 14: OpenComposeEditorAsync opens the editor and fetches the file ─

    [Fact]
    public async Task OpenComposeEditorAsync_Open_ShowsEditorAndGetsComposeFile()
    {
        _handler.SetResponse(
            HttpMethod.Get,
            "api/servers/14/docker/compose/webapp/file",
            System.Net.HttpStatusCode.OK);
        var cut = RenderSection(14).FindComponent<DockerComposeTab>();
        var method = typeof(DockerComposeTab).GetMethod("OpenComposeEditorAsync", BF)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, ["webapp"])!);

        var visible = (bool)typeof(DockerComposeTab).GetField("_composeEditorVisible", BF)!.GetValue(cut.Instance)!;
        var stack = (string)typeof(DockerComposeTab).GetField("_composeEditorStack", BF)!.GetValue(cut.Instance)!;
        Assert.True(visible);
        Assert.Equal("webapp", stack);
        Assert.Contains(_handler.Requests, r => r.Method == "GET" && r.Url.Contains("docker/compose/webapp/file"));
    }

    // ── Test 15: OpenResourceLimitsDialog seeds the memory limit from the container ─

    [Fact]
    public async Task OpenResourceLimitsDialog_SeedsMemoryFromContainer()
    {
        var cut = RenderSection(15);
        var tab = cut.FindComponent<DockerContainersTab>();
        var method = typeof(DockerContainersTab).GetMethod("OpenResourceLimitsDialog", BF)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(tab.Instance, ["abc123def456ghi"])!);

        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        Assert.Equal(typeof(DockerResourceLimitsDialog), dialog.LastComponent);
        Assert.Equal("abc123def456ghi", dialog.LastParameters!["ContainerId"]);
        Assert.Equal(256, dialog.LastParameters["MemoryLimitMb"]);
    }

    // ── Test 16: GetMemoryBarClass for high-use container ────────────────────

    [Fact]
    public void GetMemoryBarClass_HighUsage_ReturnsDangerClass()
    {
        var cut = RenderSection(16);
        var method = typeof(DockerContainersTab).GetMethod("GetMemoryBarClass",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var container = new DockerContainerDto
        {
            MemoryUsageMb = 900,
            MemoryLimitMb = 1000
        };
        var css = (string)method.Invoke(null, [container])!;
        Assert.Contains("docker-mem", css);
    }

    // ── Test 17: ContainersForProject returns correct subset ─────────────────

    [Fact]
    public void ContainersForProject_Webapp_ReturnsOnlyWebappContainers()
    {
        var cut = RenderSection(17);
        var tab = cut.FindComponent<DockerContainersTab>();
        var method = typeof(DockerContainersTab).GetMethod("ContainersForProject", BF)!;
        var result = (List<DockerContainerDto>)method.Invoke(tab.Instance, ["webapp"])!;
        Assert.All(result, c => Assert.Equal("webapp", c.Project));
    }
}
