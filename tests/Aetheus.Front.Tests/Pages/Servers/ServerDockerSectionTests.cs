// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// The Docker section and its tabs driven through their markup: rows, row menus, buttons, inputs, the
/// public parameters of child components and the task output the section forwards. Outcomes are read
/// from the rendered panels, the dialogs and the requests the faked API received, never from a private
/// member (RFL-1).
/// </summary>
public class ServerDockerSectionTests : BunitContext
{
    public ServerDockerSectionTests()
    {
        // An administrator: the section offers its admin-only actions (prune) to this user.
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
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

    private IRenderedComponent<ServerDockerSection> RenderDockerSection(ServerDetailDto? server = null)
    {
        server ??= MakeDockerServer();
        var id = server.Id;
        _handler.SetJsonResponse($"api/servers/{id}/docker/containers", new List<DockerContainerDto>());
        _handler.SetJsonResponse($"api/servers/{id}/docker/action", "{}");
        _handler.SetJsonResponse($"api/servers/{id}/docker/containers/logs", "log output here");
        _handler.SetJsonResponse($"api/servers/{id}/docker/images", new List<DockerImageDto>());
        _handler.SetJsonResponse($"api/servers/{id}/docker/images/pull", "{}");
        _handler.SetJsonResponse($"api/servers/{id}/docker/compose/action", "{}");
        _handler.SetJsonResponse($"api/servers/{id}/docker/prune", "{}");
        _handler.SetJsonResponse($"api/servers/{id}/docker/resource-limits", "{}");
        _handler.SetJsonResponse($"api/servers/{id}/docker/containers/abc123def456/inspect", "{}");
        _handler.SetJsonResponse($"api/servers/{id}/docker/compose/monitoring/file", "{}");
        _handler.SetJsonResponse($"api/servers/{id}/docker/compose/file", "{}");
        _handler.SetJsonResponse($"api/servers/{id}/docker/exec", "{}");
        _handler.SetJsonResponse($"api/servers/{id}/docker/containers/abc123def456/env", "{}");
        _handler.SetJsonResponse($"api/servers/{id}/docker/containers/browse", "{}");
        _handler.SetJsonResponse($"api/servers/{id}/docker/build", "{}");

        return Render<ServerDockerSection>(p => p
            .Add(x => x.Server, server)
            .Add(x => x.ServerId, id)
            .Add(x => x.DockerInitialLoaded, true));
    }

    /// <summary>
    /// The images tab is a component in its own right now, so it is rendered on its own: the parent no
    /// longer holds any of the state these tests read.
    /// </summary>
    private IRenderedComponent<DockerImagesTab> RenderImagesTab(bool canAdmin = false)
    {
        _handler.SetJsonResponse("api/servers/20/docker/images/pull", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/build", "{}");

        return Render<DockerImagesTab>(p => p
            .Add(x => x.ServerId, 20)
            .Add(x => x.CanAdmin, canAdmin)
            .Add(x => x.Images, MakeDockerServer().Docker.Images));
    }

    /// <summary>
    /// The containers tab, rendered on its own. Tests that only need the tab render it directly; the
    /// ones that go through the parent's task-output entry point render the section.
    /// </summary>
    private IRenderedComponent<DockerContainersTab> RenderContainersTab(bool canAdmin = false)
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
            .Add(x => x.CanAdmin, canAdmin)
            .Add(x => x.InitialLoaded, true));
    }

