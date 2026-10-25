// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Covers permission-gated server actions, configuration and agent-update progress
/// coordinated by the detail layout.</summary>
public class ServerDetailLayoutTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerDetailLayoutTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        // The layout @injects the scoped loader; the production DI registers it scoped too.
        Services.AddScoped(sp => new ServerDetailLoader(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<HubConnectionFactory>(),
            sp.GetRequiredService<NavigationManager>(),
            NullLogger<ServerDetailLoader>.Instance));
    }

    private static ServerDetailDto MakeServer(int id = 1, string name = "srv-1",
        ServerStatus status = ServerStatus.Online, string agentVersion = "3.0.0") => new()
        {
            Id = id,
            Name = name,
            Hostname = "10.0.0.1",
            IpAddress = "10.0.0.1",
            Type = ServerType.Normal,
            Status = status,
            CpuPercent = 10,
            MemoryUsedMb = 2048,
            MemoryTotalMb = 8192,
            DiskUsedGb = 10,
            DiskTotalGb = 100,
            AgentVersion = agentVersion,
            Tags = ["prod"],
            Services = [],
            Docker = new DockerDataDto { Containers = [], Images = [], ComposeStacks = [], Networks = [], Volumes = [] },
            Apache = new ApacheDataDto(),
            Certbot = new CertbotDataDto(),
            Cron = new CronDataDto(),
            Mail = new MailDataDto(),
            Teamspeak = new TeamspeakDataDto(),
            Portsentry = new PortsentryDataDto(),
            Rkhunter = new RkhunterDataDto()
        };

    /// <summary>Seeds the scoped loader with a server so the layout renders the loaded branch.</summary>
    private ServerDetailLoader SeedLoader(ServerDetailDto server)
    {
        var loader = Services.GetRequiredService<ServerDetailLoader>();
        typeof(ServerDetailLoader).GetProperty(nameof(ServerDetailLoader.Server))!.SetValue(loader, server);
        typeof(ServerDetailLoader).GetProperty(nameof(ServerDetailLoader.InitialLoadCompleted))!.SetValue(loader, true);
        return loader;
    }

    private IRenderedComponent<ServerDetailLayout> RenderLayout(ServerDetailDto server, string section = "overview")
    {
        // The trail is what carries the server name now (D7), and it is route-derived: render the
        // layout on a real server route, the way the router does.
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>()
            .NavigateTo($"/servers/{server.Id}/{section}");
        SeedLoader(server);
        return Render<ServerDetailLayout>(p => p.Add(x => x.Body, b => b.AddMarkupContent(0, "<div id=\"body\">body</div>")));
    }

    // ── action buttons gated by _canWrite ─────────────────────────────────────

    [Fact]
    public void ActionButtons_ConfigurationAndToolsMenu_Rendered_OnOverview_WhenCanWrite()
    {
        var cut = RenderLayout(MakeServer());
        // The fixture grants read and write; Configuration is visible while other write actions are
        // in the tools menu.
        var configuration = cut.Find("button[aria-label=\"Configuration\"]");
        Assert.Equal("Configuration", configuration.GetAttribute("title"));
        Assert.Contains("Configuration", configuration.TextContent, StringComparison.Ordinal);
        Assert.NotNull(configuration.QuerySelector("svg"));
        Assert.DoesNotContain("aria-label=\"Edit\"", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ContactAgent", cut.Markup);

        cut.Find(".omni-overflow-menu__trigger").Click();

        Assert.Contains("ContactAgent", cut.Markup);
        Assert.Contains("UpdateAgent", cut.Markup);
        // The tools menu retires a server; permanent deletion belongs to retired-server management.
        Assert.Contains("RetireServer", cut.Markup);
    }

    [Fact]
    public void R3_58_ToolsMenu_OffersTheCapabilityActivation_OnALinuxServer_AndOpensItsDialog()
    {
        var cut = RenderLayout(MakeServer() with { OsDescription = "Debian GNU/Linux 12" });
        var dialogs = Services.GetRequiredService<OmniDialogService>();
        string? openedTitle = null;
        dialogs.OnOpen += (title, _, _, _) => openedTitle = title;

        cut.Find(".omni-overflow-menu__trigger").Click();
        var item = cut.FindAll("[role=\"menuitem\"], .omni-menu-item, button")
            .First(element => element.TextContent.Contains("EnableAgentCapabilities", StringComparison.Ordinal));
        item.Click();

        Assert.Equal("EnableAgentCapabilities", openedTitle);
    }

    [Fact]
    public void R1_5_TheToolsMenuEntryAndTheDialogTitle_AreNamedUpdateComponents()
    {
        var resources = new System.Resources.ResourceManager("Aetheus.Front.Resources.AppStrings", typeof(Aetheus.Front.Resources.AppStrings).Assembly);

        Assert.Equal("Mettre à jour les composants", resources.GetString("EnableAgentCapabilities", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Equal("Update components", resources.GetString("EnableAgentCapabilities", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void R3_58_ToolsMenu_DoesNotOfferTheCapabilityActivation_OnAWindowsServer()
    {
        var cut = RenderLayout(MakeServer() with { OsDescription = "Microsoft Windows Server 2022" });

        cut.Find(".omni-overflow-menu__trigger").Click();

        Assert.Contains("UpdateAgent", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableAgentCapabilities", cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("logs")]
    [InlineData("pipelines")]
    public void ActionButtons_ConfigurationAndToolsMenu_NotRendered_OffOverview(string section)
    {
        // The server's own actions belong to its Overview; other sections keep theirs.
        var cut = RenderLayout(MakeServer(), section);

        Assert.Contains("id=\"body\"", cut.Markup);
        Assert.Empty(cut.FindAll("button[aria-label=\"Configuration\"]"));
        Assert.Empty(cut.FindAll(".omni-overflow-menu"));
        Assert.False(cut.Instance.IsOverview);
    }

    [Theory]
    [InlineData("servers/1", true)]
    [InlineData("servers/1/overview", true)]
    [InlineData("servers/1/overview?tab=disks", true)]
    [InlineData("servers/1/overview#top", true)]
    [InlineData("servers/1/logs", false)]
    [InlineData("servers/1/overview/extra", false)]
    [InlineData("servers", false)]
    [InlineData("projects/1/overview", false)]
    [InlineData("", false)]
    public void IsOverviewPath_TrueOnlyForServerRootAndOverview(string relativePath, bool expected)
    {
        Assert.Equal(expected, ServerDetailLayout.IsOverviewPath(relativePath));
    }

    [Theory]
    [InlineData("servers/1", "dns")]
    [InlineData("servers/1/overview", "dns")]
    [InlineData("servers/1/pipelines?tab=runs", "account_tree")]
    [InlineData("servers/1/libraries", "library_books")]
    [InlineData("servers/1/logs#end", "receipt_long")]
    [InlineData("servers/1/services", "engineering")]
    [InlineData("servers/1/projects", "folder")]
    [InlineData("servers/1/vaults", "lock")]
    [InlineData("servers/1/releases", "new_releases")]
    [InlineData("servers/1/ports", "lan")]
    [InlineData("servers/1/tasks", "task_alt")]
    [InlineData("servers/1/unknown", "dns")]
    public void SectionIconFor_MatchesTheSideMenuIcon(string relativePath, string expected)
    {
        Assert.Equal(expected, ServerDetailLayout.SectionIconFor(relativePath));
    }

    [Fact]
    public void ActionButtons_Hidden_WhenReadOnly()
    {
        // Re-register services WITHOUT write permission by clearing the permission service.
        var perms = Services.GetRequiredService<PermissionService>();
        perms.SetPermissions([], isAdmin: false);

        var cut = RenderLayout(MakeServer());
        var canWrite = (bool)typeof(ServerDetailLayout).GetField("_canWrite", Priv)!.GetValue(cut.Instance)!;
        Assert.False(canWrite);
        Assert.DoesNotContain("EditServer", cut.Markup);
    }

    // ── edit panel ────────────────────────────────────────────────────────────

    [Fact]
    public void OpenEditDialog_PopulatesFields_AndShowsPanel()
    {
        var cut = RenderLayout(MakeServer(name: "edit-me") with { Tags = ["a", "b"] });

        var method = typeof(ServerDetailLayout).GetMethod("OpenEditDialog", Priv)!;
        cut.InvokeAsync(() => method.Invoke(cut.Instance, []));

        Assert.True((bool)typeof(ServerDetailLayout).GetField("_editVisible", Priv)!.GetValue(cut.Instance)!);
        Assert.Equal("edit-me", typeof(ServerDetailLayout).GetField("_editName", Priv)!.GetValue(cut.Instance));
        cut.Render();
        Assert.Contains("EditServer", cut.Markup);
    }

    [Fact]
    public void OpenEditDialog_ServerNull_NoOp()
    {
        var loader = Services.GetRequiredService<ServerDetailLoader>();
        typeof(ServerDetailLoader).GetProperty(nameof(ServerDetailLoader.InitialLoadCompleted))!.SetValue(loader, true);
        var cut = Render<ServerDetailLayout>(p => p.Add(x => x.Body, b => b.AddMarkupContent(0, "<div/>")));

        var method = typeof(ServerDetailLayout).GetMethod("OpenEditDialog", Priv)!;
        cut.InvokeAsync(() => method.Invoke(cut.Instance, []));

        Assert.False((bool)typeof(ServerDetailLayout).GetField("_editVisible", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task SaveEditAsync_Success_CallsUpdateServerAndUpdatesLoader()
    {
        var server = MakeServer(7, "before");
        var loader = SeedLoader(server);
        _handler.SetJsonResponse("api/servers/7", server with { Name = "after", Tags = ["x"] });

        var cut = Render<ServerDetailLayout>(p => p.Add(x => x.Body, b => b.AddMarkupContent(0, "<div/>")));

        typeof(ServerDetailLayout).GetField("_editName", Priv)!.SetValue(cut.Instance, "after");
        typeof(ServerDetailLayout).GetField("_editTags", Priv)!.SetValue(cut.Instance, "x");
        typeof(ServerDetailLayout).GetField("_editVisible", Priv)!.SetValue(cut.Instance, true);

        var method = typeof(ServerDetailLayout).GetMethod("SaveEditAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        Assert.False((bool)typeof(ServerDetailLayout).GetField("_editSaving", Priv)!.GetValue(cut.Instance)!);
        Assert.False((bool)typeof(ServerDetailLayout).GetField("_editVisible", Priv)!.GetValue(cut.Instance)!);
        // Loader's server was mutated via UpdateServerFields.
        Assert.Equal("after", loader.Server!.Name);
    }

    [Fact]
    public async Task SaveEditAsync_ApiFails_KeepsPanelAndClearsSaving()
    {
        var server = MakeServer(8, "stay");
        // The trail is what carries the server name now (D7), and it is route-derived: render the
        // layout on a real server route, the way the router does.
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>()
            .NavigateTo($"/servers/{server.Id}/overview");
        SeedLoader(server);
        _handler.SetResponse("api/servers/8", System.Net.HttpStatusCode.BadRequest);

        var cut = Render<ServerDetailLayout>(p => p.Add(x => x.Body, b => b.AddMarkupContent(0, "<div/>")));

        typeof(ServerDetailLayout).GetField("_editName", Priv)!.SetValue(cut.Instance, "stay");
        typeof(ServerDetailLayout).GetField("_editTags", Priv)!.SetValue(cut.Instance, "");
        typeof(ServerDetailLayout).GetField("_editVisible", Priv)!.SetValue(cut.Instance, true);

        var method = typeof(ServerDetailLayout).GetMethod("SaveEditAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        Assert.False((bool)typeof(ServerDetailLayout).GetField("_editSaving", Priv)!.GetValue(cut.Instance)!);
    }

    // ── delete ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnRetireServer_ServerNull_ReturnsEarly()
    {
        var loader = Services.GetRequiredService<ServerDetailLoader>();
        typeof(ServerDetailLoader).GetProperty(nameof(ServerDetailLoader.InitialLoadCompleted))!.SetValue(loader, true);
        var cut = Render<ServerDetailLayout>(p => p.Add(x => x.Body, b => b.AddMarkupContent(0, "<div/>")));

        var method = typeof(ServerDetailLayout).GetMethod("OnRetireServer", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Null server short-circuits before the confirm/retire flow - no DELETE is ever sent.
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task OnRetireServer_ShowsBusyStateWhileRetireCompletes()
    {
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Delete, "api/servers/12", async ct =>
        {
            await releaseDelete.Task.WaitAsync(ct);
            return new { };
        });
        var cut = RenderLayout(MakeServer(12, "delete-me"));
        // The retire action lives in the tools menu, so its busy label only exists once it is open.
        cut.Find(".omni-overflow-menu__trigger").Click();

        var deletion = cut.InvokeAsync(cut.Instance.RetireServerConfirmedAsync);

        cut.WaitForAssertion(() =>
        {
            // Busy retirement preserves its label and disables the item without changing menu
            // width.
            var item = cut.FindAll(".omni-menu__item").Single(element => element.TextContent.Contains("RetireServer", StringComparison.Ordinal));
            Assert.True(item.HasAttribute("disabled"));
        });
        Assert.DoesNotContain("Retiring", cut.Markup, StringComparison.Ordinal);
        Assert.False(deletion.IsCompleted);

        releaseDelete.SetResult();
        await deletion;
    }

    // ── contact agent dialog ────────────────────────────────────────────────

    [Fact]
    public void OpenContactAgentDialog_ServerLoaded_OpensDialog()
    {
        var cut = RenderLayout(MakeServer(9, "contact-me"));
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var opened = false;
        dialog.OnOpen += (_, _, _, _) => opened = true;

        var method = typeof(ServerDetailLayout).GetMethod("OpenContactAgentDialog", Priv)!;
        // Returns a non-completing Task (dialog stays open); the loaded server drives OmniDialogService.Open.
        _ = (Task)method.Invoke(cut.Instance, [])!;
        Assert.True(opened);
    }

    [Fact]
    public void OpenContactAgentDialog_ServerNull_ReturnsCompletedTask()
    {
        var loader = Services.GetRequiredService<ServerDetailLoader>();
        typeof(ServerDetailLoader).GetProperty(nameof(ServerDetailLoader.InitialLoadCompleted))!.SetValue(loader, true);
        var cut = Render<ServerDetailLayout>(p => p.Add(x => x.Body, b => b.AddMarkupContent(0, "<div/>")));

        var method = typeof(ServerDetailLayout).GetMethod("OpenContactAgentDialog", Priv)!;
        var task = (Task)method.Invoke(cut.Instance, [])!;
        Assert.True(task.IsCompleted);
    }

    // ── AgentUpdateProgressCard presence + heartbeat wiring ───────────────────

    [Fact]
    public void AgentUpdateProgressCard_IsPresent_WhenServerLoaded()
    {
        var cut = RenderLayout(MakeServer());
        var card = typeof(ServerDetailLayout)
            .GetField("_agentUpdateProgress", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(card);
    }

    [Fact]
    public void OnLoaderChanged_RecomputesCanWrite_NoThrow()
    {
        var loader = SeedLoader(MakeServer(11, "load-me"));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/servers/11/overview");
        var cut = Render<ServerDetailLayout>(p => p.Add(x => x.Body, b => b.AddMarkupContent(0, "<div/>")));

        // Fire the loader event (as a heartbeat would) - recomputes _canWrite + pokes the card.
        var onChanged = typeof(ServerDetailLayout).GetMethod("OnLoaderChanged", Priv)!;
        cut.InvokeAsync(() => onChanged.Invoke(cut.Instance, []));

        Assert.True((bool)typeof(ServerDetailLayout).GetField("_canWrite", Priv)!.GetValue(cut.Instance)!);
        var owner = Services.GetRequiredService<BreadcrumbService>().Items[1];
        Assert.Equal("load-me", owner.Text);
        Assert.False(owner.IsLoading);
        GC.KeepAlive(loader);
    }

    // ── header chrome ─────────────────────────────────────────────────────────

    [Fact]
    public void Header_ShowsServerName_WithoutAStatusBadge()
    {
        // Server status belongs to the Overview essentials card.
        var cut = RenderLayout(MakeServer(name: "header-srv"));
        Assert.Contains("header-srv", cut.Markup);
        Assert.Empty(cut.FindAll(".omni-page-header__frame .omni-badge"));
    }

    [Fact]
    public void Header_OfflineServer_HasNoStatusBadgeEither()
    {
        var cut = RenderLayout(MakeServer(name: "down-srv", status: ServerStatus.Offline) with
        {
            LastHeartbeat = DateTime.Now.AddMinutes(-60)
        });
        Assert.Contains("down-srv", cut.Markup);
        Assert.Empty(cut.FindAll(".omni-page-header__frame .omni-badge"));
    }

    [Fact]
    public void CompatibilityDetails_AreNotRendered_WhenAgentIsUpToDate()
    {
        var cut = RenderLayout(MakeServer() with
        {
            AgentCompatibility = new AgentCompatibilityDto
            {
                Status = AgentCompatibilityStatus.UpToDate,
                Reason = AgentCompatibilityReason.Current,
                InstalledVersion = "1.0.1294",
                TargetVersion = "1.0.1294",
                AgentProtocolVersion = 2,
                MinimumSupportedProtocol = 1,
                MaximumSupportedProtocol = 2
            }
        });

        Assert.DoesNotContain("AgentCompatibilityDetails", cut.Markup);
    }

    [Fact]
    public void CompatibilityDetails_AreNotRendered_WhenAgentRequiresUpdate()
    {
        var cut = RenderLayout(MakeServer() with
        {
            AgentCompatibility = new AgentCompatibilityDto
            {
                Status = AgentCompatibilityStatus.UpdateRequired,
                Reason = AgentCompatibilityReason.UnsupportedProtocol,
                InstalledVersion = "1.0.1200",
                TargetVersion = "1.0.1294",
                AgentProtocolVersion = 0,
                MinimumSupportedProtocol = 1,
                MaximumSupportedProtocol = 2
            }
        });

        Assert.DoesNotContain("AgentCompatibilityDetails", cut.Markup);
        Assert.DoesNotContain("AgentCompatibilityReason_UnsupportedProtocol", cut.Markup);
        Assert.DoesNotContain("1.0.1200", cut.Markup);
    }

    [Fact]
    public void Body_Rendered_WhenServerLoaded()
    {
        var cut = RenderLayout(MakeServer());
        Assert.Contains("id=\"body\"", cut.Markup);
    }

    [Fact]
    public void NotFoundState_Rendered_WhenServerNullAfterLoad()
    {
        var loader = Services.GetRequiredService<ServerDetailLoader>();
        typeof(ServerDetailLoader).GetProperty(nameof(ServerDetailLoader.InitialLoadCompleted))!.SetValue(loader, true);
        var cut = Render<ServerDetailLayout>(p => p.Add(x => x.Body, b => b.AddMarkupContent(0, "<div/>")));
        Assert.Contains("ServerNotFound", cut.Markup);
    }

    // ── R3-29: the header's back arrow climbs to the logical parent ──────────

    [Theory]
    [InlineData("mail", "http://localhost/servers/1/overview")]
    // The server crumb links to the overview: on the overview the arrow must reach the list, not itself.
    [InlineData("overview", "http://localhost/servers")]
    public async Task HeaderBackArrow_LeadsToTheLogicalParent(string section, string expected)
    {
        var cut = RenderLayout(MakeServer(), section);
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        await cut.Find(".omni-page-header__back").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(expected, nav.Uri);
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "historyBack");
    }
}
