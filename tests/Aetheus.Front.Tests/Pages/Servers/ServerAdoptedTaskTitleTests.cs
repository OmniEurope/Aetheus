// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Resources;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Bunit.Rendering;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R-12: a task adopted on arrival (the user left the page and came back while it runs) is named as the page
/// names the task it launches itself, never by its raw backend name. Table-driven over every server page that follows
/// tasks: the page is rendered with the task already in the live feed, and the follower's title must be the expected
/// one. (The test localizer returns the resource key, so an expected title is made of keys.) The adopted task also
/// disables the page's launch actions on the pages that gate them with a flag of their own.
/// </summary>
public sealed class ServerAdoptedTaskTitleTests : BunitContext
{
    private const int ServerId = 31;
    private const int TaskId = 90031;

    private readonly BunitTestHelper.TestHandler _handler;

    public ServerAdoptedTaskTitleTests()
    {
        _handler = BunitTestHelper.RegisterServices(
            this, isAdmin: true, extraPermissionKeys: [PermissionKeys.ServerFirewallManage]);
    }

    private TaskTrackerService Tracker => Services.GetRequiredService<TaskTrackerService>();

    private static ServerDetailDto Server(Func<ServerDetailDto, ServerDetailDto>? configure = null)
    {
        var server = new ServerDetailDto
        {
            Id = ServerId,
            Name = "adopt-srv",
            Hostname = "10.0.0.31",
            Type = ServerType.Normal,
            Status = ServerStatus.Online,
            Tags = [],
            Services = []
        };
        return configure is null ? server : configure(server);
    }

    private static IRenderedComponent<ServerTaskFollower> TheFollower(IRenderedComponent<Microsoft.AspNetCore.Components.IComponent> cut) =>
        cut.FindComponents<ServerTaskFollower>().Single(follower => follower.Instance.TaskId == TaskId);

