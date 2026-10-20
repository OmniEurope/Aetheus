// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerDockerSectionFinalTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags PrivStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerDockerSectionFinalTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private static ServerDetailDto MakeDockerServer(bool initialLoaded = true) => new()
    {
        Id = 20,
        Name = "docker-srv",
        Hostname = "docker.example.com",
        IpAddress = "10.0.2.1",
        Type = ServerType.Docker,
        Status = ServerStatus.Online,
        CpuPercent = 30,
        MemoryUsedMb = 8192,
        MemoryTotalMb = 32768,
        DiskUsedGb = 100,
        DiskTotalGb = 500,
        Tags = [],
        Services = [],
        Apache = new ApacheDataDto { Modules = [], VirtualHosts = [] },
        Docker = new DockerDataDto
        {
            Containers =
            [
                new DockerContainerDto { ContainerId = "aaaaaaaaaaaa1111", Name = "web", Image = "nginx:latest", State = "running", Status = "Up 2 hours", Ports = "80/tcp, 443/tcp", Project = "webapp", CpuPercent = 5.5, MemoryUsageMb = 256, MemoryLimitMb = 512 },
                new DockerContainerDto { ContainerId = "bbbbbbbbbbbb2222", Name = "db", Image = "postgres:15", State = "running", Status = "Up 1 day", Ports = "5432/tcp", Project = "webapp", CpuPercent = 2.1, MemoryUsageMb = 512, MemoryLimitMb = 1024 },
                new DockerContainerDto { ContainerId = "cccccccccccc3333", Name = "redis", Image = "redis:7", State = "exited", Status = "Exited (0) 1 hour ago", Ports = "", Project = "cache", CpuPercent = 0, MemoryUsageMb = 0, MemoryLimitMb = 128 },
                new DockerContainerDto { ContainerId = "dddddddddddd4444", Name = "worker", Image = "myapp:v2", State = "paused", Status = "Paused", Ports = "", Project = "webapp", CpuPercent = 0.1, MemoryUsageMb = 64, MemoryLimitMb = 256 },
                new DockerContainerDto { ContainerId = "eeeeeeeeeeee5555", Name = "orphan", Image = "busybox", State = "exited", Status = "Exited (1) 2 days ago", Ports = "", Project = "", CpuPercent = 0, MemoryUsageMb = 0, MemoryLimitMb = 0 }
            ],
            Images =
            [
                new DockerImageDto { ImageId = "sha256:aaaa1111bbbb2222", Repository = "nginx", Tag = "latest", Size = "142MB", Created = DateTime.Now.AddDays(-5), Project = "webapp" },
                new DockerImageDto { ImageId = "sha256:cccc3333dddd4444", Repository = "postgres", Tag = "15", Size = "379MB", Created = DateTime.Now.AddDays(-10), Project = "webapp" },
                new DockerImageDto { ImageId = "sha256:eeee5555ffff6666", Repository = "redis", Tag = "7", Size = "38MB", Created = DateTime.Now.AddDays(-2), Project = "cache" }
            ],
            ComposeStacks =
            [
                new DockerComposeStackDto { Name = "webapp", Status = "running", RunningCount = 3, TotalCount = 3, ConfigFile = "/opt/webapp/docker-compose.yml" },
                new DockerComposeStackDto { Name = "monitoring", Status = "stopped", RunningCount = 0, TotalCount = 2, ConfigFile = "/opt/monitoring/docker-compose.yml" }
            ],
            Networks =
            [
                new DockerNetworkDto { NetworkId = "net1111aaaa2222", Name = "webapp_default", Driver = "bridge", Scope = "local", Project = "webapp" },
                new DockerNetworkDto { NetworkId = "net3333cccc4444", Name = "host", Driver = "host", Scope = "local", Project = "" }
            ],
            Volumes =
            [
                new DockerVolumeDto { Name = "webapp_pgdata", Driver = "local", Mountpoint = "/var/lib/docker/volumes/webapp_pgdata/_data", Project = "webapp" },
                new DockerVolumeDto { Name = "orphan_vol", Driver = "local", Mountpoint = "/var/lib/docker/volumes/orphan_vol/_data", Project = "" }
            ]
        }
    };

    private IRenderedComponent<ServerDockerSection> RenderSection(ServerDetailDto? server = null, bool initialLoaded = true)
    {
        var srv = server ?? MakeDockerServer();
        _handler.SetJsonResponse($"api/servers/{srv.Id}/docker/containers", srv.Docker.Containers);
        _handler.SetJsonResponse($"api/servers/{srv.Id}/docker/action", true);
        _handler.SetJsonResponse($"api/servers/{srv.Id}/docker/images/pull", true);
        _handler.SetJsonResponse($"api/servers/{srv.Id}/docker/images", true);
        _handler.SetJsonResponse($"api/servers/{srv.Id}/docker/compose", true);
        _handler.SetJsonResponse($"api/servers/{srv.Id}/docker/prune", true);
        _handler.SetJsonResponse($"api/servers/{srv.Id}/docker/containers/logs", "log output");
        _handler.SetJsonResponse($"api/servers/{srv.Id}/docker/resources", true);
        _handler.SetJsonResponse($"api/servers/{srv.Id}/docker/inspect", true);
        _handler.SetJsonResponse($"api/servers/{srv.Id}/docker/compose/file", true);
        _handler.SetJsonResponse($"api/servers/{srv.Id}/docker/shell", true);
        _handler.SetJsonResponse($"api/servers/{srv.Id}/docker/env", true);
        _handler.SetJsonResponse($"api/servers/{srv.Id}/docker/browse", true);
        _handler.SetJsonResponse($"api/servers/{srv.Id}/docker/build", true);
        return Render<ServerDockerSection>(p => p
            .Add(x => x.Server, srv)
            .Add(x => x.ServerId, srv.Id)
            .Add(x => x.DockerInitialLoaded, initialLoaded));
    }

    // === Render tests ===

    [Fact]
    public void Renders_ContainerList_WithAllStates()
    {
        var cut = RenderSection();
        Assert.Contains("webapp", cut.Markup);
        Assert.Contains("cache", cut.Markup);
    }

    [Fact]
    public void Renders_SkeletonState_WhenNotInitialLoaded()
    {
        var srv = MakeDockerServer();
        srv = srv with { Docker = srv.Docker with { Containers = [] } };
        var cut = RenderSection(srv, initialLoaded: false);
        Assert.Contains("docker-skeleton", cut.Markup);
    }

    [Fact]
    public void Renders_ImagesTab()
    {
        var cut = RenderSection();
        Assert.Contains("DockerImages", cut.Markup);
    }

    [Fact]
    public void FilteredCompose_ReturnsAllStacks()
    {
        var cut = RenderSection().FindComponent<DockerComposeTab>();
        var prop = typeof(DockerComposeTab).GetProperty("FilteredCompose", Priv)!;
        var stacks = (List<DockerComposeStackDto>)prop.GetValue(cut.Instance)!;
        Assert.Equal(2, stacks.Count);
        Assert.Contains(stacks, s => s.Name == "monitoring");
    }

    // === GetContainerBadge ===

    [Theory]
    [InlineData("running", OmniTone.Success)]
    [InlineData("exited", OmniTone.Danger)]
    [InlineData("paused", OmniTone.Warning)]
    [InlineData("restarting", OmniTone.Accent)]
    [InlineData("created", OmniTone.Neutral)]
    public void GetContainerBadge_ReturnsExpected(string state, OmniTone expected)
    {
        var method = typeof(DockerContainersTab).GetMethod("GetContainerBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [state])!;
        Assert.Equal(expected, result);
    }

    // === GetMemoryBarClass ===

    [Fact]
    public void GetMemoryBarClass_HighUsage_ReturnsRedClass()
    {
        var method = typeof(DockerContainersTab).GetMethod("GetMemoryBarClass", PrivStatic)!;
        // 950/1000 = 95% >= 90
        var c = new DockerContainerDto { MemoryUsageMb = 950, MemoryLimitMb = 1000 };
        var cls = (string)method.Invoke(null, [c])!;
        Assert.Contains("red", cls);
    }

    [Fact]
    public void GetMemoryBarClass_MediumUsage_ReturnsYellowClass()
    {
        var method = typeof(DockerContainersTab).GetMethod("GetMemoryBarClass", PrivStatic)!;
        var c = new DockerContainerDto { MemoryUsageMb = 750, MemoryLimitMb = 1024 };
        var cls = (string)method.Invoke(null, [c])!;
        Assert.Contains("yellow", cls);
    }

    [Fact]
    public void GetMemoryBarClass_LowUsage_ReturnsGreenClass()
    {
        var method = typeof(DockerContainersTab).GetMethod("GetMemoryBarClass", PrivStatic)!;
        var c = new DockerContainerDto { MemoryUsageMb = 200, MemoryLimitMb = 1024 };
        var cls = (string)method.Invoke(null, [c])!;
        Assert.Contains("green", cls);
    }

    [Fact]
    public void GetMemoryBarClass_NoLimit_ReturnsBaseClass()
    {
        var method = typeof(DockerContainersTab).GetMethod("GetMemoryBarClass", PrivStatic)!;
        var c = new DockerContainerDto { MemoryUsageMb = 500, MemoryLimitMb = 0 };
        var cls = (string)method.Invoke(null, [c])!;
        Assert.Equal("docker-mem-bar", cls);
    }

    // === GetProjectAccentClass ===

    [Fact]
    public void GetProjectAccentClass_EmptyProject_ReturnsEmpty()
    {
        var cls = DockerContainersTab.GetProjectAccentClass(null);
        Assert.Equal(string.Empty, cls);
    }

    [Fact]
    public void GetProjectAccentClass_NonEmpty_ReturnsAccentClass()
    {
        var cls = DockerContainersTab.GetProjectAccentClass("webapp");
        Assert.StartsWith("docker-project-accent-", cls);
    }

    [Fact]
    public void GetProjectAccentClass_SameProjectName_ReturnsSameClass()
    {
        var cls1 = DockerContainersTab.GetProjectAccentClass("webapp");
        var cls2 = DockerContainersTab.GetProjectAccentClass("webapp");
        Assert.Equal(cls1, cls2);
    }

    // === FilteredContainers ===

    [Fact]
    public void FilteredContainers_NoSearch_ReturnsAll()
    {
        var cut = RenderSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        var prop = typeof(DockerContainersTab).GetProperty("FilteredContainers", Priv)!;
        var containers = (List<DockerContainerDto>)prop.GetValue(tab.Instance)!;
        Assert.Equal(5, containers.Count);
    }

    [Fact]
    public void FilteredContainers_ByState_FiltersRunning()
    {
        var cut = RenderSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        typeof(DockerContainersTab).GetField("_containerSearch", Priv)!.SetValue(tab.Instance, "running");
        var prop = typeof(DockerContainersTab).GetProperty("FilteredContainers", Priv)!;
        var containers = (List<DockerContainerDto>)prop.GetValue(tab.Instance)!;
        Assert.Equal(2, containers.Count);
    }

    // === ContainersForProject ===

    [Fact]
    public void ContainersForProject_ReturnsProjectContainers()
    {
        var cut = RenderSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        var method = typeof(DockerContainersTab).GetMethod("ContainersForProject", Priv)!;
        var result = (List<DockerContainerDto>)method.Invoke(tab.Instance, ["webapp"])!;
        Assert.Equal(3, result.Count); // web, db, worker
    }

    [Fact]
    public void ContainersForProject_Empty_ReturnsEmptyOrphanSlot()
    {
        var cut = RenderSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        var method = typeof(DockerContainersTab).GetMethod("ContainersForProject", Priv)!;
        var result = (List<DockerContainerDto>)method.Invoke(tab.Instance, [""])!;
        Assert.Single(result); // orphan
    }

    // === ParseEnvVars ===

    [Fact]
    public void ParseEnvVars_ValidJson_ParsesKeyValue()
    {
        var method = typeof(DockerContainersTab).GetMethod("ParseEnvVars", PrivStatic)!;
        var result = (List<DockerEnvVarDto>)method.Invoke(null, ["[\"KEY1=value1\",\"KEY2=val=ue2\"]"])!;
        Assert.Equal(2, result.Count);
        Assert.Equal("KEY1", result[0].Key);
        Assert.Equal("value1", result[0].Value);
        Assert.Equal("KEY2", result[1].Key);
        Assert.Equal("val=ue2", result[1].Value);
    }

    [Fact]
    public void ParseEnvVars_Empty_ReturnsEmpty()
    {
        var method = typeof(DockerContainersTab).GetMethod("ParseEnvVars", PrivStatic)!;
        var result = (List<DockerEnvVarDto>)method.Invoke(null, ["[]"])!;
        Assert.Empty(result);
    }

    // === DockerPruneDialog selection ===

    [Fact]
    public void SetAll_True_SetsAllFlags()
    {
        var component = new DockerPruneDialog();
        typeof(DockerPruneDialog).GetMethod("SetAll", Priv)!.Invoke(component, [true]);
        var model = (DockerPruneDialogModel)typeof(DockerPruneDialog).GetField("_model", Priv)!.GetValue(component)!;

        Assert.True(model.Containers);
        Assert.True(model.Images);
        Assert.True(model.Volumes);
    }

    [Fact]
    public void SetAll_False_ClearsAllFlags()
    {
        var component = new DockerPruneDialog();
        typeof(DockerPruneDialog).GetMethod("SetAll", Priv)!.Invoke(component, [false]);
        var model = (DockerPruneDialogModel)typeof(DockerPruneDialog).GetField("_model", Priv)!.GetValue(component)!;

        Assert.False(model.Containers);
        Assert.False(model.Images);
        Assert.False(model.Volumes);
    }

    [Fact]
    public void PruneSelectAllToggle_ThroughRenderedControl_ClearsEverySelection()
    {
        var cut = Render<DockerPruneDialog>();

        cut.Find("input.omni-checkbox").Change(false);

        var model = cut.Instance.Model;
        Assert.False(model.Containers);
        Assert.False(model.Images);
        Assert.False(model.Volumes);
        Assert.Contains("PruneSummary", cut.Markup, StringComparison.Ordinal);
    }

    // === OpenProjectZoom / CloseProjectZoom ===

    [Fact]
    public async Task OpenProjectZoom_OpensProjectDialog()
    {
        var cut = RenderSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        var method = typeof(DockerContainersTab).GetMethod("OpenProjectZoom", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(tab.Instance, ["webapp"])!);

        Assert.Equal(typeof(DockerProjectDialog), dialog.LastComponent);
        Assert.Equal("webapp", dialog.LastParameters!["Project"]);
    }

    [Fact]
    public async Task OpenProjectZoom_ActionResult_AppliesToProjectContainers()
    {
        var cut = RenderSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.OpenResult = DockerContainerAction.Restart;
        var method = typeof(DockerContainersTab).GetMethod("OpenProjectZoom", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(tab.Instance, ["webapp"])!);

        Assert.Contains(_handler.Requests, request => request.Method == "POST" && request.Url.Contains("docker/action"));
    }

    // === OpenShell / ToggleLogs ===

    [Fact]
    public async Task ToggleLogsAsync_OpensLogsPanel()
    {
        var cut = RenderSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        var method = typeof(DockerContainersTab).GetMethod("ToggleLogsAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(tab.Instance, ["aaaaaaaaaaaa1111"])!);
        var logsId = (string?)typeof(DockerContainersTab).GetField("_logsContainerId", Priv)!.GetValue(tab.Instance);
        Assert.Equal("aaaaaaaaaaaa1111", logsId);
    }

    [Fact]
    public async Task ToggleLogsAsync_ToggleSameId_ClosesPanel()
    {
        var cut = RenderSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        typeof(DockerContainersTab).GetField("_logsContainerId", Priv)!.SetValue(tab.Instance, "aaaaaaaaaaaa1111");
        var method = typeof(DockerContainersTab).GetMethod("ToggleLogsAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(tab.Instance, ["aaaaaaaaaaaa1111"])!);
        var logsId = (string?)typeof(DockerContainersTab).GetField("_logsContainerId", Priv)!.GetValue(tab.Instance);
        Assert.Null(logsId);
    }

    [Fact]
    public void OpenShell_SetsShellContainerId()
    {
        var cut = RenderSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        typeof(DockerContainersTab).GetMethod("OpenShell", Priv)!.Invoke(tab.Instance, ["aaaaaaaaaaaa1111", "web"]);
        var shellId = (string?)typeof(DockerContainersTab).GetField("_shellContainerId", Priv)!.GetValue(tab.Instance);
        Assert.Equal("aaaaaaaaaaaa1111", shellId);
    }

    [Fact]
    public void OpenShell_ToggleSameId_ClosesShell()
    {
        var cut = RenderSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        typeof(DockerContainersTab).GetField("_shellContainerId", Priv)!.SetValue(tab.Instance, "aaaaaaaaaaaa1111");
        typeof(DockerContainersTab).GetMethod("OpenShell", Priv)!.Invoke(tab.Instance, ["aaaaaaaaaaaa1111", "web"]);
        var shellId = (string?)typeof(DockerContainersTab).GetField("_shellContainerId", Priv)!.GetValue(tab.Instance);
        Assert.Null(shellId);
    }

    // === DispatchTaskOutput ===

    [Fact]
    public async Task HandleTaskCompleted_InspectOutput_SetsInspectContent()
    {
        var cut = RenderSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        typeof(DockerContainersTab).GetField("_inspectContainerId", Priv)!.SetValue(tab.Instance, "aaaaaaaaaaaa1111");
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 1,
            ServerId = 20,
            TaskName = "docker inspect",
            Status = TaskExecutionStatus.Success,
            Output = "{\"Id\":\"aaa\"}"
        })));
        var content = (string?)typeof(DockerContainersTab).GetField("_inspectContent", Priv)!.GetValue(tab.Instance);
        Assert.Contains("aaa", content!);
    }

    [Fact]
    public async Task HandleTaskCompleted_ComposeFile_SetsEditorContent()
    {
        var cut = RenderSection();
        var composeTab = cut.FindComponent<DockerComposeTab>();
        typeof(DockerComposeTab).GetField("_composeEditorVisible", Priv)!.SetValue(composeTab.Instance, true);
        typeof(DockerComposeTab).GetField("_composeFileLoading", Priv)!.SetValue(composeTab.Instance, true);
        typeof(DockerComposeTab).GetField("_composeEditorStack", Priv)!.SetValue(composeTab.Instance, "web");
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 2,
            ServerId = 20,
            TaskName = DockerTaskNames.ComposeFile("web"),
            Status = TaskExecutionStatus.Success,
            Output = "version: '3'"
        })));
        var content = (string)typeof(DockerComposeTab).GetField("_composeEditorContent", Priv)!.GetValue(composeTab.Instance)!;
        Assert.Equal("version: '3'", content);
    }

    // === OpenResourceLimitsDialog ===

    [Fact]
    public async Task OpenResourceLimitsDialog_SetsContainerAndMemory()
    {
        var cut = RenderSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        var method = typeof(DockerContainersTab).GetMethod("OpenResourceLimitsDialog", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(tab.Instance, ["aaaaaaaaaaaa1111"])!);

        Assert.Equal(typeof(DockerResourceLimitsDialog), dialog.LastComponent);
        Assert.Equal("aaaaaaaaaaaa1111", dialog.LastParameters!["ContainerId"]);
        Assert.Equal(512, dialog.LastParameters["MemoryLimitMb"]);
    }

    // === Dispose ===

    [Fact]
    public async Task DisposeAsync_NoThrow_AndIsSafeTwice()
    {
        var cut = RenderSection();

        await cut.Instance.DisposeAsync();
        var second = await Record.ExceptionAsync(async () => await cut.Instance.DisposeAsync());

        Assert.Null(second);
    }

    // === ConfirmRemoveContainerAsync ===

    [Fact]
    public async Task ConfirmRemoveContainerAsync_OpensNativeConfirmation()
    {
        var cut = RenderSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        var method = typeof(DockerContainersTab).GetMethod("ConfirmRemoveContainerAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(tab.Instance, ["cccccccccccc3333", "redis"])!);

        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        Assert.Equal("RemoveContainer", dialog.LastTitle);
        Assert.Equal("RemoveContainerConfirm", dialog.LastConfirmMessage);
    }

    [Fact]
    public async Task ConfirmRemoveImageAsync_RequiresConfirmationBeforeDelete()
    {
        var server = MakeDockerServer();
        var cut = Render<DockerImagesTab>(p => p
            .Add(x => x.ServerId, server.Id)
            .Add(x => x.Images, server.Docker.Images));
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.ConfirmResult = false;
        var image = server.Docker.Images[0];
        var method = typeof(DockerImagesTab).GetMethod("ConfirmRemoveImageAsync", Priv)!;

        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [image])!);

        Assert.Equal("DockerRemoveImageConfirm", dialog.LastConfirmMessage);
        Assert.DoesNotContain(_handler.Requests, request =>
            request.Method == "DELETE" && request.Url.Contains("docker/images"));
    }

    // === OnShellKeyDown ===

    [Fact]
    public async Task OnShellKeyDown_Enter_SendsCommand()
    {
        _handler.SetResponse(
            HttpMethod.Post,
            "api/servers/20/docker/exec",
            System.Net.HttpStatusCode.NoContent);
        var cut = RenderSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        typeof(DockerContainersTab).GetField("_shellContainerId", Priv)!.SetValue(tab.Instance, "aaaaaaaaaaaa1111");
        typeof(DockerContainersTab).GetField("_shellCommand", Priv)!.SetValue(tab.Instance, "ls -la");

        var method = typeof(DockerContainersTab).GetMethod("OnShellKeyDown", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(tab.Instance, [new KeyboardEventArgs { Key = "Enter" }])!);
        // Enter triggers SendShellCommandAsync, which POSTs the command to the docker/exec endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/20/docker/exec"));
    }

    [Fact]
    public async Task OnShellKeyDown_OtherKey_NoOp()
    {
        var cut = RenderSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        typeof(DockerContainersTab).GetField("_shellContainerId", Priv)!.SetValue(tab.Instance, "aaaaaaaaaaaa1111");
        typeof(DockerContainersTab).GetField("_shellCommand", Priv)!.SetValue(tab.Instance, "ls");

        var method = typeof(DockerContainersTab).GetMethod("OnShellKeyDown", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(tab.Instance, [new KeyboardEventArgs { Key = "Shift" }])!);
        // Command still set since not sent
        var cmd = (string)typeof(DockerContainersTab).GetField("_shellCommand", Priv)!.GetValue(tab.Instance)!;
        Assert.Equal("ls", cmd);
    }
}
