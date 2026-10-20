// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.AppBackups;
using Aetheus.Front.Components.Notifications;
using Aetheus.Front.Components.PackageFeeds;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using BackupsPage = Aetheus.Front.Components.AppBackups.Backups;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Recette R3-35 lot 2: clicking a row runs the row's default action in the grids that have a detail or
/// edit action (notification channels and rules, backup policies, Docker containers, TeamSpeak channels,
/// package feeds, the pipeline fleet). Each test clicks a cell of the row, not its button.
/// </summary>
public sealed class GridRowClickDefaultActionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public GridRowClickDefaultActionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    }

    private static AngleSharp.Dom.IElement RowCell<T>(IRenderedComponent<T> cut, string rowText) where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.FindAll(RowEventIsolation.ClickableRows)
            .First(row => row.TextContent.Contains(rowText, StringComparison.Ordinal))
            .QuerySelector("td")!;

    [Fact]
    public void NotificationChannel_RowClick_OpensItsEditDialog()
    {
        SeedNotifications();
        var cut = Render<NotificationsAdmin>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        RowCell(cut, "slack-ops").Click();

        cut.WaitForAssertion(() => Assert.Equal(typeof(NotificationChannelEditDialog), _dialog.LastComponent));
        Assert.Equal(1, _dialog.LastParameters!["ChannelId"]);
    }

    [Fact]
    public void NotificationRule_RowClick_OpensItsEditDialog()
    {
        SeedNotifications();
        var cut = Render<NotificationsAdmin>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        RowCell(cut, "alert.triggered").Click();

        cut.WaitForAssertion(() => Assert.Equal(typeof(NotificationRuleEditDialog), _dialog.LastComponent));
        Assert.Equal(1, ((NotificationRuleDto)_dialog.LastParameters!["Rule"]!).Id);
    }

    [Fact]
    public void Notifications_RowControls_StayIsolatedFromTheRow()
    {
        SeedNotifications();
        var cut = Render<NotificationsAdmin>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 4);
    }

    private void SeedNotifications()
    {
        _handler.SetJsonResponse("api/notifications/channels", new PaginatedResult<NotificationChannelDto>
        {
            Items = [new() { Id = 1, Name = "slack-ops", Type = NotificationChannelType.Slack, IsEnabled = true, RuleCount = 1 }],
            TotalCount = 1,
            Page = 1,
            PageSize = 25
        });
        _handler.SetJsonResponse("api/notifications/rules", new PaginatedResult<NotificationRuleDto>
        {
            Items = [new() { Id = 1, NotificationChannelId = 1, ChannelName = "slack-ops", EventType = "alert.triggered", IsEnabled = true }],
            TotalCount = 1,
            Page = 1,
            PageSize = 25
        });
    }

    [Fact]
    public void BackupPolicy_RowClick_OpensItsEditForm()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "aetheus" }],
            TotalCount = 1,
            Page = 1,
            PageSize = 100
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/servers", new PaginatedResult<ServerDto>
        {
            Items = [new ServerDto { Id = 2, Name = "vps-1" }],
            TotalCount = 1,
            Page = 1,
            PageSize = 100
        });
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/backups", new[]
        {
            new BackupPolicyDto
            {
                Id = 1, Name = "nightly-db", Enabled = true, ProjectId = 1, ServerId = 2, DbEngine = BackupDbEngine.None,
                FilePaths = ["/srv/app"], ScheduleCron = "0 3 * * *", RetentionCount = 14
            }
        });
        var cut = Render<BackupsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));
        Assert.Empty(cut.FindAll("input#oe-pages-backups-backups-1"));

        RowCell(cut, "nightly-db").Click();

        cut.WaitForAssertion(() => Assert.Equal("nightly-db", cut.Find("input#oe-pages-backups-backups-1").GetAttribute("value")));
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 4);
    }

    [Fact]
    public void PackageFeed_RowClick_OpensItsPackages()
    {
        _handler.SetJsonResponse("api/package-feeds", new PaginatedResult<PackageFeedDto>
        {
            Items = [new() { Id = 5, Name = "corp-nuget", FeedType = PackageFeedType.NuGet, UpstreamUrl = "https://api.nuget.org", PackageCount = 2 }],
            TotalCount = 1,
            Page = 1,
            PageSize = 25
        });
        var cut = Render<PackageFeedsAdmin>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        RowCell(cut, "corp-nuget").Click();

        cut.WaitForAssertion(() => Assert.Equal(typeof(PackageFeedPackagesDialog), _dialog.LastComponent));
        Assert.Equal(5, _dialog.LastParameters!["FeedId"]);
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 2);
    }

    [Fact]
    public void PipelineFleet_RowClick_OpensThePipeline()
    {
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto> { new() { Id = 7, Name = "ci", Version = 2 } });
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [new ProjectDto { Id = 4, Name = "Toto" }], TotalCount = 1 });
        _handler.SetJsonResponse("api/pipelines/fleet", new PaginatedResult<PipelineFleetItemDto>
        {
            TotalCount = 1,
            Items = [new() { PipelineId = 9, PipelineName = "deploy-site", TemplateName = "ci", PinnedVersion = 1, LatestVersion = 2, Freshness = PipelineFleetFreshness.Outdated }]
        });
        var cut = Render<PipelineFleet>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows));
        RowCell(cut, "deploy-site").Click();

        Assert.EndsWith("/pipelines/9", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void DockerContainer_RowClick_InspectsIt_AndDoesNothingWhenTheAgentIsOffline()
    {
        var container = new DockerContainerDto { ContainerId = "abc123", Name = "web", Image = "nginx", State = "running", Status = "Up", Project = "site" };
        _handler.SetJsonResponse("api/servers/10/docker/containers/abc123/inspect", new { });
        var cut = Render<DockerContainersTab>(p => p
            .Add(x => x.Server, DockerServer(container, ServerStatus.Online))
            .Add(x => x.ServerId, 10)
            .Add(x => x.InitialLoaded, true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 2); // R3-42: Restart and the row menu trigger

        RowCell(cut, "web").Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r =>
            r.Method == "POST" && r.Url.Contains("docker/containers/abc123/inspect", StringComparison.Ordinal)));

        var offline = Render<DockerContainersTab>(p => p
            .Add(x => x.Server, DockerServer(container, ServerStatus.Offline))
            .Add(x => x.ServerId, 10)
            .Add(x => x.InitialLoaded, true));
        var before = _handler.Requests.Count(r => r.Method == "POST");
        RowCell(offline, "web").Click();
        Assert.Equal(before, _handler.Requests.Count(r => r.Method == "POST"));
    }

    private static ServerDetailDto DockerServer(DockerContainerDto container, ServerStatus status) => new()
    {
        Id = 10,
        Name = "srv",
        Hostname = "10.0.0.10",
        Status = status,
        Tags = [],
        Services = [],
        Docker = new DockerDataDto { Containers = [container], Images = [], ComposeStacks = [], Networks = [], Volumes = [] }
    };

    [Fact]
    public void TeamspeakChannel_RowClick_OpensItsEditDialog()
    {
        const int serverId = 5;
        var channel = new TeamspeakChannelDto { Id = 2, Name = "Gaming", ParentId = 0, TotalClients = 0, MaxClients = 10, IsPermanent = true };
        var server = new ServerDetailDto
        {
            Id = serverId,
            Name = "ts-srv",
            Hostname = "10.0.0.5",
            Type = ServerType.Normal,
            Status = ServerStatus.Online,
            Tags = ["teamspeak"],
            Services = [],
            Teamspeak = new TeamspeakDataDto { IsInstalled = true, IsRunning = true, Version = "3.13.7", Clients = [], Channels = [channel], Bans = [] }
        };
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak", server.Teamspeak with { Clients = [], Channels = [], Bans = [] });
        _handler.SetPaginatedJsonResponse($"api/servers/{serverId}/teamspeak/clients", Array.Empty<TeamspeakClientDto>());
        _handler.SetPaginatedJsonResponse($"api/servers/{serverId}/teamspeak/channels", new[] { channel });
        _handler.SetPaginatedJsonResponse($"api/servers/{serverId}/teamspeak/bans", Array.Empty<TeamspeakBanDto>());
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"/servers/{serverId}/teamspeak?tab=channels");
        var cut = Render<ServerTeamspeakSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, serverId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 2);

        RowCell(cut, "Gaming").Click();

        cut.WaitForAssertion(() => Assert.True(_dialog.OpenCount >= 1));
        Assert.Equal("EditChannel", _dialog.LastTitle);
    }
}