    private IRenderedComponent<ServerTaskFollower> Render(string page)
    {
        switch (page)
        {
            case "firewall":
                _handler.SetJsonResponse(HttpMethod.Get, "firewall", new ServerFirewallDto
                {
                    Installed = true,
                    Active = true,
                    StatusKnown = true,
                    FirewallManagementAvailable = true,
                    Rules = []
                });
                return TheFollower(Render<ServerFirewallSection>(p => p
                    .Add(c => c.ServerId, ServerId).Add(c => c.FirewallManagementAvailable, true)));
            case "patch":
                _handler.SetJsonResponse(HttpMethod.Get, "security-updates", new ServerSecurityUpdatesDto
                {
                    Known = true,
                    PackageManagerPresent = true,
                    PatchManagementAvailable = true,
                    CheckedAt = DateTime.Now
                });
                return TheFollower(Render<ServerPatchSection>(p => p
                    .Add(c => c.ServerId, ServerId).Add(c => c.PatchManagementAvailable, true)));
            case "ports":
                _handler.SetPaginatedJsonResponse(HttpMethod.Get, "/projects", new List<ProjectDto>());
                _handler.SetJsonResponse(HttpMethod.Get, "/ports/range", new PortRangeDto
                {
                    From = PortRegistryLimits.DefaultRangeFrom,
                    To = PortRegistryLimits.DefaultRangeTo,
                    IsExplicit = false
                });
                _handler.SetJsonResponse(HttpMethod.Get, "/ports", new List<PortReservationDto>());
                _handler.SetJsonResponse(HttpMethod.Post, "/ports/check", new PortCheckResultDto { ServerId = ServerId });
                return TheFollower(Render<ServerPortsSection>(p => p
                    .Add(c => c.ServerId, ServerId).Add(c => c.PortObservationAvailable, true).Add(c => c.AgentOnline, true)));
            case "services":
                return TheFollower(Render<ServerServicesSection>(p => p.Add(x => x.Server, Server()).Add(x => x.ServerId, ServerId)));
            case "apache":
                return TheFollower(Render<ServerApacheSection>(p => p.Add(x => x.ServerId, ServerId).Add(x => x.Server, Server(s =>
                    s with { Apache = new ApacheDataDto { IsInstalled = true, IsRunning = true, VirtualHosts = [], Modules = [] } }))));
            case "cron":
                return TheFollower(Render<ServerCronSection>(p => p.Add(x => x.ServerId, ServerId).Add(x => x.Server, Server(s =>
                    s with { Cron = new CronDataDto { Jobs = [] } }))));
            case "docker":
                foreach (var path in new[] { "containers", "images" })
                    _handler.SetJsonResponse($"api/servers/{ServerId}/docker/{path}", "[]");
                return TheFollower(Render<ServerDockerSection>(p => p
                    .Add(x => x.Server, Server(s => s with { Docker = new DockerDataDto { Containers = [] } }))
                    .Add(x => x.ServerId, ServerId)
                    .Add(x => x.DockerInitialLoaded, true)));
            case "mail":
                _handler.SetJsonResponse($"api/servers/{ServerId}/mail/domains", new PaginatedResult<MailDomainDto>());
                _handler.SetJsonResponse($"api/servers/{ServerId}/mail/accounts", new PaginatedResult<MailAccountDto>());
                _handler.SetJsonResponse($"api/servers/{ServerId}/mail/aliases", new PaginatedResult<MailAliasDto>());
                _handler.SetJsonResponse($"api/servers/{ServerId}/mail/diagnostics", new MailDiagnosticsDto());
                return TheFollower(Render<ServerMailSection>(p => p
                    .Add(x => x.Server, ServerTestData.MakeMailServer() with { Id = ServerId }).Add(x => x.ServerId, ServerId)));
            case "portsentry":
                _handler.SetJsonResponse($"api/servers/{ServerId}/portsentry/filter-values", new PortsentryFilterValuesDto());
                _handler.SetJsonResponse($"api/servers/{ServerId}/portsentry/whitelist", new PaginatedResult<PortsentryWhitelistIpDto>());
                _handler.SetJsonResponse($"api/servers/{ServerId}/portsentry/blocked", new PaginatedResult<PortsentryBlockedIpDto>());
                return TheFollower(Render<ServerPortsentrySection>(p => p
                    .Add(x => x.ServerId, ServerId).Add(x => x.Ps, new PortsentryDataDto { IsInstalled = true, IsRunning = true })));
            case "rkhunter":
                _handler.SetJsonResponse($"api/servers/{ServerId}/rkhunter/warnings", new List<RkhunterWarningDto>());
                _handler.SetJsonResponse($"api/servers/{ServerId}/rkhunter/history", new List<RkhunterScanResultDto>());
                _handler.SetJsonResponse($"api/servers/{ServerId}/rkhunter/schedule", true);
                return TheFollower(Render<ServerRkhunterSection>(p => p
                    .Add(x => x.ServerId, ServerId)
                    .Add(x => x.Rk, new RkhunterDataDto { IsInstalled = true, Version = "1.4.6", ScanScheduleCron = "0 3 * * *" })));
            case "teamspeak":
                var ts = new TeamspeakDataDto { IsInstalled = true, IsRunning = true, Version = "3.13.7", Clients = [], Channels = [], Bans = [] };
                _handler.SetJsonResponse($"api/servers/{ServerId}/teamspeak", ts);
                _handler.SetPaginatedJsonResponse($"api/servers/{ServerId}/teamspeak/clients", Array.Empty<TeamspeakClientDto>());
                _handler.SetPaginatedJsonResponse($"api/servers/{ServerId}/teamspeak/channels", Array.Empty<TeamspeakChannelDto>());
                _handler.SetPaginatedJsonResponse($"api/servers/{ServerId}/teamspeak/bans", Array.Empty<TeamspeakBanDto>());
                return TheFollower(Render<ServerTeamspeakSection>(p => p
                    .Add(x => x.Server, Server(s => s with { Teamspeak = ts })).Add(x => x.ServerId, ServerId)));
            default:
                throw new ArgumentOutOfRangeException(nameof(page), page, null);
        }
    }