    private IRenderedComponent<DockerComposeTab> RenderComposeTab(bool canAdmin = false, IReadOnlyList<DockerComposeStackDto>? stacks = null)
    {
        _handler.SetJsonResponse("api/servers/20/docker/compose/action", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/compose/monitoring/file", "{}");
        _handler.SetJsonResponse("api/servers/20/docker/compose/file", "{}");

        return Render<DockerComposeTab>(p => p
            .Add(x => x.ServerId, 20)
            .Add(x => x.CanAdmin, canAdmin)
            .Add(x => x.Stacks, stacks ?? MakeDockerServer().Docker.ComposeStacks));
    }

    private static Task ClickAsync(AngleSharp.Dom.IElement element) => element.ClickAsync(new MouseEventArgs());

    /// <summary>Opens the "More actions" menu of a stack row and returns its Edit compose entry (STD-BTN detection 11).</summary>
    private static AngleSharp.Dom.IElement EditComposeEntry(IRenderedComponent<DockerComposeTab> tab, int row = 0)
    {
        tab.FindAll(".omni-overflow-menu__trigger")[row].Click();
        return tab.Find(".omni-menu__item[title='EditCompose']");
    }

    private static AngleSharp.Dom.IElement Row<T>(IRenderedComponent<T> cut, string name) where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.FindAll("tbody tr[data-omni-row-index]")
            .Where(row => row.QuerySelector(".docker-row-actions") is not null)
            .Single(row => row.TextContent.Contains(name, StringComparison.Ordinal));

    /// <summary>Opens the "..." menu of a container row and picks one of its entries.</summary>
    private static async Task ChooseRowActionAsync<T>(IRenderedComponent<T> cut, string container, string label) where T : Microsoft.AspNetCore.Components.IComponent
    {
        await ClickAsync(Row(cut, container).QuerySelector(".omni-overflow-menu__trigger")!);
        var entry = cut.FindAll(".omni-overflow-menu__popup .omni-menu__item").Single(item => item.TextContent.Trim() == label);
        await ClickAsync(entry);
    }

    private static AngleSharp.Dom.IElement ButtonLabelled<T>(IRenderedComponent<T> cut, string label) where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.FindAll("button").First(button => button.TextContent.Trim() == label);

    /// <summary>The titles of the detail panels (inspect, shell, env, files) open under the grid.</summary>
    private static string[] PanelTitles<T>(IRenderedComponent<T> cut) where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.FindAll(".docker-panel-fadein strong").Select(title => title.TextContent.Trim()).ToArray();

    private static ImmediateDialogService Dialogs(BunitContext context) =>
        (ImmediateDialogService)context.Services.GetRequiredService<OmniDialogService>();

    // === Unchanged: pure functions, rendering, row classes ===

    [Fact]
    public void ContainerRows_KeepOneButton_AndPutTheOtherActionsInARowMenu()
    {
        // Recette R3-41/R3-42: a running container shows Restart, a stopped one Start; the rest is behind the menu.
        var cut = RenderContainersTab();
        var rows = cut.FindAll("tbody tr");
        var running = rows.Single(r => r.TextContent.Contains("nginx", StringComparison.Ordinal));
        var stopped = rows.Single(r => r.TextContent.Contains("api", StringComparison.Ordinal) && !r.TextContent.Contains("nginx", StringComparison.Ordinal));

        Assert.Equal(["Restart"], running.QuerySelectorAll(".docker-row-actions .omni-button:not(.omni-overflow-menu__trigger)").Select(b => b.GetAttribute("title")).ToArray());
        Assert.Equal(["Start"], stopped.QuerySelectorAll(".docker-row-actions .omni-button:not(.omni-overflow-menu__trigger)").Select(b => b.GetAttribute("title")).ToArray());

        running.QuerySelector(".omni-overflow-menu__trigger")!.Click();
        var runningEntries = cut.FindAll(".omni-overflow-menu__popup .omni-menu__item").Select(e => e.TextContent.Trim()).ToArray();
        Assert.Equal(["Stop", "ViewLogs", "ResourceLimits", "DockerInspect", "DockerEnvVars", "DockerFileBrowser"], runningEntries);
        Assert.Contains("danger", cut.FindAll(".omni-menu__item").First().ClassName, StringComparison.OrdinalIgnoreCase);

        cut.FindAll(".omni-menu__item").First().Click();
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r =>
            r.Method == "POST" && r.Url.Contains("docker/action", StringComparison.Ordinal)));
    }

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

    [Fact]
    public void PruneDialog_DefaultModel_SelectsAllResourceKinds()
    {
        var model = new DockerPruneDialogModel();

        Assert.True(model.Containers);
        Assert.True(model.Images);
        Assert.True(model.Volumes);
    }

    [Fact]
    public void PruneDialog_RendersAllResourceChoices()
    {
        var cut = Render<DockerPruneDialog>();

        Assert.True(cut.FindAll("input").Count >= 3);
        Assert.Contains("DockerPrune", cut.Markup);
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
        cut.Find(".docker-bulk-actions .omni-overflow-menu__trigger").Click();
        cut.Find(".omni-menu__item[title='StartAll']").Click();

        Assert.Single(_handler.Requests, request => request.Url.Contains("docker/action", StringComparison.Ordinal));
        cut.Find("input[placeholder='FilterContainers']").Input("second");
        cut.Find(".docker-bulk-actions .omni-overflow-menu__trigger").Click();
        Assert.True(cut.Find(".omni-menu__item[title='StartAll']").HasAttribute("disabled"));
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
    public async Task FailedComposeRead_StopsLoadingAndSaysWhy()
    {
        // Recette R3-34: the compose read failed on the agent; the editor stops waiting and shows why.
        var cut = RenderComposeTab();
        EditComposeEntry(cut).Click();

        await cut.InvokeAsync(() => cut.Instance.HandleTaskOutput(
            DockerTaskNames.ComposeFile("monitoring"), "no configuration file provided: not found", failed: true, taskId: 7));

        Assert.Contains("TaskFailed", cut.Markup);
        Assert.Contains("no configuration file provided: not found", cut.Markup);
        Assert.DoesNotContain("aria-busy=\"true\"", cut.Markup);
    }

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


    // === Containers tab ===

    [Fact]
    public async Task DockerActionAsync_SendsAction()
    {
        var cut = RenderContainersTab();

        await ChooseRowActionAsync(cut, "nginx", "Stop");

        // The stop of that very container is sent, the row is not left busy, and the user is told.
        var post = Assert.Single(_handler.RequestDetails, r => r.Method == "POST" && r.Url.Contains("docker/action"));
        Assert.Contains("abc123def456", post.Body);
        Assert.Contains("\"action\":1", post.Body); // Stop
        Assert.Empty(cut.FindAll(".btn-busy"));
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success && toast.Detail == "DockerActionSent");
    }

    [Fact]
    public async Task ConfirmRemoveContainerAsync_OpensDialogWithoutDeletingWhenCancelled()
    {
        var cut = RenderContainersTab();
        var dialog = Dialogs(this);

        await ChooseRowActionAsync(cut, "api", "Delete");

        Assert.Equal(1, dialog.OpenCount);
        Assert.Equal("RemoveContainerConfirm", dialog.LastConfirmMessage);
        Assert.Equal("RemoveContainer", dialog.LastTitle);
        Assert.DoesNotContain(_handler.Requests, request => request.Method == "POST" && request.Url.Contains("docker/action"));
    }

    [Fact]
    public async Task ConfirmRemoveContainerAsync_ConfirmedSendsRemoveAction()
    {
        var cut = RenderContainersTab();
        Dialogs(this).ConfirmResult = true;

        await ChooseRowActionAsync(cut, "api", "Delete");

        var post = Assert.Single(_handler.RequestDetails, r => r.Method == "POST" && r.Url.Contains("docker/action"));
        Assert.Contains("def789ghi012", post.Body);
        Assert.Contains("\"action\":3", post.Body); // Remove
    }

    [Fact]
    public async Task ToggleLogsAsync_ToglesLogState()
    {
        var cut = RenderContainersTab();

        // First pick - the logs panel opens with the container's logs.
        await ChooseRowActionAsync(cut, "nginx", "ViewLogs");
        cut.WaitForAssertion(() => Assert.Contains("log output here", cut.Find(".docker-logs").TextContent));
        Assert.Contains("DockerLogs - abc123def456", cut.Markup);
        Assert.Contains(_handler.RequestDetails, r => r.Url.Contains("docker/containers/logs") && r.Body!.Contains("abc123def456"));

        // Second pick - the panel closes.
        await ChooseRowActionAsync(cut, "nginx", "ViewLogs");
        Assert.Empty(cut.FindAll(".docker-logs"));
    }

    [Fact]
    public async Task OpenResourceLimitsDialog_PrefillsContainerLimits()
    {
        var cut = RenderContainersTab();
        var dialog = Dialogs(this);

        await ChooseRowActionAsync(cut, "nginx", "ResourceLimits");

        Assert.Equal("abc123def456", dialog.LastParameters!["ContainerId"]);
        Assert.Equal(256, dialog.LastParameters["MemoryLimitMb"]);
    }

    [Fact]
    public async Task ApplyResourceLimitsAsync_SendsRequest()
    {
        var cut = RenderContainersTab();
        Dialogs(this).OpenResult = new DockerResourceLimitsDialogResult(1.5, 512);

        await ChooseRowActionAsync(cut, "nginx", "ResourceLimits");

        var post = Assert.Single(_handler.RequestDetails, r => r.Url.Contains("docker/resource-limits"));
        Assert.Contains("abc123def456", post.Body);
        Assert.Contains("512", post.Body);
    }

    [Fact]
    public async Task RequestInspectAsync_TogglesInspect()
    {
        var cut = RenderContainersTab();

        // First pick - the inspect panel opens, waiting for the agent's answer.
        await ChooseRowActionAsync(cut, "nginx", "DockerInspect");
        Assert.Equal(["DockerInspect - abc123def456"], PanelTitles(cut));
        Assert.Contains("WaitingResult", cut.Markup);
        Assert.Contains(_handler.Requests, r => r.Url.EndsWith("abc123def456/inspect", StringComparison.Ordinal));

        // Second pick - the panel closes.
        await ChooseRowActionAsync(cut, "nginx", "DockerInspect");
        Assert.Empty(PanelTitles(cut));
    }

    [Fact]
    public async Task OpenShell_TogglesShellState()
    {
        var cut = RenderContainersTab(canAdmin: true);

        // Open
        await ChooseRowActionAsync(cut, "nginx", "DockerShell");
        Assert.Equal(["DockerShell - nginx"], PanelTitles(cut));
        Assert.NotEmpty(cut.FindAll(".docker-shell-output"));

        // Close (same container)
        await ChooseRowActionAsync(cut, "nginx", "DockerShell");
        Assert.Empty(PanelTitles(cut));
        Assert.Empty(cut.FindAll(".docker-shell-output"));
    }

    [Fact]
    public async Task OpenShell_IsNotOffered_WithoutTheAdminRight()
    {
        var cut = RenderContainersTab(canAdmin: false);

        await ClickAsync(Row(cut, "nginx").QuerySelector(".omni-overflow-menu__trigger")!);

        Assert.DoesNotContain(cut.FindAll(".omni-overflow-menu__popup .omni-menu__item"), item => item.TextContent.Trim() == "DockerShell");
    }

    [Fact]
    public async Task SendShellCommandAsync_WithEmptyCommand_ProducesNothing()
    {
        var cut = RenderContainersTab(canAdmin: true);
        await ChooseRowActionAsync(cut, "nginx", "DockerShell");

        // Nothing typed: Send is disabled, clicking it does nothing and the output stays empty.
        var send = cut.Find("button[title=Send]");
        Assert.True(send.HasAttribute("disabled"));
        await ClickAsync(send);

        Assert.DoesNotContain("$", cut.Find(".docker-shell-output").TextContent);
        // An empty command must not reach the agent.
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("docker/exec"));
    }

    [Fact]
    public async Task SendShellCommandAsync_WithCommand_SendsRequest()
    {
        var cut = RenderContainersTab(canAdmin: true);
        await ChooseRowActionAsync(cut, "nginx", "DockerShell");

        cut.Find("input[aria-label=EnterCommand]").Input("ls -la");
        await ClickAsync(cut.Find("button[title=Send]"));

        Assert.Contains("$ ls -la", cut.Find(".docker-shell-output").TextContent);
        // The command is dispatched to the agent via a POST to the docker/exec endpoint.
        var post = Assert.Single(_handler.RequestDetails, r => r.Method == "POST" && r.Url.Contains("api/servers/20/docker/exec"));
        Assert.Contains("ls -la", post.Body);
        Assert.Equal(string.Empty, cut.Find("input[aria-label=EnterCommand]").GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public async Task OnShellKeyDown_Enter_SendsCommand()
    {
        var cut = RenderContainersTab(canAdmin: true);
        await ChooseRowActionAsync(cut, "nginx", "DockerShell");
        var input = cut.Find("input[aria-label=EnterCommand]");
        input.Input("pwd");

        await input.KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        Assert.Contains("$ pwd", cut.Find(".docker-shell-output").TextContent);
        // Enter dispatches the buffered command to the agent (POST docker/exec).
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/20/docker/exec"));
    }

    [Fact]
    public async Task OnShellKeyDown_NonEnter_DoesNotSend()
    {
        var cut = RenderContainersTab(canAdmin: true);
        await ChooseRowActionAsync(cut, "nginx", "DockerShell");
        var input = cut.Find("input[aria-label=EnterCommand]");
        input.Input("pwd");

        await input.KeyDownAsync(new KeyboardEventArgs { Key = "a" });

        Assert.DoesNotContain("$", cut.Find(".docker-shell-output").TextContent);
        // A non-Enter keypress must not dispatch the command.
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("docker/exec"));
    }

    [Fact]
    public async Task RequestEnvVarsAsync_TogglesEnvVars()
    {
        var cut = RenderContainersTab();

        // Open
        await ChooseRowActionAsync(cut, "nginx", "DockerEnvVars");
        Assert.Equal(["DockerEnvVars - nginx"], PanelTitles(cut));
        Assert.Contains(_handler.Requests, r => r.Url.EndsWith("abc123def456/env", StringComparison.Ordinal));

        // Close
        await ChooseRowActionAsync(cut, "nginx", "DockerEnvVars");
        Assert.Empty(PanelTitles(cut));
    }

    [Fact]
    public async Task OpenFileBrowserAsync_TogglesFileBrowser()
    {
        var cut = RenderContainersTab();

        // Open: the browser lists the container's root.
        await ChooseRowActionAsync(cut, "nginx", "DockerFileBrowser");
        Assert.Equal(["DockerFileBrowser - nginx"], PanelTitles(cut));
        var post = Assert.Single(_handler.RequestDetails, r => r.Url.Contains("docker/containers/browse"));
        Assert.Contains("abc123def456", post.Body);

        // Close
        await ChooseRowActionAsync(cut, "nginx", "DockerFileBrowser");
        Assert.Empty(PanelTitles(cut));
    }

    [Fact]
    public async Task FileBrowser_AtRoot_OffersNoMoveUpButton()
    {
        var cut = RenderContainersTab();
        await ChooseRowActionAsync(cut, "nginx", "DockerFileBrowser");

        // The browser only ever lists the container root: no way up is offered.
        Assert.Empty(cut.FindAll(".docker-panel-fadein button[title=MoveUp]"));
        Assert.Contains("/", cut.Find(".docker-panel-fadein").TextContent);
    }

    [Fact]
    public void FilteredContainers_WithSearch_FiltersCorrectly()
    {
        var cut = RenderContainersTab();
        Assert.Equal(2, cut.FindAll("tbody tr[data-omni-row-index]").Count);

        cut.Find("input[aria-label=FilterContainers]").Input("nginx");

        var rows = cut.FindAll("tbody tr[data-omni-row-index]");
        Assert.Single(rows);
        Assert.Contains("nginx", rows[0].TextContent);
    }

    // === Images tab ===

    [Fact]
    public async Task PullImageAsync_WithEmptyName_MakesNoRequest()
    {
        var cut = RenderImagesTab();

        // No name typed: Pull is disabled and clicking it sends nothing.
        var pull = ButtonLabelled(cut, "DockerPull");
        Assert.True(pull.HasAttribute("disabled"));
        await ClickAsync(pull);

        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("docker/images/pull"));
    }

    [Fact]
    public async Task PullImageAsync_WithName_SendsRequest()
    {
        var cut = RenderImagesTab();

        cut.Find("input[aria-label=ImageNamePlaceholder]").Input("nginx:latest");
        await ClickAsync(ButtonLabelled(cut, "DockerPull"));

        var post = Assert.Single(_handler.RequestDetails, r => r.Url.Contains("docker/images/pull"));
        Assert.Contains("nginx:latest", post.Body);
        // The box is emptied for the next image.
        Assert.Equal(string.Empty, cut.Find("input[aria-label=ImageNamePlaceholder]").GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public async Task RemoveImageAsync_SendsRequest()
    {
        _handler.SetJsonResponse("api/servers/20/docker/images/i1", "{}");
        var cut = RenderImagesTab(canAdmin: true);
        var dialog = Dialogs(this);
        dialog.ConfirmResult = true;

        await ClickAsync(cut.FindAll("tbody tr[data-omni-row-index]").First(row => row.TextContent.Contains("nginx"))
            .QuerySelector("button[title=Delete]")!);

        // The user is asked first, naming the image; confirmed, a DELETE reaches the image endpoint.
        Assert.Equal("DockerRemoveImageConfirm", dialog.LastConfirmMessage);
        Assert.Contains(_handler.Requests, r => r.Method == "DELETE" && r.Url.Contains("api/servers/20/docker/images/i1"));
    }

    [Fact]
    public async Task RemoveImageAsync_WhenTheUserDeclines_SendsNothing()
    {
        var cut = RenderImagesTab(canAdmin: true);
        Dialogs(this).ConfirmResult = false;

        await ClickAsync(cut.FindAll("tbody tr[data-omni-row-index]").First(row => row.TextContent.Contains("nginx"))
            .QuerySelector("button[title=Delete]")!);

        Assert.DoesNotContain(_handler.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task BuildImageAsync_WithEmptyTag_MakesNoRequest()
    {
        var cut = RenderImagesTab(canAdmin: true);

        // No tag and no Dockerfile: Build is disabled and clicking it posts nothing.
        var build = ButtonLabelled(cut, "DockerBuild");
        Assert.True(build.HasAttribute("disabled"));
        await ClickAsync(build);

        // Empty tag short-circuits before the build request - nothing is posted to docker/build.
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("docker/build"));
    }

    [Fact]
    public async Task BuildImageAsync_WithContent_SendsRequest()
    {
        var cut = RenderImagesTab(canAdmin: true);

        cut.Find("#docker-build-tag").Input("myapp:latest");
        var editor = cut.FindComponent<OmniCodeEditor>();
        await cut.InvokeAsync(() => editor.Instance.ValueChanged.InvokeAsync("FROM alpine\nRUN echo hello"));
        await ClickAsync(ButtonLabelled(cut, "DockerBuild"));

        var post = Assert.Single(_handler.RequestDetails, r => r.Url.Contains("docker/build"));
        Assert.Contains("myapp:latest", post.Body);
        Assert.Contains("FROM alpine", post.Body);
        // Both inputs are emptied once the build is queued.
        Assert.Equal(string.Empty, cut.Find("#docker-build-tag").GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public void FilteredImages_WithSearch_FiltersCorrectly()
    {
        var cut = RenderImagesTab();
        Assert.Equal(2, cut.FindAll("tbody tr[data-omni-row-index]").Count);

        cut.Find("input.docker-search").Input("dotnet");

        var rows = cut.FindAll("tbody tr[data-omni-row-index]");
        Assert.Single(rows);
        Assert.Contains("dotnet", rows[0].TextContent);
    }

    // === Compose tab ===

    [Fact]
    public async Task ComposeActionAsync_SendsRequest()
    {
        var cut = RenderComposeTab();

        await ClickAsync(cut.Find("button[title=Start]"));

        // The Start of that stack is POSTed to the compose/action endpoint.
        var post = Assert.Single(_handler.RequestDetails, r => r.Method == "POST" && r.Url.Contains("api/servers/20/docker/compose/action"));
        Assert.Contains("monitoring", post.Body);
        Assert.Contains("\"action\":0", post.Body); // Up
    }

    [Fact]
    public async Task OpenComposeEditorAsync_SetsFields()
    {
        var cut = RenderComposeTab();

        await ClickAsync(EditComposeEntry(cut));

        // The editor opens under the stack's name and asks the agent for its compose file.
        Assert.Contains("DockerCompose - monitoring", cut.Markup);
        Assert.Single(cut.FindAll("div[aria-busy=true]"));
        Assert.Contains(_handler.Requests, r => r.Url.EndsWith("docker/compose/monitoring/file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CloseComposeEditor_ResetsFields()
    {
        var cut = RenderComposeTab();
        await ClickAsync(EditComposeEntry(cut));
        Assert.Contains("DockerCompose - monitoring", cut.Markup);

        await ClickAsync(cut.Find("button[title=Close]"));

        Assert.DoesNotContain("DockerCompose - monitoring", cut.Markup);
        Assert.Empty(cut.FindComponents<OmniCodeEditor>());
    }

    [Fact]
    public async Task SaveComposeFileAsync_WithEmptyContent_MakesNoRequest()
    {
        var cut = RenderComposeTab(canAdmin: true);
        await ClickAsync(EditComposeEntry(cut));
        await cut.InvokeAsync(() => cut.Instance.HandleTaskOutput(DockerTaskNames.ComposeFile("monitoring"), string.Empty));
        var requestsBefore = _handler.Requests.Count;

        // The file came back empty: Deploy is disabled and clicking it sends nothing.
        var deploy = ButtonLabelled(cut, "Deploy");
        Assert.True(deploy.HasAttribute("disabled"));
        await ClickAsync(deploy);

        // Empty content short-circuits before any HTTP: no compose-file write is emitted.
        Assert.DoesNotContain(_handler.Requests.Skip(requestsBefore), r => r.Method != "GET");
    }

    [Fact]
    public async Task SaveComposeFileAsync_WithContent_SendsRequest()
    {
        var cut = RenderComposeTab(canAdmin: true);
        await ClickAsync(EditComposeEntry(cut));
        await cut.InvokeAsync(() => cut.Instance.HandleTaskOutput(DockerTaskNames.ComposeFile("monitoring"), "version: '3'"));

        await ClickAsync(ButtonLabelled(cut, "Deploy"));

        var write = Assert.Single(_handler.RequestDetails, r => r.Method != "GET" && r.Url.Contains("docker/compose") && r.Url.Contains("/file"));
        Assert.Contains("monitoring", write.Body);
        Assert.Contains("version:", write.Body); // the apostrophes travel JSON-escaped
        // Once the deployment is queued the editor closes.
        Assert.DoesNotContain("DockerCompose - monitoring", cut.Markup);
    }

    [Fact]
    public void FilteredCompose_WithSearch_FiltersCorrectly()
    {
        var cut = RenderComposeTab(stacks:
        [
            new DockerComposeStackDto { Name = "monitoring", Status = "running(3)", RunningCount = 3, TotalCount = 3 },
            new DockerComposeStackDto { Name = "web", Status = "exited(1)", RunningCount = 0, TotalCount = 1 }
        ]);
        Assert.Equal(2, cut.FindAll("tbody tr[data-omni-row-index]").Count);

        cut.Find("input.docker-search").Input("monitoring");

        var rows = cut.FindAll("tbody tr[data-omni-row-index]");
        Assert.Single(rows);
        Assert.Contains("monitoring", rows[0].TextContent);
    }

    // === Networks and volumes tabs ===

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
        Assert.Equal(2, cut.FindAll("tbody tr[data-omni-row-index]").Count);

        cut.Find("input.docker-search").Input("bridge");

        var rows = cut.FindAll("tbody tr[data-omni-row-index]");
        Assert.Single(rows);
        Assert.Contains("bridge", rows[0].TextContent);
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
        Assert.Equal(2, cut.FindAll("tbody tr[data-omni-row-index]").Count);

        cut.Find("input.docker-search").Input("pgdata");

        var rows = cut.FindAll("tbody tr[data-omni-row-index]");
        Assert.Single(rows);
        Assert.Contains("pgdata", rows[0].TextContent);
    }


    // === Section: prune, server change, forwarded task output ===

    [Fact]
    public async Task ExecutePruneAsync_SendsRequest()
    {
        var cut = RenderDockerSection();
        Dialogs(this).OpenResult = new DockerPruneDialogResult(true, true, false);

        await ClickAsync(ButtonLabelled(cut, "DockerPrune"));

        var post = Assert.Single(_handler.RequestDetails, r => r.Url.Contains("docker/prune"));
        Assert.Contains("\"containers\":true", post.Body);
        Assert.Contains("\"images\":true", post.Body);
        Assert.Contains("\"volumes\":false", post.Body);
    }

    [Fact]
    public async Task PruneAsync_WithFlags_SendsRequest()
    {
        var cut = RenderDockerSection();
        Dialogs(this).OpenResult = new DockerPruneDialogResult(true, false, true);

        await ClickAsync(ButtonLabelled(cut, "DockerPrune"));

        var post = Assert.Single(_handler.RequestDetails, r => r.Url.Contains("docker/prune"));
        Assert.Contains("\"containers\":true", post.Body);
        Assert.Contains("\"images\":false", post.Body);
        Assert.Contains("\"volumes\":true", post.Body);
        Assert.Empty(cut.FindAll(".docker-toolbar .btn-busy"));
    }

    [Fact]
    public async Task ServerChange_ResetsTransientStateAndIgnoresOldNotifications()
    {
        var cut = RenderDockerSection();
        await ChooseRowActionAsync(cut, "nginx", "DockerShell");
        await ChooseRowActionAsync(cut, "nginx", "DockerInspect");
        Assert.Equal(2, PanelTitles(cut).Length);

        _handler.SetJsonResponse("api/servers/21/docker/containers/abc123def456/inspect", "{}");
        var nextServer = MakeDockerServer(21, "next-server");
        cut.Render(parameters => parameters
            .Add(component => component.Server, nextServer)
            .Add(component => component.ServerId, 21)
            .Add(component => component.DockerInitialLoaded, true));

        // @key="ServerId" is what resets the tab: the old instance is discarded rather than cleaned up,
        // so the shell and inspect panels opened above are gone from whatever now occupies the slot.
        Assert.Empty(PanelTitles(cut));

        await ChooseRowActionAsync(cut, "nginx", "DockerInspect");
        Assert.Contains("WaitingResult", cut.Markup);
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 1,
            ServerId = 20,
            TaskName = "docker inspect",
            Status = TaskExecutionStatus.Success,
            Output = "old-server-output"
        })));
        // The output dispatch was queued on the renderer by the handler above: this item runs after it.
        await cut.InvokeAsync(() => { });

        // The old server's answer is ignored; the new server's own answer is shown.
        Assert.DoesNotContain("old-server-output", cut.Markup);
        Assert.Contains("WaitingResult", cut.Markup);
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 2,
            ServerId = 21,
            TaskName = "docker inspect",
            Status = TaskExecutionStatus.Success,
            Output = "new-server-output"
        })));
        cut.WaitForAssertion(() => Assert.Contains("new-server-output", cut.Markup));
    }

    [Fact]
    public async Task HandleTaskCompleted_InspectOutput_SetsInspectContent()
    {
        var cut = RenderDockerSection();
        await ChooseRowActionAsync(cut, "nginx", "DockerInspect");

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 1,
            ServerId = 20,
            TaskName = "docker inspect abc123def456",
            Status = TaskExecutionStatus.Success,
            Output = "{\"Id\": \"abc123\"}"
        })));

        cut.WaitForAssertion(() => Assert.Contains("\"Id\": \"abc123\"", cut.Find(".docker-panel-fadein .docker-logs").TextContent));
    }

    [Fact]
    public async Task HandleTaskCompleted_ComposeFileOutput_SetsComposeContent()
    {
        // The editor state lives in the extracted tab now, so this also covers the parent's forwarding.
        var cut = RenderDockerSection();
        var composeTab = cut.FindComponent<DockerComposeTab>();
        await ClickAsync(EditComposeEntry(composeTab));
        Assert.Single(composeTab.FindAll("div[aria-busy=true]"));

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 2,
            ServerId = 20,
            TaskName = DockerTaskNames.ComposeFile("monitoring"),
            Status = TaskExecutionStatus.Success,
            Output = "version: '3'\nservices:\n  web:\n    image: nginx"
        })));

        cut.WaitForAssertion(() => Assert.Contains("nginx", composeTab.FindComponent<OmniCodeEditor>().Instance.Value));
        Assert.Empty(composeTab.FindAll("div[aria-busy=true]"));
    }

    // FRONT-017: open stack A, then B; A's compose file arrives last. It must not land under B's title,
    // and Deploy must send B's own file for B.
    [Fact]
    public async Task HandleTaskCompleted_LateComposeFileOfPreviousStack_IsIgnoredAndSaveDeploysOwnFile()
    {
        var server = MakeDockerServer() with
        {
            Docker = MakeDockerServer().Docker with
            {
                ComposeStacks =
                [
                    new DockerComposeStackDto { Name = "stack-a", Status = "running(1)", RunningCount = 1, TotalCount = 1 },
                    new DockerComposeStackDto { Name = "stack-b", Status = "running(1)", RunningCount = 1, TotalCount = 1 }
                ]
            }
        };
        var cut = RenderDockerSection(server);
        _handler.SetResponse(HttpMethod.Get, "api/servers/20/docker/compose/stack-a/file", System.Net.HttpStatusCode.Accepted);
        _handler.SetResponse(HttpMethod.Get, "api/servers/20/docker/compose/stack-b/file", System.Net.HttpStatusCode.Accepted);
        _handler.SetResponse(HttpMethod.Put, "api/servers/20/docker/compose/file", System.Net.HttpStatusCode.Accepted);
        var composeTab = cut.FindComponent<DockerComposeTab>();
        await ClickAsync(EditComposeEntry(composeTab, row: 0));
        await ClickAsync(EditComposeEntry(composeTab, row: 1));

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 1,
            ServerId = 20,
            TaskName = DockerTaskNames.ComposeFile("stack-a"),
            Status = TaskExecutionStatus.Success,
            Output = "name: stack-a-file"
        })));
        // The output dispatch was queued on the renderer by the handler above: this item runs after it.
        await cut.InvokeAsync(() => { });
        // A's late answer neither fills B's editor nor ends its loading.
        Assert.Equal(string.Empty, composeTab.FindComponent<OmniCodeEditor>().Instance.Value);
        Assert.Single(composeTab.FindAll("div[aria-busy=true]"));
        Assert.Contains("DockerCompose - stack-b", composeTab.Markup);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 2,
            ServerId = 20,
            TaskName = DockerTaskNames.ComposeFile("stack-b"),
            Status = TaskExecutionStatus.Success,
            Output = "name: stack-b-file"
        })));
        cut.WaitForAssertion(() => Assert.Equal("name: stack-b-file", composeTab.FindComponent<OmniCodeEditor>().Instance.Value));

        await ClickAsync(composeTab.FindAll("button").First(button => button.TextContent.Trim() == "Deploy"));
        var body = _handler.RequestDetails.Last(r => r.Method == "PUT" && r.Url.Contains("docker/compose/file")).Body!;
        Assert.Contains("\"stackName\":\"stack-b\"", body);
        Assert.Contains("stack-b-file", body);
        Assert.DoesNotContain("stack-a-file", body);
    }

    [Fact]
    public async Task HandleTaskCompleted_ExecOutput_AppendsToShellOutput()
    {
        var cut = RenderDockerSection();
        await ChooseRowActionAsync(cut, "nginx", "DockerShell");

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 3,
            ServerId = 20,
            TaskName = "docker exec abc123",
            Status = TaskExecutionStatus.Success,
            Output = "/app"
        })));

        cut.WaitForAssertion(() => Assert.Contains("/app", cut.Find(".docker-shell-output").TextContent));
    }

    [Fact]
    public async Task HandleTaskCompleted_EnvOutput_SetsEnvContent()
    {
        var cut = RenderDockerSection();
        await ChooseRowActionAsync(cut, "nginx", "DockerEnvVars");

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 4,
            ServerId = 20,
            TaskName = "docker env abc123",
            Status = TaskExecutionStatus.Success,
            Output = "[\"PATH=/usr/bin\",\"HOME=/root\"]"
        })));

        // The variables are listed in the panel's grid, one row each.
        cut.WaitForAssertion(() =>
        {
            var panel = cut.Find(".docker-panel-fadein");
            Assert.Contains("PATH", panel.TextContent);
            Assert.Contains("/usr/bin", panel.TextContent);
            Assert.Contains("HOME", panel.TextContent);
        });
    }

    [Fact]
    public async Task HandleTaskCompleted_BrowseOutput_SetsBrowseContent()
    {
        var cut = RenderDockerSection();
        await ChooseRowActionAsync(cut, "nginx", "DockerFileBrowser");

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 5,
            ServerId = 20,
            TaskName = "docker ls /app",
            Status = TaskExecutionStatus.Success,
            Output = "bin  lib  src"
        })));

        cut.WaitForAssertion(() => Assert.Contains("bin  lib  src", cut.Find(".docker-panel-fadein .docker-logs").TextContent));
    }

    [Fact]
    public async Task HandleTaskCompleted_NullOutput_DoesNotDispatch()
    {
        var cut = RenderDockerSection();
        await ChooseRowActionAsync(cut, "nginx", "DockerInspect");

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 6,
            ServerId = 20,
            TaskName = "docker inspect abc123",
            Status = TaskExecutionStatus.Success,
            Output = null
        })));
        // The output dispatch was queued on the renderer by the handler above: this item runs after it.
        await cut.InvokeAsync(() => { });

        // A task that wrote nothing leaves the panel waiting.
        Assert.Contains("WaitingResult", cut.Markup);
        Assert.Empty(cut.FindAll(".docker-panel-fadein .docker-logs"));
    }

    [Fact]
    public async Task HandleTaskCompleted_UnmatchedTask_DoesNotSetAnyField()
    {
        var cut = RenderDockerSection();
        await ChooseRowActionAsync(cut, "nginx", "DockerInspect");
        await ChooseRowActionAsync(cut, "nginx", "DockerEnvVars");
        await ChooseRowActionAsync(cut, "nginx", "DockerFileBrowser");
        Assert.Equal(3, PanelTitles(cut).Length);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 7,
            ServerId = 20,
            TaskName = "docker stats",
            Status = TaskExecutionStatus.Success,
            Output = "some output"
        })));
        // The output dispatch was queued on the renderer by the handler above: this item runs after it.
        await cut.InvokeAsync(() => { });

        // None of the three panels takes an output that is not theirs.
        Assert.DoesNotContain("some output", cut.Markup);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(cut.Markup, "WaitingResult").Count);
    }

}
