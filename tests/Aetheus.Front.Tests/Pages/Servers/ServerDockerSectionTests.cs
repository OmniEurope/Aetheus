// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerDockerSectionTests : BunitContext
{
    public ServerDockerSectionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private readonly BunitTestHelper.TestHandler _handler;

    private static ServerDetailDto MakeDockerServer(int id = 20, string name = "method-test-srv") => new()
    {
        Id = id,
        Name = name,
        Hostname = "10.0.0.1",
        Type = ServerType.Docker,
        Status = ServerStatus.Online,
        CpuPercent = 45.2,
        MemoryUsedMb = 8192,
        MemoryTotalMb = 16384,
        DiskUsedGb = 50,
        DiskTotalGb = 200,
        Tags = ["prod"],
        Services = [],
        Docker = new DockerDataDto
        {
            Containers =
            [
                new DockerContainerDto { ContainerId = "abc123def456", Name = "nginx", Image = "nginx:latest", State = "running", CpuPercent = 5, MemoryUsageMb = 64, MemoryLimitMb = 256, Status = "Up 2h" },
                new DockerContainerDto { ContainerId = "def789ghi012", Name = "api", Image = "dotnet:8", State = "exited", CpuPercent = 0, MemoryUsageMb = 0, MemoryLimitMb = 512, Status = "Exited" }
            ],
            Images =
            [
                new DockerImageDto { ImageId = "i1", Repository = "nginx", Tag = "latest", Size = "142MB" },
                new DockerImageDto { ImageId = "i2", Repository = "dotnet", Tag = "8", Size = "800MB" }
            ],
            ComposeStacks =
            [
                new DockerComposeStackDto { Name = "monitoring", Status = "running(3)", RunningCount = 3, TotalCount = 3 }
            ],
            Networks = [new DockerNetworkDto { NetworkId = "n1", Name = "bridge", Driver = "bridge", Scope = "local" }],
            Volumes = [new DockerVolumeDto { Name = "pgdata", Driver = "local", Mountpoint = "/data" }]
        }
    };

    private IRenderedComponent<ServerDockerSection> RenderDockerSection()
    {
        var server = MakeDockerServer();
        _handler.SetJsonResponse("api/servers/20/docker/containers", new List<DockerContainerDto>());
        _handler.SetJsonResponse("api/servers/20/docker/action", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/containers/logs", "log output here");
        _handler.SetJsonResponse("api/servers/20/docker/images", new List<DockerImageDto>());
        _handler.SetJsonResponse("api/servers/20/docker/images/pull", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/compose/action", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/prune", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/resource-limits", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/containers/abc123def456/inspect", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/compose/monitoring/file", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/compose/file", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/exec", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/containers/abc123def456/env", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/containers/browse", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/build", "{}");

        return Render<ServerDockerSection>(p => p
            .Add(x => x.Server, server)
            .Add(x => x.ServerId, 20)
            .Add(x => x.DockerInitialLoaded, true));
    }

    /// <summary>
    /// The images tab is a component in its own right now, so it is rendered on its own: the parent no
    /// longer holds any of the state these tests read.
    /// </summary>
    private IRenderedComponent<DockerImagesTab> RenderImagesTab()
    {
        _handler.SetJsonResponse("api/servers/20/docker/images/pull", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/build", "{}");

        return Render<DockerImagesTab>(p => p
            .Add(x => x.ServerId, 20)
            .Add(x => x.Images, MakeDockerServer().Docker.Images));
    }

    /// <summary>
    /// The containers tab, rendered on its own. Tests that only need the tab render it directly; the
    /// ones that go through the parent's SignalR entry point still render the section and fish it out.
    /// </summary>
    private IRenderedComponent<DockerContainersTab> RenderContainersTab()
    {
        var server = MakeDockerServer();
        _handler.SetJsonResponse("api/servers/20/docker/action", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/containers/logs", "log output here");
        _handler.SetJsonResponse("api/servers/20/docker/resource-limits", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/containers/abc123def456/inspect", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/exec", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/containers/abc123def456/env", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/containers/browse", "{}");

        return Render<DockerContainersTab>(p => p
            .Add(x => x.Server, server)
            .Add(x => x.ServerId, 20)
            .Add(x => x.InitialLoaded, true));
    }

    private IRenderedComponent<DockerComposeTab> RenderComposeTab()
    {
        _handler.SetJsonResponse("api/servers/20/docker/compose/action", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/compose/monitoring/file", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/compose/file", "{}");

        return Render<DockerComposeTab>(p => p
            .Add(x => x.ServerId, 20)
            .Add(x => x.Stacks, MakeDockerServer().Docker.ComposeStacks));
    }

    // === Static method tests ===

    [Theory]
    [InlineData("running", OmniTone.Success)]
    [InlineData("exited", OmniTone.Danger)]
    [InlineData("paused", OmniTone.Warning)]
    [InlineData("unknown", OmniTone.Neutral)]
    public void GetContainerBadge_ReturnsExpectedStyle(string state, OmniTone expected)
    {
        // A360-10: called directly. These are pure functions, so reflection bought nothing except a
        // test that keeps compiling after the method it targets is renamed or deleted.
        Assert.Equal(expected, DockerContainersTab.GetContainerBadge(state));
    }

    [Theory]
    [InlineData(0, 256, "docker-mem-bar docker-mem-green")]
    [InlineData(200, 256, "docker-mem-bar docker-mem-yellow")]
    [InlineData(240, 256, "docker-mem-bar docker-mem-red")]
    [InlineData(50, 0, "docker-mem-bar")]
    public void GetMemoryBarClass_ReturnsExpectedClass(double usage, double limit, string expected)
    {
        var container = new DockerContainerDto { MemoryUsageMb = usage, MemoryLimitMb = limit };

        Assert.Equal(expected, DockerContainersTab.GetMemoryBarClass(container));
    }

    [Fact]
    public void ParseEnvVars_ParsesValidJson()
    {
        var result = DockerContainersTab.ParseEnvVars("[\"PATH=/usr/bin\",\"HOME=/root\",\"DB_HOST=localhost\"]");
        Assert.Equal(3, result.Count);
        Assert.Equal("PATH", result[0].Key);
        Assert.Equal("/usr/bin", result[0].Value);
        Assert.Equal("HOME", result[1].Key);
        Assert.Equal("DB_HOST", result[2].Key);
    }

    [Fact]
    public void ParseEnvVars_HandlesEmptyInput()
    {
        Assert.Empty(DockerContainersTab.ParseEnvVars("[]"));
    }

    // === Instance method tests ===

    [Fact]
    public void PruneDialog_DefaultModel_SelectsAllResourceKinds()
    {
        var model = new DockerPruneDialogModel();

        Assert.True(model.Containers);
        Assert.True(model.Images);
        Assert.True(model.Volumes);
    }

    [Fact]
    public async Task DockerActionAsync_SendsAction()
    {
        var cut = RenderContainersTab();

        var method = typeof(DockerContainersTab).GetMethod("DockerActionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, ["abc123def456", DockerContainerAction.Stop])!;

        var target = typeof(DockerContainersTab).GetField("_dockerActionTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Null(target);
    }

    [Fact]
    public async Task ConfirmRemoveContainerAsync_OpensDialogWithoutDeletingWhenCancelled()
    {
        var cut = RenderContainersTab();

        var method = typeof(DockerContainersTab).GetMethod("ConfirmRemoveContainerAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, ["abc123def456", "nginx"])!);

        Assert.Equal(1, dialog.OpenCount);
        Assert.Equal("RemoveContainerConfirm", dialog.LastConfirmMessage);
        Assert.Equal("RemoveContainer", dialog.LastTitle);
        Assert.DoesNotContain(_handler.Requests, request => request.Method == "POST" && request.Url.Contains("docker/action"));
    }

    [Fact]
    public async Task ConfirmRemoveContainerAsync_ConfirmedSendsRemoveAction()
    {
        var cut = RenderContainersTab();

        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.ConfirmResult = true;
        var method = typeof(DockerContainersTab).GetMethod("ConfirmRemoveContainerAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, ["abc123def456", "nginx"])!);

        Assert.Contains(_handler.Requests, request => request.Method == "POST" && request.Url.Contains("docker/action"));
    }

    [Fact]
    public async Task ToggleLogsAsync_ToglesLogState()
    {
        var cut = RenderContainersTab();

        var method = typeof(DockerContainersTab).GetMethod("ToggleLogsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // First toggle - open
        await (Task)method.Invoke(cut.Instance, ["abc123def456"])!;
        var logsId = typeof(DockerContainersTab).GetField("_logsContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Equal("abc123def456", logsId);

        // Second toggle - close
        await (Task)method.Invoke(cut.Instance, ["abc123def456"])!;
        logsId = typeof(DockerContainersTab).GetField("_logsContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Null(logsId);
    }

    [Fact]
    public async Task PullImageAsync_WithEmptyName_MakesNoRequest()
    {
        var cut = RenderImagesTab();

        var method = typeof(DockerImagesTab).GetMethod("PullImageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("docker/images/pull"));
    }

    [Fact]
    public async Task PullImageAsync_WithName_SendsRequest()
    {
        var cut = RenderImagesTab();

        typeof(DockerImagesTab).GetField("_pullImageName", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "nginx:latest");

        var method = typeof(DockerImagesTab).GetMethod("PullImageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        Assert.Contains(_handler.Requests, r => r.Url.Contains("docker/images/pull"));
    }

    [Fact]
    public async Task RemoveImageAsync_SendsRequest()
    {
        _handler.SetJsonResponse("api/servers/20/docker/images/abc123def456", "{}");
        var cut = RenderImagesTab();

        var method = typeof(DockerImagesTab).GetMethod("RemoveImageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, ["abc123def456"])!;

        // RemoveDockerImageAsync issues a DELETE to the image endpoint (image id in the path).
        Assert.Contains(_handler.Requests, r => r.Method == "DELETE" && r.Url.Contains("api/servers/20/docker/images/abc123def456"));
    }

    [Fact]
    public async Task ComposeActionAsync_SendsRequest()
    {
        var cut = RenderComposeTab();

        var method = typeof(DockerComposeTab).GetMethod("ComposeActionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, ["monitoring", DockerComposeAction.Up])!;

        // ExecuteComposeActionAsync POSTs the compose action to the compose/action endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/20/docker/compose/action"));
    }

    [Fact]
    public void PruneDialog_RendersAllResourceChoices()
    {
        var cut = Render<DockerPruneDialog>();

        Assert.True(cut.FindAll("input").Count >= 3);
        Assert.Contains("DockerPrune", cut.Markup);
    }

    [Fact]
    public async Task ExecutePruneAsync_SendsRequest()
    {
        var cut = RenderDockerSection();

        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.OpenResult = new DockerPruneDialogResult(true, true, false);
        var method = typeof(ServerDockerSection).GetMethod("OpenPruneDialogAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        Assert.Contains(_handler.Requests, r => r.Url.Contains("docker/prune"));
    }

    [Fact]
    public async Task OpenResourceLimitsDialog_PrefillsContainerLimits()
    {
        var cut = RenderContainersTab();

        var method = typeof(DockerContainersTab).GetMethod("OpenResourceLimitsDialog", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, ["abc123def456"])!);

        Assert.Equal("abc123def456", dialog.LastParameters!["ContainerId"]);
        Assert.Equal(256, dialog.LastParameters["MemoryLimitMb"]);
    }

    [Fact]
    public async Task ApplyResourceLimitsAsync_SendsRequest()
    {
        var cut = RenderContainersTab();

        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.OpenResult = new DockerResourceLimitsDialogResult(1.5, 512);
        var method = typeof(DockerContainersTab).GetMethod("OpenResourceLimitsDialog", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, ["abc123def456"])!);

        Assert.Contains(_handler.Requests, r => r.Url.Contains("docker/resource-limits"));
    }

    [Fact]
    public void OnContainerRowRender_SetsClassForRunning()
    {
        var cut = RenderContainersTab();

        Assert.Contains("docker-row-running", cut.Find("tr[data-omni-row-index='0']").ClassList);
    }

    [Fact]
    public void OnContainerRowRender_SetsClassForExited()
    {
        var cut = RenderContainersTab();

        Assert.Contains("docker-row-exited", cut.Find("tr[data-omni-row-index='1']").ClassList);
    }

    [Fact]
    public void ProjectToolbar_StartsOnlyContainersInSelectedProject()
    {
        _handler.SetJsonResponse("api/servers/20/docker/action", "{}");
        var original = MakeDockerServer();
        var server = original with
        {
            Docker = original.Docker with
            {
                Containers =
                [
                    new DockerContainerDto { ContainerId = "alpha-one", Name = "first", Project = "alpha", State = "exited" },
                    new DockerContainerDto { ContainerId = "beta-one", Name = "second", Project = "beta", State = "exited" }
                ]
            }
        };
        var cut = Render<DockerContainersTab>(parameters => parameters
            .Add(component => component.Server, server)
            .Add(component => component.ServerId, 20)
            .Add(component => component.InitialLoaded, true));

        cut.Find("select#docker-bulk-project").Change("0");
        cut.Find("button[title='StartAll']").Click();

        Assert.Single(_handler.Requests, request => request.Url.Contains("docker/action", StringComparison.Ordinal));
        cut.Find("input[placeholder='FilterContainers']").Input("second");
        Assert.True(cut.Find("button[title='StartAll']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task RequestInspectAsync_TogglesInspect()
    {
        var cut = RenderContainersTab();

        var method = typeof(DockerContainersTab).GetMethod("RequestInspectAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // First call - open
        await (Task)method.Invoke(cut.Instance, ["abc123def456"])!;
        var inspectId = typeof(DockerContainersTab).GetField("_inspectContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Equal("abc123def456", inspectId);

        // Second call - close
        await (Task)method.Invoke(cut.Instance, ["abc123def456"])!;
        inspectId = typeof(DockerContainersTab).GetField("_inspectContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Null(inspectId);
    }

    [Fact]
    public async Task OpenComposeEditorAsync_SetsFields()
    {
        var cut = RenderComposeTab();

        var method = typeof(DockerComposeTab).GetMethod("OpenComposeEditorAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, ["monitoring"])!;

        var visible = (bool)typeof(DockerComposeTab).GetField("_composeEditorVisible", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.True(visible);

        var stack = (string)typeof(DockerComposeTab).GetField("_composeEditorStack", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Equal("monitoring", stack);
    }

    [Fact]
    public void CloseComposeEditor_ResetsFields()
    {
        var cut = RenderComposeTab();

        typeof(DockerComposeTab).GetField("_composeEditorVisible", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, true);
        typeof(DockerComposeTab).GetField("_composeEditorStack", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "monitoring");

        var method = typeof(DockerComposeTab).GetMethod("CloseComposeEditor", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, []);

        var visible = (bool)typeof(DockerComposeTab).GetField("_composeEditorVisible", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.False(visible);
    }

    [Fact]
    public async Task SaveComposeFileAsync_WithEmptyContent_MakesNoRequest()
    {
        var cut = RenderComposeTab();

        typeof(DockerComposeTab).GetField("_composeEditorContent", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, string.Empty);

        var method = typeof(DockerComposeTab).GetMethod("SaveComposeFileAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        // Empty content short-circuits before any HTTP: no compose-file PUT/POST is emitted.
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("docker/compose") && r.Url.Contains("/file"));
    }

    [Fact]
    public async Task SaveComposeFileAsync_WithContent_SendsRequest()
    {
        var cut = RenderComposeTab();

        typeof(DockerComposeTab).GetField("_composeEditorStack", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "monitoring");
        typeof(DockerComposeTab).GetField("_composeEditorContent", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "version: '3'");
        typeof(DockerComposeTab).GetField("_composeEditorVisible", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, true);

        var method = typeof(DockerComposeTab).GetMethod("SaveComposeFileAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        Assert.Contains(_handler.Requests, r => r.Url.Contains("docker/compose") && r.Url.Contains("/file"));
    }

    [Fact]
    public void OpenShell_TogglesShellState()
    {
        var cut = RenderContainersTab();

        var method = typeof(DockerContainersTab).GetMethod("OpenShell", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Open
        method.Invoke(cut.Instance, ["abc123def456", "nginx"]);
        var shellId = typeof(DockerContainersTab).GetField("_shellContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Equal("abc123def456", shellId);

        // Close (same container ID)
        method.Invoke(cut.Instance, ["abc123def456", "nginx"]);
        shellId = typeof(DockerContainersTab).GetField("_shellContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Null(shellId);
    }

    [Fact]
    public async Task SendShellCommandAsync_WithEmptyCommand_ProducesNothing()
    {
        var cut = RenderContainersTab();

        typeof(DockerContainersTab).GetField("_shellContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "abc123def456");
        typeof(DockerContainersTab).GetField("_shellCommand", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, string.Empty);

        var method = typeof(DockerContainersTab).GetMethod("SendShellCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        var output = (string)typeof(DockerContainersTab).GetField("_shellOutput", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Empty(output);
        // An empty command must not reach the agent.
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("docker/exec"));
    }

    [Fact]
    public async Task SendShellCommandAsync_WithCommand_SendsRequest()
    {
        var cut = RenderContainersTab();

        typeof(DockerContainersTab).GetField("_shellContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "abc123def456");
        typeof(DockerContainersTab).GetField("_shellCommand", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "ls -la");

        var method = typeof(DockerContainersTab).GetMethod("SendShellCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        var output = (string)typeof(DockerContainersTab).GetField("_shellOutput", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Contains("$ ls -la", output);
        // The command is dispatched to the agent via a POST to the docker/exec endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/20/docker/exec"));
    }

    [Fact]
    public async Task RequestEnvVarsAsync_TogglesEnvVars()
    {
        var cut = RenderContainersTab();

        var method = typeof(DockerContainersTab).GetMethod("RequestEnvVarsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Open
        await (Task)method.Invoke(cut.Instance, ["abc123def456", "nginx"])!;
        var envId = typeof(DockerContainersTab).GetField("_envContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Equal("abc123def456", envId);

        // Close
        await (Task)method.Invoke(cut.Instance, ["abc123def456", "nginx"])!;
        envId = typeof(DockerContainersTab).GetField("_envContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Null(envId);
    }

    [Fact]
    public async Task OpenFileBrowserAsync_TogglesFileBrowser()
    {
        var cut = RenderContainersTab();

        var method = typeof(DockerContainersTab).GetMethod("OpenFileBrowserAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Open
        await (Task)method.Invoke(cut.Instance, ["abc123def456", "nginx"])!;
        var browseId = typeof(DockerContainersTab).GetField("_browseContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Equal("abc123def456", browseId);

        // Close
        await (Task)method.Invoke(cut.Instance, ["abc123def456", "nginx"])!;
        browseId = typeof(DockerContainersTab).GetField("_browseContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Null(browseId);
    }

    [Fact]
    public async Task BrowseParentAsync_AtRoot_LeavesTheValueUnchanged()
    {
        var cut = RenderContainersTab();

        typeof(DockerContainersTab).GetField("_browsePath", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "/");

        var method = typeof(DockerContainersTab).GetMethod("BrowseParentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        var path = (string)typeof(DockerContainersTab).GetField("_browsePath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Equal("/", path);
    }

    [Fact]
    public async Task BrowseParentAsync_InSubDirectory_GoesUp()
    {
        var cut = RenderContainersTab();

        typeof(DockerContainersTab).GetField("_browseContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "abc123def456");
        typeof(DockerContainersTab).GetField("_browsePath", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "/var/log");

        var method = typeof(DockerContainersTab).GetMethod("BrowseParentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        var path = (string)typeof(DockerContainersTab).GetField("_browsePath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Equal("/var", path);
    }

    [Fact]
    public async Task BuildImageAsync_WithEmptyTag_MakesNoRequest()
    {
        var cut = RenderImagesTab();

        typeof(DockerImagesTab).GetField("_buildImageTag", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, string.Empty);

        var method = typeof(DockerImagesTab).GetMethod("BuildImageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        // Empty tag short-circuits before the build request - nothing is posted to docker/build.
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("docker/build"));
    }

    [Fact]
    public async Task BuildImageAsync_WithContent_SendsRequest()
    {
        var cut = RenderImagesTab();

        typeof(DockerImagesTab).GetField("_buildImageTag", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "myapp:latest");
        typeof(DockerImagesTab).GetField("_buildDockerfileContent", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "FROM alpine\nRUN echo hello");

        var method = typeof(DockerImagesTab).GetMethod("BuildImageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        Assert.Contains(_handler.Requests, r => r.Url.Contains("docker/build"));
    }

    [Fact]
    public void R181_NoRefreshButton_TheContainersFollowTheHeartbeatInventory()
    {
        var cut = RenderDockerSection();
        Assert.Empty(cut.FindAll(".docker-toolbar button[title='Refresh']"));
        // No polling toggle either: the heartbeat is the only source of the inventory.
        Assert.Empty(cut.FindAll(".docker-toolbar .omni-checkbox-label"));
        Assert.Empty(cut.FindAll(".docker-toolbar .omni-switch"));
        var server = MakeDockerServer();
        var pushed = server with
        {
            Docker = server.Docker with
            {
                Containers = [new DockerContainerDto { ContainerId = "fedcba987654", Name = "pushed-by-heartbeat", Image = "nginx", State = "running" }]
            }
        };

        // ServerDetailLoader hands every heartbeat's inventory down as a new Server: no fetch involved.
        cut.Render(p => p.Add(x => x.Server, pushed));

        cut.WaitForAssertion(() => Assert.Contains("pushed-by-heartbeat", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("docker/containers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServerChange_ResetsTransientStateAndIgnoresOldNotifications()
    {
        var cut = RenderDockerSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        typeof(DockerContainersTab).GetField("_shellContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(tab.Instance, "abc123def456");
        typeof(DockerContainersTab).GetField("_inspectContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(tab.Instance, "abc123def456");

        var nextServer = MakeDockerServer(21, "next-server");
        cut.Render(parameters => parameters
            .Add(component => component.Server, nextServer)
            .Add(component => component.ServerId, 21)
            .Add(component => component.DockerInitialLoaded, true));

        // @key="ServerId" is what resets the tab: the old instance is discarded rather than cleaned up,
        // so the state set above is gone from whatever component now occupies the slot.
        var rebuiltTab = cut.FindComponent<DockerContainersTab>();
        Assert.Null(typeof(DockerContainersTab).GetField("_shellContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(rebuiltTab.Instance));
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification
        {
            TaskId = 1,
            ServerId = 20,
            TaskName = "docker inspect",
            Status = TaskExecutionStatus.Success,
            Output = "old-server-output"
        }));
        Assert.Null(typeof(DockerContainersTab).GetField("_inspectContent", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(rebuiltTab.Instance));
    }

    [Fact]
    public async Task PruneAsync_WithFlags_SendsRequest()
    {
        var cut = RenderDockerSection();

        var method = typeof(ServerDockerSection).GetMethod("PruneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [true, false, true])!;

        Assert.Contains(_handler.Requests, r => r.Url.Contains("docker/prune"));
    }

    [Fact]
    public async Task OnShellKeyDown_Enter_SendsCommand()
    {
        var cut = RenderContainersTab();

        typeof(DockerContainersTab).GetField("_shellContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "abc123def456");
        typeof(DockerContainersTab).GetField("_shellCommand", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "pwd");

        var method = typeof(DockerContainersTab).GetMethod("OnShellKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var args = new KeyboardEventArgs { Key = "Enter" };
        await (Task)method.Invoke(cut.Instance, [args])!;

        var output = (string)typeof(DockerContainersTab).GetField("_shellOutput", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Contains("$ pwd", output);
        // Enter dispatches the buffered command to the agent (POST docker/exec).
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/20/docker/exec"));
    }

    [Fact]
    public async Task OnShellKeyDown_NonEnter_DoesNotSend()
    {
        var cut = RenderContainersTab();

        typeof(DockerContainersTab).GetField("_shellContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "abc123def456");
        typeof(DockerContainersTab).GetField("_shellCommand", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "pwd");

        var method = typeof(DockerContainersTab).GetMethod("OnShellKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var args = new KeyboardEventArgs { Key = "a" };
        await (Task)method.Invoke(cut.Instance, [args])!;

        var output = (string)typeof(DockerContainersTab).GetField("_shellOutput", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Empty(output);
        // A non-Enter keypress must not dispatch the command.
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("docker/exec"));
    }

    [Fact]
    public void FilteredContainers_WithSearch_FiltersCorrectly()
    {
        var cut = RenderContainersTab();

        typeof(DockerContainersTab).GetField("_containerSearch", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "nginx");

        var prop = typeof(DockerContainersTab).GetProperty("FilteredContainers", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var filtered = (List<DockerContainerDto>)prop.GetValue(cut.Instance)!;

        Assert.Single(filtered);
        Assert.Equal("nginx", filtered[0].Name);
    }

    [Fact]
    public void FilteredImages_WithSearch_FiltersCorrectly()
    {
        var cut = RenderImagesTab();

        cut.Find("input.docker-search").Input("dotnet");

        var prop = typeof(DockerImagesTab).GetProperty("FilteredImages", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var filtered = (List<DockerImageDto>)prop.GetValue(cut.Instance)!;

        Assert.Single(filtered);
        Assert.Equal("dotnet", filtered[0].Repository);
    }

    [Fact]
    public void FilteredCompose_WithSearch_FiltersCorrectly()
    {
        var cut = RenderComposeTab();

        cut.Find("input.docker-search").Input("monitoring");

        var prop = typeof(DockerComposeTab).GetProperty("FilteredCompose", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var filtered = (List<DockerComposeStackDto>)prop.GetValue(cut.Instance)!;

        Assert.Single(filtered);
    }

    /// <summary>
    /// The filter moved into the extracted tab, so the test moved with it - and it now exercises the
    /// rendered grid rather than a private field, which is what the tab actually promises.
    /// </summary>
    [Fact]
    public void NetworksTab_WithSearch_FiltersCorrectly()
    {
        var cut = Render<DockerNetworksTab>(parameters => parameters
            .Add(p => p.Networks, new List<DockerNetworkDto>
            {
                new() { NetworkId = "n1", Name = "bridge", Driver = "bridge", Scope = "local" },
                new() { NetworkId = "n2", Name = "host", Driver = "host", Scope = "local" }
            }));

        cut.Find("input.docker-search").Input("bridge");

        var filtered = (List<DockerNetworkDto>)typeof(DockerNetworksTab)
            .GetProperty("FilteredNetworks", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.Single(filtered);
        Assert.Equal("bridge", filtered[0].Name);
    }

    [Fact]
    public void NetworksTab_ProjectGroupsCollapseTheirOwnRows()
    {
        var cut = Render<DockerNetworksTab>(parameters => parameters
            .Add(p => p.Networks, new List<DockerNetworkDto>
            {
                new() { NetworkId = "n1", Name = "bridge", Driver = "bridge", Scope = "local", Project = "alpha" },
                new() { NetworkId = "n2", Name = "host", Driver = "host", Scope = "local", Project = "beta" }
            }));

        Assert.Equal(2, cut.FindAll("tr.omni-data-grid__group").Count);
        Assert.Equal(2, cut.FindAll("tbody tr[data-omni-row-index]").Count);

        cut.Find("tr.omni-data-grid__group button[aria-expanded='true']").Click();

        Assert.Single(cut.FindAll("tbody tr[data-omni-row-index]"));
        Assert.Single(cut.FindAll("tr.omni-data-grid__group button[aria-expanded='false']"));
    }

    [Fact]
    public void VolumesTab_WithSearch_FiltersCorrectly()
    {
        var cut = Render<DockerVolumesTab>(parameters => parameters
            .Add(p => p.Volumes, new List<DockerVolumeDto>
            {
                new() { Name = "pgdata", Driver = "local", Mountpoint = "/data" },
                new() { Name = "logs", Driver = "local", Mountpoint = "/logs" }
            }));

        cut.Find("input.docker-search").Input("pgdata");

        var filtered = (List<DockerVolumeDto>)typeof(DockerVolumesTab)
            .GetProperty("FilteredVolumes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.Single(filtered);
        Assert.Equal("pgdata", filtered[0].Name);
    }

    // === HandleTaskCompleted / DispatchTaskOutput ===

    [Fact]
    public async Task HandleTaskCompleted_InspectOutput_SetsInspectContent()
    {
        var cut = RenderDockerSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        typeof(DockerContainersTab).GetField("_inspectContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(tab.Instance, "abc123def456");

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification
        {
            TaskId = 1,
            ServerId = 20,
            TaskName = "docker inspect abc123def456",
            Status = TaskExecutionStatus.Success,
            Output = "{\"Id\": \"abc123\"}"
        }));

        var content = (string?)typeof(DockerContainersTab).GetField("_inspectContent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tab.Instance);
        Assert.Equal("{\"Id\": \"abc123\"}", content);
    }

    [Fact]
    public async Task HandleTaskCompleted_ComposeFileOutput_SetsComposeContent()
    {
        // The editor state lives in the extracted tab now, so this also covers the parent's forwarding.
        var cut = RenderDockerSection();
        var composeTab = cut.FindComponent<DockerComposeTab>();
        typeof(DockerComposeTab).GetField("_composeEditorVisible", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(composeTab.Instance, true);
        typeof(DockerComposeTab).GetField("_composeFileLoading", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(composeTab.Instance, true);
        typeof(DockerComposeTab).GetField("_composeEditorStack", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(composeTab.Instance, "monitoring");

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification
        {
            TaskId = 2,
            ServerId = 20,
            TaskName = DockerTaskNames.ComposeFile("monitoring"),
            Status = TaskExecutionStatus.Success,
            Output = "version: '3'\nservices:\n  web:\n    image: nginx"
        }));

        var content = (string?)typeof(DockerComposeTab).GetField("_composeEditorContent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(composeTab.Instance);
        var loading = (bool)typeof(DockerComposeTab).GetField("_composeFileLoading", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(composeTab.Instance)!;
        Assert.Contains("nginx", content!);
        Assert.False(loading);
    }

    // FRONT-017: open stack A, then B; A's compose file arrives last. It must not land under B's title,
    // and Deploy must send B's own file for B.
    [Fact]
    public async Task HandleTaskCompleted_LateComposeFileOfPreviousStack_IsIgnoredAndSaveDeploysOwnFile()
    {
        var cut = RenderDockerSection();
        _handler.SetResponse(HttpMethod.Get, "api/servers/20/docker/compose/stack-a/file", System.Net.HttpStatusCode.Accepted);
        _handler.SetResponse(HttpMethod.Get, "api/servers/20/docker/compose/stack-b/file", System.Net.HttpStatusCode.Accepted);
        _handler.SetResponse(HttpMethod.Put, "api/servers/20/docker/compose/file", System.Net.HttpStatusCode.Accepted);
        var composeTab = cut.FindComponent<DockerComposeTab>();
        var open = typeof(DockerComposeTab).GetMethod("OpenComposeEditorAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)open.Invoke(composeTab.Instance, ["stack-a"])!);
        await cut.InvokeAsync(() => (Task)open.Invoke(composeTab.Instance, ["stack-b"])!);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification
        {
            TaskId = 1,
            ServerId = 20,
            TaskName = DockerTaskNames.ComposeFile("stack-a"),
            Status = TaskExecutionStatus.Success,
            Output = "name: stack-a-file"
        }));
        var field = (string name) => typeof(DockerComposeTab).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(composeTab.Instance);
        Assert.Equal(string.Empty, field("_composeEditorContent"));
        Assert.True((bool)field("_composeFileLoading")!);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification
        {
            TaskId = 2,
            ServerId = 20,
            TaskName = DockerTaskNames.ComposeFile("stack-b"),
            Status = TaskExecutionStatus.Success,
            Output = "name: stack-b-file"
        }));
        Assert.Equal("name: stack-b-file", field("_composeEditorContent"));

        var save = typeof(DockerComposeTab).GetMethod("SaveComposeFileAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)save.Invoke(composeTab.Instance, [])!);
        var body = _handler.RequestDetails.Last(r => r.Method == "PUT" && r.Url.Contains("docker/compose/file")).Body!;
        Assert.Contains("\"stackName\":\"stack-b\"", body);
        Assert.Contains("stack-b-file", body);
        Assert.DoesNotContain("stack-a-file", body);
    }

    [Fact]
    public async Task HandleTaskCompleted_ExecOutput_AppendsToShellOutput()
    {
        var cut = RenderDockerSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        typeof(DockerContainersTab).GetField("_shellContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(tab.Instance, "abc123def456");
        typeof(DockerContainersTab).GetField("_shellOutput", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(tab.Instance, "");

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification
        {
            TaskId = 3,
            ServerId = 20,
            TaskName = "docker exec abc123",
            Status = TaskExecutionStatus.Success,
            Output = "/app"
        }));

        var output = (string)typeof(DockerContainersTab).GetField("_shellOutput", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tab.Instance)!;
        Assert.Contains("/app", output);
    }

    [Fact]
    public async Task HandleTaskCompleted_EnvOutput_SetsEnvContent()
    {
        var cut = RenderDockerSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        typeof(DockerContainersTab).GetField("_envContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(tab.Instance, "abc123def456");

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification
        {
            TaskId = 4,
            ServerId = 20,
            TaskName = "docker env abc123",
            Status = TaskExecutionStatus.Success,
            Output = "[\"PATH=/usr/bin\",\"HOME=/root\"]"
        }));

        var content = (string?)typeof(DockerContainersTab).GetField("_envContent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tab.Instance);
        Assert.Contains("PATH", content!);
    }

    [Fact]
    public async Task HandleTaskCompleted_BrowseOutput_SetsBrowseContent()
    {
        var cut = RenderDockerSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        typeof(DockerContainersTab).GetField("_browseContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(tab.Instance, "abc123def456");

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification
        {
            TaskId = 5,
            ServerId = 20,
            TaskName = "docker ls /app",
            Status = TaskExecutionStatus.Success,
            Output = "bin  lib  src"
        }));

        var content = (string?)typeof(DockerContainersTab).GetField("_browseContent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tab.Instance);
        Assert.Equal("bin  lib  src", content);
    }

    [Fact]
    public async Task HandleTaskCompleted_NullOutput_DoesNotDispatch()
    {
        var cut = RenderDockerSection();
        var tab = cut.FindComponent<DockerContainersTab>();
        typeof(DockerContainersTab).GetField("_inspectContainerId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(tab.Instance, "abc123def456");

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification
        {
            TaskId = 6,
            ServerId = 20,
            TaskName = "docker inspect abc123",
            Status = TaskExecutionStatus.Success,
            Output = null
        }));

        var content = (string?)typeof(DockerContainersTab).GetField("_inspectContent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tab.Instance);
        Assert.Null(content);
    }

    [Fact]
    public async Task HandleTaskCompleted_UnmatchedTask_DoesNotSetAnyField()
    {
        var cut = RenderDockerSection();
        var tab = cut.FindComponent<DockerContainersTab>();

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification
        {
            TaskId = 7,
            ServerId = 20,
            TaskName = "docker stats",
            Status = TaskExecutionStatus.Success,
            Output = "some output"
        }));

        var inspect = (string?)typeof(DockerContainersTab).GetField("_inspectContent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tab.Instance);
        var env = (string?)typeof(DockerContainersTab).GetField("_envContent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tab.Instance);
        var browse = (string?)typeof(DockerContainersTab).GetField("_browseContent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tab.Instance);
        Assert.Null(inspect);
        Assert.Null(env);
        Assert.Null(browse);
    }

    // === GetProjectAccentClass ===

    [Theory]
    [InlineData("myproject")]
    [InlineData("another-project")]
    [InlineData("backend-api")]
    public void GetProjectAccentClass_NonEmptyProject_ReturnsAccentClass(string project)
    {
        var result = DockerContainersTab.GetProjectAccentClass(project);
        Assert.StartsWith("docker-project-accent-", result);
        var index = int.Parse(result["docker-project-accent-".Length..]);
        Assert.InRange(index, 0, 7);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void GetProjectAccentClass_EmptyProject_ReturnsEmpty(string? project)
    {
        Assert.Empty(DockerContainersTab.GetProjectAccentClass(project));
    }

    [Fact]
    public void GetProjectAccentClass_SameProject_ReturnsSameClass()
    {
        Assert.Equal(
            DockerContainersTab.GetProjectAccentClass("myapp"),
            DockerContainersTab.GetProjectAccentClass("myapp"));
    }

    [Fact]
    public void GetProjectAccentClass_DifferentProjects_ProduceMultipleColors()
    {
        var classes = new HashSet<string>();
        foreach (var p in new[] { "app1", "app2", "app3", "app4", "app5", "app6", "app7", "app8" })
            classes.Add(DockerContainersTab.GetProjectAccentClass(p));
        Assert.True(classes.Count > 1);
    }
}