    [Theory]
    [InlineData("firewall", "Firewall allow 8080/tcp", "FirewallAllow 8080/tcp")]
    [InlineData("firewall", "Firewall deny 25/tcp", "FirewallDeny 25/tcp")]
    [InlineData("firewall", "Firewall delete 8080/tcp", "Delete 8080/tcp")]
    [InlineData("firewall", "Firewall enable", "FirewallTitle: FirewallEnable")]
    [InlineData("firewall", "Firewall disable", "FirewallTitle: FirewallDisable")]
    [InlineData("patch", ServerTaskNames.PatchCheck, "PatchCheck")]
    [InlineData("patch", ServerTaskNames.PatchUpgrade, "PatchApply")]
    [InlineData("ports", ServerTaskNames.PortObservation, "PortScanRefresh")]
    [InlineData("services", "Service Restart - nginx", "Restart - nginx")]
    [InlineData("services", "Install - nginx", "Install - nginx")]
    [InlineData("services", "Uninstall - nginx", "Uninstall - nginx")]
    [InlineData("apache", "Apache Restart", "Restart")]
    [InlineData("apache", "Apache EnableSite - a.conf", "Enable a.conf")]
    [InlineData("apache", "Apache DisableModule - rewrite", "Disable rewrite")]
    [InlineData("apache", "Apache DeleteSite - a.conf", "Delete a.conf")]
    [InlineData("apache", "Apache save config - a.conf", "Save")]
    [InlineData("apache", "Apache logs - error", "ViewLogs")]
    [InlineData("cron", "Cron save - root", "Save")]
    [InlineData("cron", "Cron delete - j1", "DeleteCronJob")]
    [InlineData("docker", "Docker prune", "DockerPrune")]
    [InlineData("docker", "Docker Restart - abc123def456", "Restart abc123def456")]
    [InlineData("docker", "Compose Up - monitoring", "DockerCompose Start monitoring")]
    [InlineData("docker", "Compose Down - monitoring", "DockerCompose Stop monitoring")]
    [InlineData("docker", "Docker pull - nginx:latest", "DockerPull nginx:latest")]
    [InlineData("docker", "Docker rmi - abc123def456", "Delete abc123def456")]
    [InlineData("docker", "Docker update limits - abc123def456", "ResourceLimits")]
    [InlineData("docker", "Docker compose deploy - app", "Deploy app")]
    [InlineData("docker", "Docker build - app:1", "DockerBuild app:1")]
    [InlineData("docker", "Docker exec - abc123def456", "DockerShell abc123def456")]
    [InlineData("mail", "Mail - add domain example.org", "AddDomain example.org")]
    [InlineData("mail", "Mail - remove domain example.org", "DeleteDomain")]
    [InlineData("mail", "Mail - add account bob@example.org", "AddAccount bob@example.org")]
    [InlineData("mail", "Mail - delete account bob@example.org", "DeleteAccount")]
    [InlineData("mail", "Mail - alias a@example.org → b@example.org", "AddAlias a@example.org")]
    [InlineData("mail", "Mail - remove alias a@example.org", "DeleteAlias")]
    [InlineData("mail", "Mail - rotate DKIM key for example.org (mail → mail2)", "RotateDkimKey example.org")]
    [InlineData("mail", "Mail - prepare the administrator access", "MailMasterAccessPrepare")]
    [InlineData("mail", "Mail - install spam filter (rspamd)", "MailSpamFilter")]
    [InlineData("mail", "Mail - spam thresholds", "MailSpamThresholds")]
    [InlineData("mail", "Mail - learn spam", "MailLearnTitle")]
    [InlineData("mail", "Mail - RestartSpamFilter", "MailSpamFilter")]
    [InlineData("portsentry", "PortSentry - Restart", "Restart")]
    [InlineData("portsentry", "PortSentry - logs", "ViewLogs")]
    [InlineData("portsentry", "PortSentry - unblock 203.0.113.7", "Unblock 203.0.113.7")]
    [InlineData("portsentry", "PortSentry - setup", "PortsentrySetup")]
    [InlineData("rkhunter", "RKHunter - RunScan", "RunScan")]
    [InlineData("rkhunter", "RKHunter - UpdateDatabase", "UpdateDatabase")]
    [InlineData("rkhunter", "RKHunter - scheduled scan", "RunScan")]
    [InlineData("rkhunter", "RKHunter - logs", "ViewLogs")]
    [InlineData("rkhunter", "RKHunter - setup", "RkhunterSetup")]
    [InlineData("teamspeak", "TeamSpeak - Restart", "Restart")]
    [InlineData("teamspeak", "TeamSpeak - install", "TeamspeakSetup")]
    [InlineData("teamspeak", "TeamSpeak - logs", "Logs")]
    [InlineData("teamspeak", "TeamSpeak - kick client 4", "KickClient")]
    [InlineData("teamspeak", "TeamSpeak - ban abcDEF=", "BanClient")]
    [InlineData("teamspeak", "TeamSpeak - graceful restart (60s warn)", "GracefulRestart")]
    [InlineData("teamspeak", "TeamSpeak - create channel Lobby", "CreateChannel")]
    [InlineData("teamspeak", "TeamSpeak - unban 12", "Unban")]
    [InlineData("teamspeak", "TeamSpeak - global message", "SendMessage")]
    public void R12_AnAdoptedTask_IsNamedAsThePageNamesItsOwnLaunch(string page, string rawTaskName, string expectedTitle)
    {
        Tracker.Upsert(new ServerTaskDto { Id = TaskId, ServerId = ServerId, Name = rawTaskName, Status = TaskExecutionStatus.Running });

        var follower = Render(page);

        Assert.Equal(expectedTitle, follower.Find(".server-task-follower strong").TextContent.Trim());
    }

