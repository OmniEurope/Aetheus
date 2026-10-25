// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Offline-agent sections disable task actions and their handlers send nothing; online
/// agents retain available operations.</summary>
public sealed class ServerSectionsOfflineAgentTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerSectionsOfflineAgentTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static ServerDetailDto Server(ServerStatus status) => new()
    {
        Id = 30,
        Name = "srv",
        Hostname = "10.0.0.3",
        Status = status,
        Tags = [],
        Services = [],
        Docker = new DockerDataDto { Containers = [], Images = [], ComposeStacks = [], Networks = [], Volumes = [] },
        Apache = new ApacheDataDto { IsInstalled = true, IsRunning = true, Modules = [], VirtualHosts = [] },
        Certbot = new CertbotDataDto { IsInstalled = true, Certificates = [] },
        Rkhunter = new RkhunterDataDto { IsInstalled = true }
    };

    private bool SentAnyRequestTo(string path) =>
        _handler.Requests.Any(request => request.Method != HttpMethod.Get.Method
            && request.Url.Contains(path, StringComparison.Ordinal));

    private void AssertOfflineToast() =>
        Assert.Contains(Services.Toasts(), toast =>
            toast.Severity == OmniSeverity.Danger && toast.Summary == "TaskAgentOffline");

    [Fact]
    public async Task Apache_OfflineAgent_ShowsTheNotice_DisablesTest_AndSendsNothing()
    {
        var cut = this.RenderWithPageHeader<ServerApacheSection>(p => p.Add(x => x.Server, Server(ServerStatus.Offline)).Add(x => x.ServerId, 30));

        Assert.Contains("AgentOfflineActionBlocked", cut.Find(".agent-offline-notice").TextContent, StringComparison.Ordinal);
        cut.Find(".omni-overflow-menu__trigger").Click();
        Assert.True(cut.Find(".omni-menu__item[title='TestConfig']").HasAttribute("disabled"));

        // The handler behind the disabled button refuses too (a stale render, a keyboard path).
        var execute = typeof(ServerApacheSection).GetMethod("ExecuteActionAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)execute.Invoke(cut.FindComponent<ServerApacheSection>().Instance, [ApacheAction.TestConfig, null])!);
        var logs = typeof(ServerApacheSection).GetMethod("FetchLogsAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)logs.Invoke(cut.FindComponent<ServerApacheSection>().Instance, [])!);

        Assert.False(SentAnyRequestTo("api/servers/30/apache"));
        AssertOfflineToast();
    }

    [Fact]
    public async Task Apache_OnlineAgent_HasNoNotice_AndQueuesTheTest()
    {
        _handler.SetJsonResponse("api/servers/30/apache/action", "{}");
        var cut = this.RenderWithPageHeader<ServerApacheSection>(p => p.Add(x => x.Server, Server(ServerStatus.Online)).Add(x => x.ServerId, 30));

        Assert.Empty(cut.FindAll(".agent-offline-notice"));
        cut.Find(".omni-overflow-menu__trigger").Click();
        var test = cut.Find(".omni-menu__item[title='TestConfig']");
        Assert.False(test.HasAttribute("disabled"));
        await cut.InvokeAsync(() => test.Click());

        cut.WaitForAssertion(() => Assert.True(SentAnyRequestTo("api/servers/30/apache/action")));
    }

    [Fact]
    public async Task Certbot_OfflineAgent_DisablesItsActions_AndSendsNothing()
    {
        var cut = Render<ServerCertbotSection>(p => p.Add(x => x.Server, Server(ServerStatus.Offline)).Add(x => x.ServerId, 30));

        Assert.NotEmpty(cut.FindAll(".agent-offline-notice"));
        var execute = typeof(ServerCertbotSection).GetMethod("ExecuteActionAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)execute.Invoke(cut.Instance, [CertbotAction.RenewalCheck, null, null])!);

        Assert.False(SentAnyRequestTo("api/servers/30/certbot"));
        AssertOfflineToast();
    }

    [Fact]
    public async Task Docker_OfflineAgent_DisablesPrune_AndOpensNoDialog()
    {
        var cut = Render<ServerDockerSection>(p => p.Add(x => x.Server, Server(ServerStatus.Offline)).Add(x => x.ServerId, 30));

        Assert.NotEmpty(cut.FindAll(".agent-offline-notice"));
        var open = typeof(ServerDockerSection).GetMethod("OpenPruneDialogAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)open.Invoke(cut.Instance, [])!);

        Assert.False(SentAnyRequestTo("api/servers/30/docker"));
        AssertOfflineToast();
    }

    /// <summary>The loader the server detail layout cascades, loaded with a server in <paramref name="status"/>.</summary>
    private async Task<ServerDetailLoader> LoaderAsync(ServerStatus status)
    {
        var loader = new ServerDetailLoader(
            Services.GetRequiredService<ApiClient>(),
            Services.GetRequiredService<HubConnectionFactory>(),
            Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(),
            NullLogger<ServerDetailLoader>.Instance);
        _handler.SetJsonResponse("api/servers/30", Server(status));
        _handler.SetJsonResponse("api/monitoring/servers/30/metrics", new List<ServerMetricDto>());
        await loader.EnsureLoadedAsync(30, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(status, loader.Server?.Status);
        return loader;
    }

    [Fact]
    public async Task Rkhunter_ReadsTheAgentFromTheCascadedLoader_AndRefusesOffline()
    {
        var loader = await LoaderAsync(ServerStatus.Offline);
        _handler.SetJsonResponse("api/servers/30/rkhunter/warnings", new List<RkhunterWarningDto>());
        _handler.SetJsonResponse("api/servers/30/rkhunter/history", new List<RkhunterScanResultDto>());

        var cut = Render<ServerRkhunterSection>(p => p
            .Add(x => x.ServerId, 30)
            .Add(x => x.Rk, new RkhunterDataDto { IsInstalled = true })
            .AddCascadingValue("ServerLoader", loader));

        Assert.NotEmpty(cut.FindAll(".agent-offline-notice"));
        var execute = typeof(ServerRkhunterSection).GetMethod("ExecuteActionAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)execute.Invoke(cut.Instance, [RkhunterAction.RunScan])!);

        Assert.False(SentAnyRequestTo("rkhunter/action"));
        AssertOfflineToast();
    }

    private void StubMailReads()
    {
        _handler.SetJsonResponse("api/servers/10/mail/domains", new PaginatedResult<MailDomainDto>
        {
            Items = [new() { Id = 1, Name = "example.com", IsActive = true, DkimSelector = "default" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/servers/10/mail/accounts", new PaginatedResult<MailAccountDto>
        {
            Items = [new() { Id = 1, Email = "admin@example.com", IsActive = true, QuotaMb = 1024 }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/servers/10/mail/aliases", new PaginatedResult<MailAliasDto>());
        _handler.SetJsonResponse("api/servers/10/mail/diagnostics", new MailDiagnosticsDto());
    }

    private IRenderedComponent<ServerMailSection> RenderMail(ServerStatus status)
    {
        StubMailReads();
        var server = ServerTestData.MakeMailServer() with { Status = status, MailSetupAvailable = true };
        return Render<ServerMailSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, server.Id));
    }

    // The accounts tab opens first (R1-29); these tests exercise the domains tab.
    private void OpenMailTab(string tab) =>
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo($"/servers/10/mail?tab={tab}");

    [Fact]
    public async Task Mail_OfflineAgent_ShowsTheNotice_DisablesItsWrites_AndSendsNothing()
    {
        OpenMailTab("domains");
        var cut = RenderMail(ServerStatus.Offline);

        Assert.Contains("AgentOfflineActionBlocked", cut.Find(".agent-offline-notice").TextContent, StringComparison.Ordinal);
        // Only the open tab (domains) renders; the accounts and aliases tabs use the same guard.
        Assert.True(cut.Find("button[title='AddDomain']").HasAttribute("disabled"));

        var delete = typeof(ServerMailSection).GetMethod("DeleteDomainAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)delete.Invoke(cut.Instance, [1])!);
        var add = typeof(ServerMailSection).GetMethod("CreateAccountAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)add.Invoke(cut.Instance, [])!);

        Assert.False(SentAnyRequestTo("api/servers/10/mail"));
        AssertOfflineToast();
    }

    [Fact]
    public async Task Mail_OnlineAgent_HasNoNotice_AndQueuesTheDelete()
    {
        OpenMailTab("domains");
        var cut = RenderMail(ServerStatus.Online);

        Assert.Empty(cut.FindAll(".agent-offline-notice"));
        Assert.False(cut.Find("button[title='AddDomain']").HasAttribute("disabled"));
        var delete = typeof(ServerMailSection).GetMethod("DeleteDomainAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)delete.Invoke(cut.Instance, [1])!);

        Assert.True(SentAnyRequestTo("api/servers/10/mail/domains/1"));
    }

    private IRenderedComponent<PageHeaderOutletHost> RenderPortsentry(ServerDetailLoader loader)
    {
        _handler.SetPaginatedJsonResponse("api/servers/30/portsentry/blocked", new List<PortsentryBlockedIpDto>());
        _handler.SetPaginatedJsonResponse("api/servers/30/portsentry/whitelist", new List<PortsentryWhitelistIpDto>());
        _handler.SetJsonResponse("api/servers/30/portsentry/filter-values", new PortsentryFilterValuesDto());
        return this.RenderWithPageHeader<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 30)
            .Add(x => x.Ps, new PortsentryDataDto { IsInstalled = true, IsRunning = true })
            .AddCascadingValue("ServerLoader", loader));
    }

    [Fact]
    public async Task Portsentry_ReadsTheAgentFromTheCascadedLoader_AndRefusesOffline()
    {
        var cut = RenderPortsentry(await LoaderAsync(ServerStatus.Offline));

        Assert.NotEmpty(cut.FindAll(".agent-offline-notice"));
        Assert.True(cut.FindMenuItem("ViewLogs")!.HasAttribute("disabled"));
        var execute = typeof(ServerPortsentrySection).GetMethod("ExecuteActionAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)execute.Invoke(cut.FindComponent<ServerPortsentrySection>().Instance, [PortsentryAction.Restart])!);
        var unblock = typeof(ServerPortsentrySection).GetMethod("UnblockIpAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)unblock.Invoke(cut.FindComponent<ServerPortsentrySection>().Instance, ["10.0.0.9"])!);

        Assert.False(SentAnyRequestTo("api/servers/30/portsentry"));
        AssertOfflineToast();
    }

    [Fact]
    public async Task Portsentry_OnlineAgent_QueuesTheAction()
    {
        _handler.SetJsonResponse("api/servers/30/portsentry/action", "{}");
        var cut = RenderPortsentry(await LoaderAsync(ServerStatus.Online));

        Assert.Empty(cut.FindAll(".agent-offline-notice"));
        var execute = typeof(ServerPortsentrySection).GetMethod("ExecuteActionAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)execute.Invoke(cut.FindComponent<ServerPortsentrySection>().Instance, [PortsentryAction.Restart])!);

        Assert.True(SentAnyRequestTo("api/servers/30/portsentry/action"));
    }

    private static ServerDetailDto WithDocker(ServerStatus status) => Server(status) with
    {
        Docker = new DockerDataDto
        {
            Containers = [new DockerContainerDto { ContainerId = "abc123def456", Name = "nginx", Image = "nginx:latest", State = "running", Status = "Up 2h" }],
            Images = [new DockerImageDto { ImageId = "sha256:0123456789ab", Repository = "nginx", Tag = "latest" }],
            ComposeStacks = [new DockerComposeStackDto { Name = "monitoring", Status = "running(3)", RunningCount = 3, TotalCount = 3 }],
            Networks = [],
            Volumes = []
        }
    };

    [Theory]
    [InlineData(ServerStatus.Offline)]
    [InlineData(ServerStatus.Online)]
    public async Task DockerContainers_SendTheirActions_OnlyWhileTheAgentIsOnline(ServerStatus status)
    {
        _handler.SetJsonResponse("api/servers/30/docker/action", "{}");
        var cut = Render<DockerContainersTab>(p => p
            .Add(x => x.Server, WithDocker(status)).Add(x => x.ServerId, 30).Add(x => x.CanAdmin, true).Add(x => x.InitialLoaded, true));

        var offline = status == ServerStatus.Offline;
        // R3-42: Restart is the row's button; Stop and the logs sit behind its menu, which cannot open offline.
        Assert.Equal(offline, cut.Find("button[title='Restart']").HasAttribute("disabled"));
        Assert.Equal(offline, cut.Find("tbody .omni-overflow-menu__trigger").HasAttribute("disabled"));
        if (!offline)
        {
            cut.Find("tbody .omni-overflow-menu__trigger").Click();
            Assert.False(cut.FindAll(".omni-menu__item").Single(i => i.TextContent.Trim() == "Stop").HasAttribute("disabled"));
            Assert.False(cut.FindAll(".omni-menu__item").Single(i => i.TextContent.Trim() == "ViewLogs").HasAttribute("disabled"));
        }
        var execute = typeof(DockerContainersTab).GetMethod("DockerActionAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)execute.Invoke(cut.Instance, ["abc123def456", DockerContainerAction.Restart])!);

        Assert.Equal(!offline, SentAnyRequestTo("api/servers/30/docker/action"));
        if (offline) AssertOfflineToast();
    }

    [Theory]
    [InlineData(ServerStatus.Offline)]
    [InlineData(ServerStatus.Online)]
    public async Task DockerImagesAndCompose_ReadTheAgentFromTheCascadedLoader(ServerStatus status)
    {
        var loader = await LoaderAsync(status);
        var docker = WithDocker(status).Docker;
        _handler.SetResponse("api/servers/30/docker/images/", System.Net.HttpStatusCode.OK);
        _handler.SetJsonResponse("api/servers/30/docker/compose/action", "{}");
        var images = Render<DockerImagesTab>(p => p
            .Add(x => x.ServerId, 30).Add(x => x.CanAdmin, true).Add(x => x.Images, docker.Images)
            .AddCascadingValue("ServerLoader", loader));
        var compose = Render<DockerComposeTab>(p => p
            .Add(x => x.ServerId, 30).Add(x => x.CanAdmin, true).Add(x => x.Stacks, docker.ComposeStacks)
            .AddCascadingValue("ServerLoader", loader));

        var offline = status == ServerStatus.Offline;
        Assert.Equal(offline, images.Find("button[title='Delete']").HasAttribute("disabled"));
        // Restart sits in the stack row's "More actions" menu (STD-BTN detection 11).
        compose.Find(".omni-overflow-menu__trigger").Click();
        Assert.Equal(offline, compose.Find(".omni-menu__item[title='Restart']").HasAttribute("disabled"));
        var remove = typeof(DockerImagesTab).GetMethod("RemoveImageAsync", Priv)!;
        await images.InvokeAsync(() => (Task)remove.Invoke(images.Instance, ["sha256:0123456789ab"])!);
        var action = typeof(DockerComposeTab).GetMethod("ComposeActionAsync", Priv)!;
        await compose.InvokeAsync(() => (Task)action.Invoke(compose.Instance, ["monitoring", DockerComposeAction.Restart])!);

        Assert.Equal(!offline, SentAnyRequestTo("api/servers/30/docker/images/"));
        Assert.Equal(!offline, SentAnyRequestTo("api/servers/30/docker/compose/action"));
        if (offline) AssertOfflineToast();
    }

    [Fact]
    public async Task Overview_OfflineAgent_DisablesTheAgentUpdate_AndSendsNothing()
    {
        var server = Server(ServerStatus.Offline) with
        {
            AgentVersion = "1.0.1547",
            AgentCompatibility = new AgentCompatibilityDto
            {
                Status = AgentCompatibilityStatus.UpdateRequired,
                InstalledVersion = "1.0.1547",
                TargetVersion = "1.0.1552"
            }
        };
        var cut = Render<ServerOverviewSection>(p => p.Add(x => x.Server, server));

        Assert.True(cut.Find("button[title='UpdateAgent']").HasAttribute("disabled"));
        var update = typeof(ServerOverviewSection).GetMethod("UpdateAgentAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)update.Invoke(cut.Instance, [])!);

        Assert.False(SentAnyRequestTo("agent/update"));
        AssertOfflineToast();
    }

    [Fact]
    public async Task TeamspeakOperations_AreNotSent_WhileTheAgentIsOffline()
    {
        var sent = false;
        var runner = new TeamspeakOperationRunner(Services.GetRequiredService<NotifyHelper>(), () => true);

        var queued = await runner.RunOperationAsync(() => { sent = true; return Task.FromResult(new ApiStatus(true, false, false)); }, "Kick");

        Assert.False(queued);
        Assert.False(sent);
    }

    private static MailDataDto MailStack() => new()
    {
        IsInstalled = true,
        IsManagedByAetheus = true,
        HelperVersion = ServerMailReinstall.MinimumHelperVersion,
        Hostname = "mx.example.com"
    };

    [Theory]
    [InlineData(ServerStatus.Offline)]
    [InlineData(ServerStatus.Online)]
    public async Task MailReinstall_IsOfferedOnlyWhileTheAgentIsOnline_WithTheOfflineReason(ServerStatus status)
    {
        var server = ServerTestData.MakeMailServer() with { Status = status, MailSetupAvailable = true };
        // The button is drawn in the page header.
        var host = this.RenderWithOutlets<ServerMailReinstall>(new Dictionary<string, object?>
        {
            [nameof(ServerMailReinstall.Server)] = server,
            [nameof(ServerMailReinstall.ServerId)] = server.Id,
            [nameof(ServerMailReinstall.Mail)] = MailStack(),
            [nameof(ServerMailReinstall.CanManage)] = true
        }, PageHeaderActions.SectionName);
        var cut = host.FindComponent<ServerMailReinstall>();

        var offline = status == ServerStatus.Offline;
        var purge = host.FindAll("button").Single(b => b.TextContent.Contains("MailReinstallAction", StringComparison.Ordinal));
        Assert.Equal(offline, purge.HasAttribute("disabled"));
        Assert.Equal(offline ? "AgentOfflineActionBlocked" : "MailReinstall", purge.GetAttribute("title"));

        if (!offline) return;
        // The handler behind the disabled button refuses too, before any dialog or request.
        var run = typeof(ServerMailReinstall).GetMethod("PurgeAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)run.Invoke(cut.Instance, [])!);

        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("/mail/", StringComparison.Ordinal));
        AssertOfflineToast();
    }

    [Fact]
    public async Task MailSetupAssistant_OfflineAgent_DisablesTheProbeAndTheSetup_AndQueuesNothing()
    {
        var server = ServerTestData.MakeMailServer(installed: false) with
        {
            Status = ServerStatus.Offline,
            MailSetupAvailable = true,
            AgentCapabilities = [AgentCapabilities.MailOutboundProbe, AgentCapabilities.ShellExecution]
        };
        _handler.SetJsonResponse("api/servers/10/mail/prerequisites?hostname=mx.example.com", new MailDiagnosticsDto
        {
            Items =
            [
                new() { Key = "hostname-dns", Verdict = MailCheckVerdict.Ok, Detail = "mx.example.com" },
                new() { Key = "reverse-dns", Verdict = MailCheckVerdict.Ok, Detail = "203.0.113.25" }
            ]
        });
        var cut = Render<ServerMailSetupAssistant>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, 10));
        cut.Find("#mail-setup-domain").Input("example.com");
        cut.Find("button.omni-wizard__next").Click();
        cut.WaitForAssertion(() => Assert.True(cut.Instance.ARecordOk));
        cut.Find("button.omni-wizard__next").Click();
        cut.Find("button.omni-wizard__next").Click();
        Assert.Equal(ServerMailSetupAssistant.StepOutbound, cut.Instance.Step);

        // The DNS checks run on the backend and stay live; the port 25 probe is an agent task.
        var probe = cut.FindAll("button").Single(b => b.TextContent.Contains("MailCheckPrerequisite", StringComparison.Ordinal));
        Assert.True(probe.HasAttribute("disabled"));
        Assert.Equal("AgentOfflineActionBlocked", probe.GetAttribute("title"));
        Assert.Contains("AgentOfflineActionBlocked", cut.Find(".mail-setup-agent-offline").TextContent, StringComparison.Ordinal);
        Assert.False(cut.Instance.CanConfigure);

        var runProbe = typeof(ServerMailSetupAssistant).GetMethod("ProbeOutboundAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)runProbe.Invoke(cut.Instance, [])!);
        var configure = typeof(ServerMailSetupAssistant).GetMethod("ConfigureAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)configure.Invoke(cut.Instance, [])!);

        Assert.False(SentAnyRequestTo("api/servers/10/mail"));
        AssertOfflineToast();
    }
}
