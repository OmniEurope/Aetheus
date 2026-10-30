// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerApacheSectionMethodTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerApacheSectionMethodTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static ServerDetailDto MakeApacheServer() => new()
    {
        Id = 30,
        Name = "apache-srv",
        Hostname = "10.0.0.3",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        CpuPercent = 10,
        MemoryUsedMb = 2048,
        MemoryTotalMb = 8192,
        DiskUsedGb = 20,
        DiskTotalGb = 100,
        Tags = [],
        Services = [],
        Docker = new DockerDataDto { Containers = [], Images = [], ComposeStacks = [], Networks = [], Volumes = [] },
        Apache = new ApacheDataDto
        {
            Modules =
            [
                new ApacheModuleDto { Name = "mod_ssl", IsEnabled = true },
                new ApacheModuleDto { Name = "mod_rewrite", IsEnabled = true },
                new ApacheModuleDto { Name = "mod_proxy", IsEnabled = false }
            ],
            VirtualHosts =
            [
                new ApacheVirtualHostDto { ServerName = "example.com", Port = 80, IsEnabled = true, DocumentRoot = "/var/www/html", ConfigFile = "example.conf" },
                new ApacheVirtualHostDto { ServerName = "example.com", Port = 443, IsEnabled = true, DocumentRoot = "/var/www/html", ConfigFile = "example-ssl.conf" },
                new ApacheVirtualHostDto { ServerName = "staging.example.com", Port = 80, IsEnabled = false, DocumentRoot = "/var/www/staging", ConfigFile = "staging.conf" }
            ],
        }
    };

    private IRenderedComponent<ServerApacheSection> RenderSection()
    {
        _handler.SetJsonResponse("api/servers/30/apache/action", "{}");
        _handler.SetJsonResponse("api/servers/30/apache/logs", "log content");
        _handler.SetJsonResponse("api/servers/30/apache/config", "config content");
        return Render<ServerApacheSection>(p => p
            .Add(x => x.Server, MakeApacheServer())
            .Add(x => x.ServerId, 30));
    }

    // === GroupBy (internal static via EnabledVirtualHostGroups) ===

    [Fact]
    public void EnabledVirtualHostGroups_GroupsByServerName()
    {
        var cut = RenderSection();
        var groups = cut.Instance.EnabledVirtualHostGroups;

        // example.com has :80 and :443 → 1 group with 2 members
        Assert.Single(groups);
        Assert.Equal("example.com", groups[0].ServerName);
        Assert.Equal(2, groups[0].Members.Count);
        Assert.Equal(80, groups[0].Members[0].Port); // ordered by port asc
        Assert.Equal(443, groups[0].Members[1].Port);
    }

    [Fact]
    public void DisabledVirtualHostGroups_ReturnsDisabledHosts()
    {
        var cut = RenderSection();
        var groups = cut.Instance.DisabledVirtualHostGroups;

        Assert.Single(groups);
        Assert.Equal("staging.example.com", groups[0].ServerName);
    }

    [Fact]
    public void ApacheVirtualHostGroup_Primary_ReturnsLowestPortMember()
    {
        var cut = RenderSection();
        var group = cut.Instance.EnabledVirtualHostGroups[0];
        Assert.Equal(80, group.Primary.Port);
    }

    [Fact]
    public void ApacheVirtualHostGroup_DocumentRootsCompact_DeduplicatesRoots()
    {
        var cut = RenderSection();
        var group = cut.Instance.EnabledVirtualHostGroups[0];
        // Both :80 and :443 share /var/www/html
        Assert.Equal("/var/www/html", group.DocumentRootsCompact);
    }

    // === Modules ===

    [Fact]
    public void Modules_AreAllGivenToTheGrid_WhichFiltersThemByColumn()
    {
        var cut = RenderSection();
        var prop = typeof(ServerApacheSection).GetProperty("Apache", Priv)!;
        var modules = ((ApacheDataDto)prop.GetValue(cut.Instance)!).Modules;
        Assert.Equal(3, modules.Count);
    }

    // === ExecuteActionAsync ===

    [Fact]
    public async Task ExecuteActionAsync_SetsAndClearsRunningFlag()
    {
        var cut = RenderSection();
        var method = typeof(ServerApacheSection).GetMethod("ExecuteActionAsync", Priv)!;
        await (Task)method.Invoke(cut.Instance, [ApacheAction.Restart, null])!;

        var running = (bool)typeof(ServerApacheSection).GetField("_actionRunning", Priv)!.GetValue(cut.Instance)!;
        Assert.False(running);
    }

    [Fact]
    public async Task ExecuteActionAsync_WithTarget_Completes()
    {
        var cut = RenderSection();
        var method = typeof(ServerApacheSection).GetMethod("ExecuteActionAsync", Priv)!;
        await (Task)method.Invoke(cut.Instance, [ApacheAction.EnableSite, "example.com"])!;

        // The targeted action is POSTed to the server's apache/action endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/30/apache/action"));
    }

    // === HandleTaskCompleted ===

    [Fact]
    public async Task HandleTaskCompleted_ApacheLogs_SetsLogContent()
    {
        var cut = RenderSection();
        typeof(ServerApacheSection).GetField("_logLoading", Priv)!.SetValue(cut.Instance, true);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 1,
            ServerId = 30,
            TaskName = "Apache logs error",
            Status = TaskExecutionStatus.Success,
            Output = "[error] seg fault"
        })));

        var content = (string?)typeof(ServerApacheSection).GetField("_logContent", Priv)!.GetValue(cut.Instance);
        var loading = (bool)typeof(ServerApacheSection).GetField("_logLoading", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal("[error] seg fault", content);
        Assert.False(loading);
    }

    [Fact]
    public async Task HandleTaskCompleted_ApacheConfig_SetsConfigContent()
    {
        var cut = RenderSection();
        // Config text routes into the shared model held by the section while a dialog is open.
        var model = new ApacheConfigEditorModel { SiteName = "example.com", Loading = true };
        typeof(ServerApacheSection).GetField("_configEditor", Priv)!.SetValue(cut.Instance, model);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 2,
            ServerId = 30,
            TaskName = "Apache config read",
            Status = TaskExecutionStatus.Success,
            Output = "ServerName example.com"
        })));

        Assert.Contains("example.com", model.Content);
        Assert.False(model.Loading);
    }

    [Fact]
    public async Task HandleTaskCompleted_FailedConfigRead_ShowsWhyInsteadOfAnEmptyEditor()
    {
        // Recette R3-30: a read that failed says so, with its output, and leaves no empty file to save.
        var cut = RenderSection();
        var model = new ApacheConfigEditorModel { SiteName = "example.com", Loading = true };
        typeof(ServerApacheSection).GetField("_configEditor", Priv)!.SetValue(cut.Instance, model);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 3,
            ServerId = 30,
            TaskName = "Apache config read",
            Status = TaskExecutionStatus.Failed,
            Output = "Could not read /etc/apache2/sites-available/example.com: Permission denied"
        })));

        Assert.Equal(string.Empty, model.Content);
        Assert.Contains("Permission denied", model.Error);
        Assert.False(model.Loading);
    }

    [Fact]
    public async Task HandleTaskCompleted_ConfigSave_DoesNotSetConfigContent()
    {
        var cut = RenderSection();
        var model = new ApacheConfigEditorModel { SiteName = "example.com", Content = "original" };
        typeof(ServerApacheSection).GetField("_configEditor", Priv)!.SetValue(cut.Instance, model);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 3,
            ServerId = 30,
            TaskName = "Apache config save",
            Status = TaskExecutionStatus.Success,
            Output = "saved"
        })));

        // "save" tasks are filtered out - the model content must remain untouched.
        Assert.Equal("original", model.Content);
    }

    // === FetchLogsAsync ===

    [Fact]
    public async Task FetchLogsAsync_SetsAndClearsLoadingFlag()
    {
        var cut = RenderSection();
        var method = typeof(ServerApacheSection).GetMethod("FetchLogsAsync", Priv)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        var loading = (bool)typeof(ServerApacheSection).GetField("_logLoading", Priv)!.GetValue(cut.Instance)!;
        Assert.False(loading);
    }

    // === OpenConfigEditorAsync ===

    // Hardened rule #3: OpenConfigEditorAsync opens an ApacheConfigEditorDialog via OmniDialogService.
    // Assert ONLY that OnOpen fires, then close immediately (never await OpenAsync first).
    [Fact]
    public async Task OpenConfigEditorAsync_OpensDialog()
    {
        var cut = RenderSection();
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var opened = false;
        dialog.OnOpen += (_, _, _, _) => opened = true;

        var method = typeof(ServerApacheSection).GetMethod("OpenConfigEditorAsync", Priv)!;
        var task = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, ["example.com"])!);
        await cut.InvokeAsync(() => dialog.Close(null));
        await task;

        Assert.True(opened);
    }

    // === SaveConfigAsync ===

    [Fact]
    public async Task SaveConfigAsync_CompletesWithoutThrowing()
    {
        var cut = RenderSection();
        var request = new ApacheVHostSaveRequest { SiteName = "example.com", Content = "content" };
        var method = typeof(ServerApacheSection).GetMethod("SaveConfigAsync", Priv)!;
        await (Task)method.Invoke(cut.Instance, [request])!;

        // SaveApacheVHostConfigAsync PUTs to the vhost config endpoint (site name in the path).
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/servers/30/apache/vhosts/example.com/config"));
    }

    // === ToggleFollow / StopFollow ===

    [Fact]
    public void ToggleFollow_StartsTimer()
    {
        var cut = RenderSection();
        typeof(ServerApacheSection).GetMethod("ToggleFollow", Priv)!.Invoke(cut.Instance, []);

        var following = (bool)typeof(ServerApacheSection).GetField("_logFollowing", Priv)!.GetValue(cut.Instance)!;
        var timer = typeof(ServerApacheSection).GetField("_followTimer", Priv)!.GetValue(cut.Instance);
        Assert.True(following);
        Assert.NotNull(timer);

        // Toggle again to stop
        typeof(ServerApacheSection).GetMethod("ToggleFollow", Priv)!.Invoke(cut.Instance, []);
        following = (bool)typeof(ServerApacheSection).GetField("_logFollowing", Priv)!.GetValue(cut.Instance)!;
        Assert.False(following);
    }

    // === Dispose ===

    [Fact]
    public void Dispose_StopsTimer()
    {
        var cut = RenderSection();
        typeof(ServerApacheSection).GetMethod("ToggleFollow", Priv)!.Invoke(cut.Instance, []);
        cut.Instance.Dispose();
        // No exception
    }

    // === GetStatusBadge ===

    [Theory]
    [InlineData(true, OmniTone.Success)]
    [InlineData(false, OmniTone.Danger)]
    public void GetStatusBadge_ReturnsExpected(bool running, OmniTone expected)
    {
        Assert.Equal(expected, ServerApacheSection.GetStatusBadge(running));
    }
}
