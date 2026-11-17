// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Bunit.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R-12, the mail status list: its own actions (the services of Postfix and Dovecot, the OpenDKIM restart,
/// the server check, the certificate, the delivery test and the mailbox usage) are tracked by task id from the moment
/// they are queued, so leaving the mail page and coming back showed nothing of them while they still ran. The list now
/// finds them again in the live task feed by the names the backend gives them: its banner names them, their buttons
/// stay locked (no second launch) and their outcome is read when they end. A task that ended while the user was away
/// is not in the feed and never shows as running; the tasks other parts of the page follow are not taken.
/// </summary>
public sealed class ServerMailStatusAdoptionTests : BunitContext
{
    private const int ServerId = 10;

    private readonly BunitTestHelper.TestHandler _handler;

    public ServerMailStatusAdoptionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse($"api/servers/{ServerId}/mail/diagnostics", new MailDiagnosticsDto
        {
            CheckedAt = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc),
            Items = [new() { Key = "services", Verdict = MailCheckVerdict.Ok }, new() { Key = "tls", Verdict = MailCheckVerdict.Ok }]
        });
    }

    private TaskTrackerService Tracker => Services.GetRequiredService<TaskTrackerService>();

    private static ServerTaskDto InFlight(int id, string name, int serverId = ServerId, int? pipelineRunId = null,
        TaskExecutionStatus status = TaskExecutionStatus.Running) =>
        new() { Id = id, ServerId = serverId, Name = name, Status = status, PipelineRunId = pipelineRunId };

    private IRenderedComponent<ContainerFragment>? _host;
    private IRenderedComponent<ContainerFragment> Host => _host ?? throw new InvalidOperationException("Render the list first.");

    private IRenderedComponent<ServerMailStatusList> RenderList()
    {
        _host = this.RenderWithOutlets<ServerMailStatusList>(new Dictionary<string, object?>
        {
            [nameof(ServerMailStatusList.ServerId)] = ServerId,
            [nameof(ServerMailStatusList.Mail)] = new MailDataDto
            {
                IsInstalled = true,
                IsManagedByAetheus = true,
                Hostname = "mail.example.com",
                HelperVersion = ServerMailStatusList.CurrentHelperVersion
            },
            [nameof(ServerMailStatusList.CanManage)] = true,
            [nameof(ServerMailStatusList.DefaultSender)] = "admin@example.com"
        }, ServerMailReinstall.HeaderMenuSection);
        return Host.FindComponent<ServerMailStatusList>();
    }

    private int PostsToTheActionEndpoint => _handler.Requests.Count(r => r.Method == "POST" && r.Url.EndsWith("mail/action", StringComparison.Ordinal));

    // The names are the ones the backend gives the tasks (MailService.ExecuteActionAsync, MailOperationsService).
    [Theory]
    [InlineData("Mail - RestartPostfix", "Postfix")]
    [InlineData("Mail - StopPostfix", "Postfix")]
    [InlineData("Mail - ReloadDovecot", "Dovecot")]
    [InlineData("Mail - StartDovecot", "Dovecot")]
    [InlineData("Mail - RestartOpenDkim", "OpenDkim")]
    [InlineData("Mail - TestConfig", "ServerCheck")]
    [InlineData("Mail - TLS certificate (mail.example.com)", "ObtainCertificate")]
    [InlineData("Mail - delivery test to bob@example.org", "DeliveryTest")]
    [InlineData("Mail - mailbox usage", "Usage")]
    public void R12_TheStatusList_FollowsOneOfItsActionsAlreadyRunning_OnArrival(string name, string kindName)
    {
        var kind = Enum.Parse<ServerMailStatusList.MailTaskKind>(kindName);
        Tracker.Upsert(InFlight(74001, name));

        var cut = RenderList();

        var running = Assert.Single(cut.Instance.RunningTasks);
        Assert.Equal(74001, running.Key);
        Assert.Equal(kind, running.Value.Kind);
        Assert.True(cut.Instance.IsBusy(kind));
        // One banner at the top names it; R-37: a mail task is not blocking, nothing is veiled.
        Assert.Contains("MailTaskRunning", Host.Find(".mail-task-running").TextContent, StringComparison.Ordinal);
        Assert.False(Services.GetRequiredService<BusyVeil>().IsBusy);
    }

    [Theory]
    [InlineData("Mail - queue listing")]
    [InlineData("Mail logs - postfix")]
    [InlineData("Mail - read DKIM key (example.com)")]
    [InlineData("Mail - StartSpamFilter")]
    [InlineData("Mail - add domain example.org")]
    [InlineData("Mail - full setup (example.com)")]
    [InlineData("Mail - erase the mail stack (reinstall)")]
    [InlineData("Apache restart")]
    public void R12_TheStatusList_DoesNotTakeATaskFollowedElsewhere(string name)
    {
        Tracker.Upsert(InFlight(74002, name));

        var cut = RenderList();

        Assert.Empty(cut.Instance.RunningTasks);
        Assert.Empty(Host.FindAll(".mail-task-running"));
    }

    [Fact]
    public void R12_TheStatusList_DoesNotTakeATaskOfAnotherServerOrOfAPipelineRun()
    {
        Tracker.Upsert(InFlight(74003, "Mail - RestartPostfix", serverId: 99));
        Tracker.Upsert(InFlight(74004, "Mail - RestartDovecot", pipelineRunId: 5));

        var cut = RenderList();

        Assert.Empty(cut.Instance.RunningTasks);
    }

    [Fact]
    public void R12_ATaskThatEndedWhileTheUserWasAway_NeverShowsAsRunning()
    {
        // A task that ended is not in the live feed; one the feed still lists as ended is not in flight either.
        Tracker.Upsert(InFlight(74005, "Mail - RestartPostfix", status: TaskExecutionStatus.Success));
        Tracker.Upsert(InFlight(74006, "Mail - TestConfig", status: TaskExecutionStatus.Failed));

        var cut = RenderList();

        Assert.Empty(cut.Instance.RunningTasks);
        Assert.False(cut.Instance.IsBusy(ServerMailStatusList.MailTaskKind.Postfix));
        Assert.Empty(Host.FindAll(".mail-task-running"));
    }

    [Fact]
    public void R12_AnActionThatEntersTheFeedAfterArrival_IsFollowedToo()
    {
        var cut = RenderList();
        Assert.Empty(cut.Instance.RunningTasks);

        Tracker.Upsert(InFlight(74007, "Mail - RestartOpenDkim", status: TaskExecutionStatus.Pending));

        cut.WaitForAssertion(() => Assert.Equal(ServerMailStatusList.MailTaskKind.OpenDkim, cut.Instance.RunningTasks[74007].Kind));
    }

    [Fact]
    public void R12_AnAdoptedOpenDkimRestart_KeepsItsMenuItemLocked_SoItIsNotLaunchedTwice()
    {
        Tracker.Upsert(InFlight(74008, "Mail - RestartOpenDkim"));
        var cut = RenderList();

        Host.Find(".mail-actions-menu .omni-overflow-menu__trigger").Click();
        var item = Host.FindAll(".omni-overflow-menu__popup .omni-menu__item")
            .First(i => i.TextContent.Contains("MailRestartOpenDkim", StringComparison.Ordinal));
        Assert.True(item.HasAttribute("disabled") || item.GetAttribute("aria-disabled") == "true");
        item.Click();

        Assert.Equal(0, PostsToTheActionEndpoint);
        Assert.Single(cut.Instance.RunningTasks);
    }

    [Fact]
    public void R12_AnAdoptedPostfixAction_KeepsTheServicesButtonBusy_AndRefusesAClick()
    {
        Tracker.Upsert(InFlight(74009, "Mail - RestartPostfix"));
        var cut = RenderList();
        var services = cut.FindComponents<OmniSplitButton>().Single(b => b.Instance.MenuLabel == "MailServiceActions");

        Assert.True(services.Instance.Busy);
        services.Find("button").Click();

        Assert.Equal(0, PostsToTheActionEndpoint);
    }

    [Fact]
    public void R12_AnAdoptedServiceStart_AlsoLocksTheStartServicesFix_SoTheStoppedServicesAreNotStartedTwice()
    {
        _handler.SetJsonResponse($"api/servers/{ServerId}/mail/diagnostics", new MailDiagnosticsDto
        {
            CheckedAt = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc),
            Items = [new() { Key = "services", Verdict = MailCheckVerdict.Mismatch, Detail = "postfix,opendkim" }]
        });
        Tracker.Upsert(InFlight(74010, "Mail - StartPostfix"));
        var cut = RenderList();
        cut.WaitForAssertion(() => Assert.Contains("MailStartServices", cut.Markup, StringComparison.Ordinal));
        var fix = cut.FindAll(".mail-status-fix button").Single(b => b.TextContent.Contains("MailStartServices", StringComparison.Ordinal));

        Assert.Equal("true", fix.GetAttribute("aria-busy"));
        fix.Click();

        Assert.Equal(0, PostsToTheActionEndpoint);
    }

    [Fact]
    public async Task R12_AnAdoptedTask_LeavesTheBannerAndFreesItsButton_WhenItEnds()
    {
        Tracker.Upsert(InFlight(74011, "Mail - RestartOpenDkim"));
        var cut = RenderList();
        Assert.Single(cut.Instance.RunningTasks);

        // The feed drops the task, then every listener learns of its end (the mail section forwards it).
        Tracker.HandleTerminal(new TaskCompletedNotification
        {
            TaskId = 74011,
            ServerId = ServerId,
            TaskName = "Mail - RestartOpenDkim",
            Status = TaskExecutionStatus.Success
        });
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(new TaskCompletedNotification
        {
            TaskId = 74011,
            ServerId = ServerId,
            TaskName = "Mail - RestartOpenDkim",
            Status = TaskExecutionStatus.Success
        }));

        Assert.Empty(cut.Instance.RunningTasks);
        Assert.False(cut.Instance.IsBusy(ServerMailStatusList.MailTaskKind.OpenDkim));
        Assert.Empty(Host.FindAll(".mail-task-running"));
    }

    [Fact]
    public async Task R12_AnAdoptedMailboxUsage_IsStoredWhenItEnds_AsIfItHadBeenLaunchedHere()
    {
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, $"api/servers/{ServerId}/mail/quota/ingest/74012", 3);
        Tracker.Upsert(InFlight(74012, "Mail - mailbox usage"));
        var cut = RenderList();

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(new TaskCompletedNotification
        {
            TaskId = 74012,
            ServerId = ServerId,
            TaskName = "Mail - mailbox usage",
            Status = TaskExecutionStatus.Success
        }));

        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("mail/quota/ingest/74012", StringComparison.Ordinal));
        Assert.Empty(cut.Instance.RunningTasks);
    }
}
