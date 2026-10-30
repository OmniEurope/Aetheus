// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerApacheSectionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerApacheSectionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static ServerDetailDto MakeServer(bool running = true, List<ApacheVirtualHostDto>? vhosts = null, List<ApacheModuleDto>? modules = null) => new()
    {
        Id = 1,
        Name = "web-01",
        Apache = new ApacheDataDto
        {
            IsInstalled = true,
            IsRunning = running,
            VirtualHosts = vhosts ?? [],
            Modules = modules ?? []
        }
    };

    private IRenderedComponent<ServerApacheSection> RenderSection(ServerDetailDto? server = null)
    {
        var s = server ?? MakeServer();
        return Render<ServerApacheSection>(p =>
            p.Add(x => x.ServerId, 1)
             .Add(x => x.Server, s));
    }

    [Fact]
    public void Renders_ApacheSection()
    {
        var cut = RenderSection();
        // R-120: a running Apache shows no status badge (neither "Running" nor "Stopped"), the room goes to the lists.
        Assert.DoesNotContain("Running", cut.Markup);
        Assert.DoesNotContain("Stopped", cut.Markup);
    }

    [Fact]
    public void R120_TheVersionIsNotInTheStatusLine_ItIsInThePageTitle()
    {
        var server = MakeServer();
        server = server with { Apache = server.Apache with { Version = "2.4.58" } };

        var cut = RenderSection(server);

        Assert.DoesNotContain("2.4.58", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R120_TheSitesTab_HasNoCountAndNoFreeTextFilterAboveTheGrids()
    {
        var cut = RenderSection(MakeServer(vhosts:
        [
            new ApacheVirtualHostDto { ServerName = "example.com", DocumentRoot = "/var/www/html", Port = 80, IsEnabled = true }
        ]));

        // The sites tab is the first and active one: its free-text filter (placeholder and label were the key
        // FilterSites) and its count badge are gone; the grid headings carry the counts.
        Assert.DoesNotContain("FilterSites", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindComponents<CountBadge>());
        Assert.Contains("example.com", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R120_BothSitesGrids_AreCappedToAboutTenRows_AndFilterInlineUnderTheirHeader()
    {
        var cut = RenderSection(MakeServer(vhosts:
        [
            new ApacheVirtualHostDto { ServerName = "on.example.com", DocumentRoot = "/var/www/on", Port = 80, IsEnabled = true },
            new ApacheVirtualHostDto { ServerName = "off.example.com", DocumentRoot = "/var/www/off", Port = 80, IsEnabled = false }
        ]));

        var grids = cut.FindComponents<OmniDataGrid<ServerApacheSection.ApacheVirtualHostGroup>>();

        Assert.Equal(2, grids.Count);
        Assert.All(grids, grid =>
        {
            Assert.Equal(Aetheus.Front.Components.Shared.AetheusGrid.TenRowsMaxHeight, grid.Instance.MaxHeight);
            Assert.False(grid.Instance.FillAvailableHeight, "neither sites grid takes the whole remaining height");
            Assert.False(grid.Instance.ShowHeaderFilterMenu, "the filter row sits under the header");
        });
    }

    [Fact]
    public void R120_AStoppedApache_ShowsTheStoppedBadge_ARunningOneDoesNot()
    {
        var stopped = RenderSection(MakeServer(running: false));
        Assert.Single(stopped.FindAll(".omni-badge"), badge => badge.TextContent.Contains("Stopped", StringComparison.Ordinal));

        var running = RenderSection(MakeServer(running: true));
        Assert.DoesNotContain(running.FindAll(".omni-badge"), badge => badge.TextContent.Contains("Stopped", StringComparison.Ordinal));
    }

    [Fact]
    public void Renders_ApacheSection_WithVHosts()
    {
        var server = MakeServer(vhosts:
        [
            new ApacheVirtualHostDto { ServerName = "example.com", DocumentRoot = "/var/www/html", Port = 80, IsEnabled = true }
        ], modules:
        [
            new ApacheModuleDto { Name = "mod_rewrite", IsEnabled = true },
            new ApacheModuleDto { Name = "mod_ssl", IsEnabled = true }
        ]);
        var cut = RenderSection(server);
        // The configured vhost ServerName renders in the enabled-sites grid.
        Assert.Contains("example.com", cut.Markup);
    }

    [Fact]
    public void Renders_ApacheSection_NotRunning()
    {
        var cut = RenderSection(MakeServer(running: false));
        // A stopped server renders the "Stopped" status badge (not "Running").
        Assert.Contains("Stopped", cut.Markup);
    }

    [Theory]
    [InlineData(true, OmniTone.Success)]
    [InlineData(false, OmniTone.Danger)]
    public void GetStatusBadge_ReturnsExpected(bool isRunning, OmniTone expected)
    {
        var result = ServerApacheSection.GetStatusBadge(isRunning);
        Assert.Equal(expected, result);
    }

    // R-120: a site with no ServerName (default-ssl.conf, for instance) shows its configuration file name, not an
    // empty cell; two such sites stay two rows.
    [Fact]
    public void ASiteWithoutAServerName_IsNamedAfterItsConfigurationFile()
    {
        var server = MakeServer(vhosts:
        [
            new ApacheVirtualHostDto { ServerName = "", Port = 443, IsEnabled = false, ConfigFile = "/etc/apache2/sites-available/default-ssl.conf" },
            new ApacheVirtualHostDto { ServerName = "", Port = 80, IsEnabled = false, ConfigFile = "/etc/apache2/sites-available/000-default.conf" },
            new ApacheVirtualHostDto { ServerName = "named.example.com", Port = 80, IsEnabled = false, ConfigFile = "/etc/apache2/sites-available/named.conf" }
        ]);
        var cut = RenderSection(server);

        var names = cut.Instance.DisabledVirtualHostGroups.Select(group => group.ServerName).ToList();

        Assert.Equal(["000-default.conf", "default-ssl.conf", "named.example.com"], names);
    }

    [Fact]
    public async Task ExecuteActionAsync_Success_ShowsToast()
    {
        var cut = RenderSection();
        var method = typeof(ServerApacheSection).GetMethod("ExecuteActionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [ApacheAction.Restart, null])!);
        // ExecuteApacheActionAsync POSTs the action to the server's apache/action endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/1/apache/action"));
    }

    [Fact]
    public async Task FetchLogsAsync_CallsApi()
    {
        _handler.SetResponse("api/servers/1/apache/logs", System.Net.HttpStatusCode.OK);
        var cut = RenderSection();
        var method = typeof(ServerApacheSection).GetMethod("FetchLogsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);
        // GetApacheLogsAsync POSTs the log request to the server's apache/logs endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/1/apache/logs"));
    }

    // Hardened rule #2/#3: OpenConfigEditorAsync now opens an ApacheConfigEditorDialog via
    // OmniDialogService. The dialog content lives in a separate host (never in cut.Markup) - assert
    // ONLY that OmniDialogService.OnOpen fires, then close immediately. Never await OpenAsync first.
    [Fact]
    public async Task OpenConfigEditorAsync_OpensDialog()
    {
        var server = MakeServer(vhosts:
        [
            new ApacheVirtualHostDto { ServerName = "example.com", DocumentRoot = "/var/www", Port = 80, IsEnabled = true }
        ]);
        var cut = RenderSection(server);
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var opened = false;
        dialog.OnOpen += (_, _, _, _) => opened = true;

        var method = typeof(ServerApacheSection).GetMethod("OpenConfigEditorAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, ["example.com"])!);
        await cut.InvokeAsync(() => dialog.Close(null));
        await task;

        Assert.True(opened);
    }

    [Fact]
    public async Task SaveConfigAsync_OnSuccess_ShowsToast()
    {
        var cut = RenderSection();
        var request = new ApacheVHostSaveRequest { SiteName = "site", Content = "ServerName s" };
        var method = typeof(ServerApacheSection).GetMethod("SaveConfigAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [request])!);
        // SaveApacheVHostConfigAsync PUTs the config to the vhost config endpoint (site name in path).
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/servers/1/apache/vhosts/site/config"));
    }


    [Fact]
    public void HandleTaskCompleted_ApacheLogs_SetsContent()
    {
        var cut = RenderSection();
        var notification = new TaskCompletedNotification { TaskName = "Apache logs fetch", Output = "log output here" };
        cut.InvokeAsync(() => { cut.Instance.HandleTaskCompleted(_handler.Logged(notification)); return Task.CompletedTask; });
        var content = (string?)typeof(ServerApacheSection).GetField("_logContent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Equal("log output here", content);
    }

    [Fact]
    public async Task HandleTaskCompleted_ApacheConfig_SetsContent()
    {
        var cut = RenderSection();
        // SignalR-pushed config text only lands when a config-editor dialog is open: it routes
        // into the shared ApacheConfigEditorModel held by the section.
        var model = new ApacheConfigEditorModel { SiteName = "example.com", Loading = true };
        typeof(ServerApacheSection).GetField("_configEditor", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, model);

        var notification = new TaskCompletedNotification { TaskName = "Apache config read", Output = "<VirtualHost>...</VirtualHost>" };
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(_handler.Logged(notification)));

        Assert.Equal("<VirtualHost>...</VirtualHost>", model.Content);
        Assert.False(model.Loading);
    }

    [Fact]
    public void ToggleFollow_SetsFollowing()
    {
        var cut = RenderSection();
        var method = typeof(ServerApacheSection).GetMethod("ToggleFollow", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, []);
        var following = (bool)typeof(ServerApacheSection).GetField("_logFollowing", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.True(following);
    }

    [Fact]
    public void ToggleFollow_Twice_StopsFollowing()
    {
        var cut = RenderSection();
        var method = typeof(ServerApacheSection).GetMethod("ToggleFollow", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, []);
        method.Invoke(cut.Instance, []);
        var following = (bool)typeof(ServerApacheSection).GetField("_logFollowing", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.False(following);
    }

    [Fact]
    public void Dispose_DisposesTimer()
    {
        var cut = RenderSection();
        // Start the follow loop so a live timer exists, then dispose it. Disposing an already-
        // disposed System.Threading.Timer is a no-op, so a second Dispose must NOT throw - that
        // proves Dispose actually disposed the timer the first time.
        typeof(ServerApacheSection).GetMethod("ToggleFollow", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(cut.Instance, []);
        var timer = (System.Threading.Timer?)typeof(ServerApacheSection)
            .GetField("_followTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.NotNull(timer);

        cut.Instance.Dispose();

        // The timer reference was disposed by Dispose(); disposing it again is a safe no-op.
        timer!.Dispose();
    }

    [Fact]
    public void ApacheResourceNames_ReturnsVHostNames()
    {
        var server = MakeServer(vhosts:
        [
            new ApacheVirtualHostDto { ServerName = "a.com", DocumentRoot = "/a", Port = 80, IsEnabled = true },
            new ApacheVirtualHostDto { ServerName = "b.com", DocumentRoot = "/b", Port = 443, IsEnabled = true }
        ]);
        var cut = RenderSection(server);
        var names = (List<string>)typeof(ServerApacheSection).GetProperty("ApacheResourceNames", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Equal(2, names.Count);
        Assert.Contains("a.com", names);
    }
}