    [Theory]
    [InlineData("services", "Service Restart - nginx", "Redémarrer - nginx")]
    [InlineData("services", "Install - nginx", "Installer - nginx")]
    [InlineData("services", "Uninstall - nginx", "Désinstaller - nginx")]
    [InlineData("docker", "Docker Restart - abc123def456", "Redémarrer abc123def456")]
    [InlineData("docker", "Docker Remove - abc123def456", "Supprimer abc123def456")]
    [InlineData("docker", "Compose Up - monitoring", "Compose Démarrer monitoring")]
    public void R12_TheVerbOfAnAdoptedTask_IsInTheReadersLanguage(string page, string rawTaskName, string expectedTitle)
    {
        Services.AddSingleton<IStringLocalizer<AppStrings>>(ResxLocalizer.French());
        Tracker.Upsert(new ServerTaskDto { Id = TaskId, ServerId = ServerId, Name = rawTaskName, Status = TaskExecutionStatus.Running });

        var follower = Render(page);

        Assert.Equal(expectedTitle, follower.Find(".server-task-follower strong").TextContent.Trim());
    }

    [Fact]
    public void R12_AnAdoptedTaskWithANameThePageDoesNotKnow_KeepsItsRawName()
    {
        Tracker.Upsert(new ServerTaskDto { Id = TaskId, ServerId = ServerId, Name = "Apache something new", Status = TaskExecutionStatus.Running });

        var follower = Render("apache");

        Assert.Equal("Apache something new", follower.Find(".server-task-follower strong").TextContent.Trim());
    }

