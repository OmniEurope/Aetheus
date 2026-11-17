// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R-12, mail and Docker pages: leaving the page and coming back while an action runs shows it again. The mail
/// page finds again its domain, account, alias and DKIM tasks (and the spam filter and administrator access ones) by
/// the names the backend gives them, but never the setup and the leftovers check (their own components follow those)
/// nor the purge (the reinstall component follows it). The Docker page finds its actions again, never its reads (a
/// log, an inspect, the environment, a listing), which last seconds and must not veil the page on arrival.
/// </summary>
public sealed class ServerTaskAdoptionMailDockerTests : BunitContext
{
    private const int MailServerId = 10;
    private const int DockerServerId = 20;

    private readonly BunitTestHelper.TestHandler _handler;

    public ServerTaskAdoptionMailDockerTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    private TaskTrackerService Tracker => Services.GetRequiredService<TaskTrackerService>();

    private static ServerTaskDto InFlight(int serverId, int id, string name, TaskExecutionStatus status = TaskExecutionStatus.Running) =>
        new() { Id = id, ServerId = serverId, Name = name, Status = status };

    private IRenderedComponent<ServerMailSection> RenderMail()
    {
        _handler.SetJsonResponse($"api/servers/{MailServerId}/mail/domains", new PaginatedResult<MailDomainDto>
        {
            Items = [new() { Id = 1, Name = "example.com", IsActive = true, DkimSelector = "default" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse($"api/servers/{MailServerId}/mail/accounts", new PaginatedResult<MailAccountDto>
        {
            Items = [new() { Id = 1, Email = "admin@example.com", IsActive = true, QuotaMb = 1024 }],
            TotalCount = 1
        });
        _handler.SetJsonResponse($"api/servers/{MailServerId}/mail/aliases", new PaginatedResult<MailAliasDto>());
        _handler.SetJsonResponse($"api/servers/{MailServerId}/mail/diagnostics", new MailDiagnosticsDto());
        var server = ServerTestData.MakeMailServer(true, true, true) with { MailSetupAvailable = true };
        return Render<ServerMailSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, MailServerId));
    }

    private IRenderedComponent<ServerDockerSection> RenderDocker()
    {
        foreach (var path in new[] { "containers", "images" })
            _handler.SetJsonResponse($"api/servers/{DockerServerId}/docker/{path}", "[]");
        var server = new ServerDetailDto
        {
            Id = DockerServerId,
            Name = "docker-srv",
            Hostname = "10.0.0.20",
            Type = ServerType.Docker,
            Status = ServerStatus.Online,
            Tags = [],
            Services = [],
            Docker = new DockerDataDto()
        };
        return Render<ServerDockerSection>(p => p
            .Add(x => x.Server, server)
            .Add(x => x.ServerId, DockerServerId)
            .Add(x => x.DockerInitialLoaded, true));
    }

    private static IReadOnlyList<IRenderedComponent<ServerTaskFollower>> Followers(IRenderedComponent<ServerMailSection> cut) =>
        cut.FindComponents<ServerTaskFollower>();

    private static IReadOnlyList<IRenderedComponent<ServerTaskFollower>> Followers(IRenderedComponent<ServerDockerSection> cut) =>
        cut.FindComponents<ServerTaskFollower>();

    // The names are the ones the backend gives the tasks (MailService, MailOperationsService, MailMasterAccessService).
    [Theory]
    [InlineData("Mail - add domain example.org")]
    [InlineData("Mail - remove domain example.org")]
    [InlineData("Mail - add account bob@example.com")]
    [InlineData("Mail - delete account bob@example.com")]
    [InlineData("Mail - alias a@example.com → b@example.com")]
    [InlineData("Mail - remove alias a@example.com")]
    [InlineData("Mail - rotate DKIM key for example.com (default → s2)")]
    [InlineData("Mail - prepare the administrator access")]
    [InlineData("Mail - install spam filter (rspamd)")]
    [InlineData("Mail - spam thresholds")]
    [InlineData("Mail - StartSpamFilter")]
    [InlineData("Mail - RestartSpamFilter")]
    [InlineData("Mail - StopSpamFilter")]
    [InlineData("Mail - learn spam")]
    [InlineData("Mail - learn ham")]
    public void R12_TheMailPage_FollowsAMailActionAlreadyRunning(string name)
    {
        Tracker.Upsert(InFlight(MailServerId, 71001, name));

        var cut = RenderMail();

        var follower = Assert.Single(Followers(cut), f => f.Instance.TaskId == 71001);
        // R-12: the follower shows the page's own name for the task, never the raw backend one (exact titles:
        // ServerAdoptedTaskTitleTests).
        var title = follower.Find(".server-task-follower strong").TextContent.Trim();
        Assert.False(string.IsNullOrWhiteSpace(title));
        Assert.NotEqual(name, title);
    }

    [Theory]
    [InlineData("Mail - full setup (example.com)")]
    [InlineData("Mail - leftovers check")]
    [InlineData("Mail - erase the mail stack (reinstall)")]
    [InlineData("Mail - mailbox usage")]
    [InlineData("Mail - read DKIM key (example.com)")]
    [InlineData("Mail logs - mail")]
    public void R12_TheMailPageFollower_DoesNotTakeATaskFollowedElsewhereOrOnlyRead(string name)
    {
        Tracker.Upsert(InFlight(MailServerId, 71002, name));

        var cut = RenderMail();

        Assert.All(Followers(cut), f => Assert.Null(f.Instance.TaskId));
    }

    [Fact]
    public void R12_TheMailPage_DoesNotFollowAMailTaskOfAnotherServer()
    {
        Tracker.Upsert(InFlight(99, 71003, "Mail - add domain example.org"));

        var cut = RenderMail();

        Assert.All(Followers(cut), f => Assert.Null(f.Instance.TaskId));
    }

    [Fact]
    public void R12_TheMailReinstall_FindsThePurgeAgainWhenTheUserComesBack()
    {
        Tracker.Upsert(InFlight(MailServerId, 71004, "Mail - erase the mail stack (reinstall)"));

        var cut = RenderMail();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".mail-reinstall-running")));
        Assert.True(cut.FindComponent<ServerMailReinstall>().Instance.Erasing);
    }

    [Fact]
    public void R12_TheMailReinstall_FindsThePurgeAgainWhenItEntersTheFeedAfterArrival()
    {
        var cut = RenderMail();
        Assert.False(cut.FindComponent<ServerMailReinstall>().Instance.Erasing);

        Tracker.Upsert(InFlight(MailServerId, 71005, "Mail - erase the mail stack (reinstall)", TaskExecutionStatus.Pending));

        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<ServerMailReinstall>().Instance.Erasing));
    }

    [Fact]
    public void R12_TheMailReinstall_IgnoresAPurgeThatEndedOrBelongsToAnotherServer()
    {
        Tracker.Upsert(InFlight(99, 71006, "Mail - erase the mail stack (reinstall)"));
        Tracker.Upsert(InFlight(MailServerId, 71007, "Mail - erase the mail stack (reinstall)", TaskExecutionStatus.Success));

        var cut = RenderMail();

        Assert.False(cut.FindComponent<ServerMailReinstall>().Instance.Erasing);
        Assert.Empty(cut.FindAll(".mail-reinstall-running"));
    }

    [Theory]
    [InlineData("Docker prune")]
    [InlineData("Docker restart - abc123def456")]
    [InlineData("Docker compose deploy - monitoring")]
    [InlineData("Docker pull - nginx:latest")]
    [InlineData("Docker exec - abc123def456")]
    public void R12_TheDockerPage_FollowsAnActionAlreadyRunning(string name)
    {
        Tracker.Upsert(InFlight(DockerServerId, 72001, name));

        var cut = RenderDocker();

        Assert.Single(Followers(cut), f => f.Instance.TaskId == 72001);
    }

    [Theory]
    [InlineData("Docker logs - abc123def456")]
    [InlineData("Docker inspect - abc123def456")]
    [InlineData("Docker env - abc123def456")]
    [InlineData("Docker ls - abc123def456")]
    [InlineData("Docker compose file - monitoring")]
    public void R12_TheDockerPage_DoesNotAdoptAReadWhichWouldVeilItOnArrival(string name)
    {
        Tracker.Upsert(InFlight(DockerServerId, 72002, name));

        var cut = RenderDocker();

        Assert.All(Followers(cut), f =>
        {
            Assert.Null(f.Instance.TaskId);
            Assert.Equal(ServerTaskFollowState.Idle, f.Instance.State);
        });
    }

    [Fact]
    public void R12_TheDockerPage_StillFollowsTheActionWhenAReadRunsBesideIt()
    {
        Tracker.Upsert(InFlight(DockerServerId, 72003, "Docker logs - abc123def456"));
        Tracker.Upsert(InFlight(DockerServerId, 72004, "Docker prune"));

        var cut = RenderDocker();

        Assert.Single(Followers(cut), f => f.Instance.TaskId == 72004);
        Assert.DoesNotContain(Followers(cut), f => f.Instance.TaskId == 72003);
    }
}