    [Fact]
    public void R12_WhileAnAdoptedApacheTaskRuns_NoActionOfThePageCanBeLaunched()
    {
        Tracker.Upsert(new ServerTaskDto { Id = TaskId, ServerId = ServerId, Name = "Apache Restart", Status = TaskExecutionStatus.Running });
        var apache = new ApacheDataDto
        {
            IsInstalled = true,
            IsRunning = true,
            Modules = [],
            VirtualHosts = [new ApacheVirtualHostDto { ServerName = "a.test", ConfigFile = "/etc/apache2/sites-available/a.conf", IsEnabled = true, Port = 80 }]
        };
        var cut = Render<ServerApacheSection>(p => p.Add(x => x.ServerId, ServerId).Add(x => x.Server, Server(s => s with { Apache = apache })));

        cut.WaitForAssertion(() => Assert.True(cut.Find("button[title^='Disable a.conf']").HasAttribute("disabled")), TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("firewall", "Firewall enable", "FirewallDisable")]
    [InlineData("patch", ServerTaskNames.PatchUpgrade, "PatchApply")]
    [InlineData("ports", ServerTaskNames.PortObservation, "PortScanRefresh")]
    public void R12_WhileAnAdoptedTaskRuns_TheLaunchButtonOfThePageIsDisabled(string page, string rawTaskName, string buttonText)
    {
        Tracker.Upsert(new ServerTaskDto { Id = TaskId, ServerId = ServerId, Name = rawTaskName, Status = TaskExecutionStatus.Running });

        var cut = RenderWithHeader(page);

        cut.WaitForAssertion(() => Assert.True(LaunchButton(cut, buttonText).HasAttribute("disabled")), TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("firewall", "FirewallDisable")]
    [InlineData("patch", "PatchApply")]
    [InlineData("ports", "PortScanRefresh")]
    public void R12_WithoutATask_TheLaunchButtonOfThePageIsEnabled(string page, string buttonText)
    {
        var cut = RenderWithHeader(page);

        Assert.False(LaunchButton(cut, buttonText).HasAttribute("disabled"));
    }

    private static AngleSharp.Dom.IElement LaunchButton(IRenderedComponent<ContainerFragment> cut, string text) =>
        cut.FindAll("button").First(button => button.TextContent.Trim() == text);

    // The launch buttons sit in the page header, which the hosting layout renders: an outlet stands in for it.
    private IRenderedComponent<ContainerFragment> RenderWithHeader(string page)
    {
        _handler.SetJsonResponse(HttpMethod.Get, "firewall", new ServerFirewallDto
        {
            Installed = true,
            Active = true,
            StatusKnown = true,
            FirewallManagementAvailable = true,
            Rules = []
        });
        _handler.SetJsonResponse(HttpMethod.Get, "security-updates", new ServerSecurityUpdatesDto
        {
            Known = true,
            PackageManagerPresent = true,
            PatchManagementAvailable = true,
            CheckedAt = DateTime.Now
        });
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "/projects", new List<ProjectDto>());
        _handler.SetJsonResponse(HttpMethod.Get, "/ports/range", new PortRangeDto
        {
            From = PortRegistryLimits.DefaultRangeFrom,
            To = PortRegistryLimits.DefaultRangeTo,
            IsExplicit = false
        });
        _handler.SetJsonResponse(HttpMethod.Get, "/ports", new List<PortReservationDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "/ports/check", new PortCheckResultDto { ServerId = ServerId });
        return Render(builder =>
        {
            builder.OpenComponent<SectionOutlet>(0);
            builder.AddAttribute(1, nameof(SectionOutlet.SectionName), PageHeaderActions.SectionName);
            builder.CloseComponent();
            switch (page)
            {
                case "firewall":
                    builder.OpenComponent<ServerFirewallSection>(2);
                    builder.AddAttribute(3, nameof(ServerFirewallSection.ServerId), ServerId);
                    builder.AddAttribute(4, nameof(ServerFirewallSection.FirewallManagementAvailable), true);
                    break;
                case "patch":
                    builder.OpenComponent<ServerPatchSection>(2);
                    builder.AddAttribute(3, nameof(ServerPatchSection.ServerId), ServerId);
                    builder.AddAttribute(4, nameof(ServerPatchSection.PatchManagementAvailable), true);
                    break;
                default:
                    builder.OpenComponent<ServerPortsSection>(2);
                    builder.AddAttribute(3, nameof(ServerPortsSection.ServerId), ServerId);
                    builder.AddAttribute(4, nameof(ServerPortsSection.PortObservationAvailable), true);
                    builder.AddAttribute(5, nameof(ServerPortsSection.AgentOnline), true);
                    break;
            }
            builder.CloseComponent();
        });
    }
}
