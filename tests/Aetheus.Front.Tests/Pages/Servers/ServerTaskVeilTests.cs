// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R-119: an action that queues an agent task keeps its follower on screen and veils the section body
/// (content kept mounted) from the launch until the task ends, success or failure. A page that shows an inventory
/// the agent sends back keeps the veil until the server's next state has arrived. A veil is never held for ever.
/// </summary>
public sealed class ServerTaskVeilTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly FakeTimeProvider _clock = new();
    private ServerDetailLoader? _loader;

    public ServerTaskVeilTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, extraPermissionKeys: [PermissionKeys.ServerFirewallManage]);
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddScoped(sp => new ServerDetailLoader(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<HubConnectionFactory>(),
            sp.GetRequiredService<NavigationManager>(),
            NullLogger<ServerDetailLoader>.Instance));
        _handler.SetJsonResponse(HttpMethod.Get, "api/monitoring/servers/", new List<ServerMetricDto>());
    }

    private TaskTrackerService Tracker => Services.GetRequiredService<TaskTrackerService>();

    private ServerDetailLoader Loader => _loader ??= Services.GetRequiredService<ServerDetailLoader>();

    private int ServerReads() => _handler.Requests.Count(r => r.Url.EndsWith("api/servers/5", StringComparison.Ordinal));

    private async Task LoadServerAsync()
    {
        _handler.SetJsonResponse("api/servers/5", new ServerDetailDto
        {
            Id = 5,
            Name = "srv",
            Hostname = "10.0.0.5",
            Type = ServerType.Normal,
            Status = ServerStatus.Online,
            Tags = [],
            Services = []
        });
        await Loader.EnsureLoadedAsync(5, Xunit.TestContext.Current.CancellationToken);
    }

    private IRenderedComponent<ServerTaskFollower> RenderFollower(bool settle = false, Func<Task>? onSettled = null) =>
        Render<ServerTaskFollower>(p =>
        {
            p.AddCascadingValue("ServerLoader", Loader)
                .Add(x => x.ServerId, 5)
                .Add(x => x.NamePrefix, "Apache")
                .Add(x => x.SettleOnHeartbeat, settle);
            if (onSettled is not null)
                p.Add(x => x.OnSettled, EventCallback.Factory.Create(this, onSettled));
        });

    private void Complete(int id, TaskExecutionStatus status) =>
        Tracker.HandleTerminal(new TaskCompletedNotification { TaskId = id, ServerId = 5, TaskName = "Apache - Disable site", Status = status });

    private void Seen(int id) =>
        Tracker.Upsert(new ServerTaskDto { Id = id, ServerId = 5, Name = "Apache - Disable site", Status = TaskExecutionStatus.Running });

    // ---- the loader's veil counter --------------------------------------------------------------------

    [Fact]
    public void TheLoader_IsPendingWhileAHoldIsOpen_AndEachHoldLiftsOnce()
    {
        var changes = 0;
        Loader.OnChanged += () => changes++;
        Assert.False(Loader.TaskPending);

        var first = Loader.HoldTaskVeil();
        var second = Loader.HoldTaskVeil();
        Assert.True(Loader.TaskPending);

        first.Dispose();
        first.Dispose();
        Assert.True(Loader.TaskPending);
        second.Dispose();
        Assert.False(Loader.TaskPending);
        Assert.Equal(4, changes);
    }

    // ---- the layout shows the veil on the section body ------------------------------------------------

    [Fact]
    public async Task TheLayout_HoldsThePageVeil_WhileATaskIsPending_AndKeepsTheContentMounted()
    {
        var cut = RenderLoadedLayout();
        var veil = Services.GetRequiredService<BusyVeil>();

        Assert.NotNull(cut.Find("[data-testid='server-section-body']"));
        Assert.False(veil.IsBusy);

        var hold = await cut.InvokeAsync(Loader.HoldTaskVeil);
        cut.WaitForAssertion(() =>
        {
            Assert.True(veil.IsBusy);
            Assert.NotNull(cut.Find("[data-testid='server-section-body']").QuerySelector("#body"));
        });

        await cut.InvokeAsync(hold.Dispose);
        cut.WaitForAssertion(() => Assert.False(veil.IsBusy));
    }

    private IRenderedComponent<ServerDetailLayout> RenderLoadedLayout()
    {
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("/servers/5/apache");
        typeof(ServerDetailLoader).GetProperty(nameof(ServerDetailLoader.Server))!.SetValue(Loader, new ServerDetailDto
        {
            Id = 5,
            Name = "srv",
            Hostname = "10.0.0.5",
            Type = ServerType.Normal,
            Status = ServerStatus.Online,
            Tags = [],
            Services = [],
            Apache = new ApacheDataDto(),
            Docker = new DockerDataDto { Containers = [], Images = [], ComposeStacks = [], Networks = [], Volumes = [] },
            Certbot = new CertbotDataDto(),
            Cron = new CronDataDto(),
            Mail = new MailDataDto(),
            Teamspeak = new TeamspeakDataDto(),
            Portsentry = new PortsentryDataDto(),
            Rkhunter = new RkhunterDataDto()
        });
        typeof(ServerDetailLoader).GetProperty(nameof(ServerDetailLoader.InitialLoadCompleted))!.SetValue(Loader, true);
        return Render<ServerDetailLayout>(p => p.Add(x => x.Body, b =>
        {
            b.AddMarkupContent(0, "<div id=\"body\">body</div>");
            b.OpenComponent<MountProbe>(1);
            b.AddComponentParameter(2, nameof(MountProbe.Mounted), (Action)(() => _bodyMounts++));
            b.CloseComponent();
        }));
    }

    private int _bodyMounts;

    /// <summary>Counts how many times the section body's content is mounted: a veil that remounts it shows here.</summary>
    private sealed class MountProbe : ComponentBase
    {
        [Parameter] public Action? Mounted { get; set; }

        protected override void OnInitialized() => Mounted?.Invoke();
    }

    /// <summary>R-19, R-1: the veil of a running task is one stable layer. The section body is mounted once, with the same
    /// class, from the first update to the last, the loader reports the pending state once up and once down, and the page
    /// veil goes up once and down once: no remount and no toggle per progress update, so what the user sees pulsing is
    /// never the markup.</summary>
    [Fact]
    public async Task TheSectionVeil_KeepsTheBodyMountedOnce_WithTheSameClass_AcrossEveryProgressUpdate()
    {
        var cut = RenderLoadedLayout();
        var seen = new List<bool>();
        Loader.OnChanged += () => seen.Add(Loader.TaskPending);
        var veil = Services.GetRequiredService<BusyVeil>();
        var veilSeen = new List<bool>();
        veil.Changed += () => veilSeen.Add(veil.IsBusy);

        var hold = await cut.InvokeAsync(Loader.HoldTaskVeil);
        cut.WaitForAssertion(() => Assert.True(veil.IsBusy));
        var veiledClass = cut.Find("[data-testid='server-section-body']").ClassName;

        for (var update = 0; update < 6; update++)
        {
            // Each update of the followed task reaches the layout as a Loader notification: a second hold that
            // opens and lifts notifies twice without the pending state ever leaving true.
            await cut.InvokeAsync(() => Loader.HoldTaskVeil().Dispose());
            Assert.True(Loader.TaskPending);
            Assert.Equal(1, _bodyMounts);
            Assert.Equal(veiledClass, cut.Find("[data-testid='server-section-body']").ClassName);
            Assert.True(veil.IsBusy);
        }

        await cut.InvokeAsync(hold.Dispose);
        cut.WaitForAssertion(() => Assert.False(veil.IsBusy));
        Assert.DoesNotContain(false, seen.Take(seen.Count - 1));
        Assert.False(seen[^1]);
        // The page veil came up once and went down once: nothing toggled it per progress update.
        Assert.Equal([true, false], veilSeen);
    }

    /// <summary>R-19: a task seen again and again by the follower (queued, then running, each progress line) holds one veil.</summary>
    [Fact]
    public async Task TheFollower_HoldsOneVeil_ThroughQueuedRunningAndEveryProgressUpdate()
    {
        var cut = RenderFollower();
        var pending = new List<bool>();
        Loader.OnChanged += () => pending.Add(Loader.TaskPending);

        await cut.InvokeAsync(() => cut.Instance.Expect("Disable site", veil: true));
        for (var update = 0; update < 5; update++)
            await cut.InvokeAsync(() => Seen(50103));
        await cut.InvokeAsync(() => Complete(50103, TaskExecutionStatus.Success));
        cut.WaitForAssertion(() => Assert.False(Loader.TaskPending));

        // Up when the launch is registered, down only at the end of the task: never down in between.
        Assert.True(pending[0]);
        Assert.False(pending[^1]);
        Assert.DoesNotContain(false, pending.Take(pending.Count - 1));
    }

    // ---- the follower holds the veil from the launch to the end of the task ---------------------------

    [Fact]
    public async Task ASucceededTask_VeilsFromTheLaunchUntilItEnds()
    {
        var cut = RenderFollower();

        await cut.InvokeAsync(() => cut.Instance.Expect("Disable site", veil: true));
        Assert.True(Loader.TaskPending);

        await cut.InvokeAsync(() => Seen(50101));
        Assert.True(Loader.TaskPending);

        await cut.InvokeAsync(() => Complete(50101, TaskExecutionStatus.Success));
        cut.WaitForAssertion(() => Assert.False(Loader.TaskPending));
        Assert.Contains("ServerTaskSucceeded", cut.Markup);
    }

    [Fact]
    public async Task AFailedTask_LiftsTheVeil_AndTheFollowerKeepsTheCause()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/logs/task/50102", new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = 50102, Message = "AH00526: Syntax error on line 7" }
        });
        var cut = RenderFollower();
        await cut.InvokeAsync(() => cut.Instance.Follow("Disable site", 50102, veil: true));
        Assert.True(Loader.TaskPending);

        await cut.InvokeAsync(() => Complete(50102, TaskExecutionStatus.Failed));

        cut.WaitForAssertion(() => Assert.False(Loader.TaskPending));
        cut.WaitForAssertion(() => Assert.Contains("AH00526", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARepeatedRead_AskedWithoutTheVeil_NeverVeilsTheSection()
    {
        var cut = RenderFollower();

        await cut.InvokeAsync(() => cut.Instance.Expect("Logs", veil: false));
        await cut.InvokeAsync(() => Seen(50103));

        Assert.False(Loader.TaskPending);
        Assert.Contains("ServerTaskRunning", cut.Markup);
    }

    [Fact]
    public async Task ATaskThatNeverShowsInTheFeed_DoesNotVeilTheSectionForEver()
    {
        var cut = RenderFollower();
        await cut.InvokeAsync(() => cut.Instance.Expect("Disable site", veil: true));
        Assert.True(Loader.TaskPending);

        _clock.Advance(ServerTaskFollower.UnboundLimit + TimeSpan.FromSeconds(1));

        cut.WaitForAssertion(() => Assert.False(Loader.TaskPending));
        Assert.Contains("ServerTaskRequested", cut.Markup);
    }

    [Fact]
    public async Task ATaskFollowedByIdThatTheFeedNeverLists_DoesNotVeilTheSectionForEver()
    {
        var cut = RenderFollower();
        await cut.InvokeAsync(() => cut.Instance.Follow("Open port", 50104, veil: true));
        Assert.True(Loader.TaskPending);

        _clock.Advance(ServerTaskFollower.UnboundLimit + TimeSpan.FromSeconds(1));

        cut.WaitForAssertion(() => Assert.False(Loader.TaskPending));
    }

    [Fact]
    public async Task ForgettingTheTask_LiftsTheVeil()
    {
        var cut = RenderFollower();
        await cut.InvokeAsync(() => cut.Instance.Expect("Disable site", veil: true));
        Assert.True(Loader.TaskPending);

        await cut.InvokeAsync(cut.Instance.Forget);

        cut.WaitForAssertion(() => Assert.False(Loader.TaskPending));
    }

    [Fact]
    public async Task LeavingThePage_NeverLeavesTheSectionVeiled()
    {
        var cut = RenderFollower();
        await cut.InvokeAsync(() => cut.Instance.Expect("Disable site", veil: true));
        Assert.True(Loader.TaskPending);

        await cut.InvokeAsync(() => cut.Instance.Dispose());

        Assert.False(Loader.TaskPending);
    }

    [Fact]
    public async Task OutsideAServerPage_TheFollowerOnlyReports()
    {
        var cut = Render<ServerTaskFollower>(p => p.Add(x => x.ServerId, 5).Add(x => x.NamePrefix, "Apache"));

        await cut.InvokeAsync(() => cut.Instance.Expect("Disable site", veil: true));

        Assert.Contains("ServerTaskRequested", cut.Markup);
        Assert.False(cut.Instance.Veiling);
    }

    // ---- an inventory page waits for the server's next state ------------------------------------------

    [Fact]
    public async Task AnInventoryPage_KeepsTheVeilAfterTheTaskEnded_UntilTheNextStateArrives()
    {
        await LoadServerAsync();
        var cut = RenderFollower(settle: true);
        await cut.InvokeAsync(() => cut.Instance.Expect("Disable site", veil: true));
        await cut.InvokeAsync(() => Seen(50105));

        await cut.InvokeAsync(() => Complete(50105, TaskExecutionStatus.Success));
        cut.WaitForAssertion(() => Assert.Contains("ServerTaskSucceeded", cut.Markup));
        Assert.True(Loader.TaskPending, "the task ended, but the server's state has not arrived yet");

        await Task.Delay(30, Xunit.TestContext.Current.CancellationToken);
        await Loader.RefreshAsync(5, Xunit.TestContext.Current.CancellationToken);

        cut.WaitForAssertion(() => Assert.False(Loader.TaskPending));
    }

    [Fact]
    public async Task AnInventoryPage_WhenNoStateComes_ReadsTheServerAgainAndLiftsTheVeil()
    {
        await LoadServerAsync();
        var cut = RenderFollower(settle: true);
        await cut.InvokeAsync(() => cut.Instance.Expect("Disable site", veil: true));
        await cut.InvokeAsync(() => Seen(50106));
        await cut.InvokeAsync(() => Complete(50106, TaskExecutionStatus.Success));
        cut.WaitForAssertion(() => Assert.Contains("ServerTaskSucceeded", cut.Markup));
        Assert.True(Loader.TaskPending);
        var readsBefore = ServerReads();

        _clock.Advance(ServerTaskFollower.SettleLimit + TimeSpan.FromSeconds(1));

        cut.WaitForAssertion(() => Assert.False(Loader.TaskPending));
        Assert.Equal(readsBefore + 1, ServerReads());
    }

    [Fact]
    public async Task AnInventoryPage_TellsThePageItsStateHasArrived_BeforeTheVeilLifts()
    {
        await LoadServerAsync();
        var settled = new List<bool>();
        var cut = RenderFollower(settle: true, onSettled: () =>
        {
            settled.Add(Loader.TaskPending);
            return Task.CompletedTask;
        });
        await cut.InvokeAsync(() => cut.Instance.Expect("Disable site", veil: true));
        await cut.InvokeAsync(() => Seen(50108));
        await cut.InvokeAsync(() => Complete(50108, TaskExecutionStatus.Success));
        cut.WaitForAssertion(() => Assert.Contains("ServerTaskSucceeded", cut.Markup));
        Assert.Empty(settled);

        await Task.Delay(30, Xunit.TestContext.Current.CancellationToken);
        await Loader.RefreshAsync(5, Xunit.TestContext.Current.CancellationToken);

        cut.WaitForAssertion(() => Assert.False(Loader.TaskPending));
        // Told once, while the veil still lay over the page: the page read its data again before showing it.
        Assert.Equal([true], settled);
    }

    [Fact]
    public async Task AnInventoryPage_WhenNoStateComes_TellsThePageToo()
    {
        await LoadServerAsync();
        var settled = 0;
        var cut = RenderFollower(settle: true, onSettled: () =>
        {
            settled++;
            return Task.CompletedTask;
        });
        await cut.InvokeAsync(() => cut.Instance.Expect("Disable site", veil: true));
        await cut.InvokeAsync(() => Seen(50109));
        await cut.InvokeAsync(() => Complete(50109, TaskExecutionStatus.Success));
        cut.WaitForAssertion(() => Assert.Contains("ServerTaskSucceeded", cut.Markup));

        _clock.Advance(ServerTaskFollower.SettleLimit + TimeSpan.FromSeconds(1));

        cut.WaitForAssertion(() => Assert.False(Loader.TaskPending));
        Assert.Equal(1, settled);
    }

    [Fact]
    public async Task AReadTask_OnAnInventoryPage_LiftsTheVeilAtTheEndOfTheTask_AndWaitsForNoState()
    {
        // A log, a configuration test: the agent changes nothing, so no new state is worth waiting for.
        await LoadServerAsync();
        var settled = 0;
        var cut = RenderFollower(settle: true, onSettled: () =>
        {
            settled++;
            return Task.CompletedTask;
        });
        await cut.InvokeAsync(() => cut.Instance.Expect("View logs", veil: true, settle: false));
        await cut.InvokeAsync(() => Seen(50110));

        await cut.InvokeAsync(() => Complete(50110, TaskExecutionStatus.Success));

        cut.WaitForAssertion(() => Assert.False(Loader.TaskPending));
        Assert.Equal(0, settled);
    }

    [Fact]
    public async Task AFollowedTaskById_OnAnInventoryPage_AlsoWaitsForTheNextState_UnlessItReadsOnly()
    {
        await LoadServerAsync();
        var cut = RenderFollower(settle: true);
        await cut.InvokeAsync(() => cut.Instance.Follow("Check", 50111, veil: true, settle: false));
        await cut.InvokeAsync(() => Complete(50111, TaskExecutionStatus.Success));
        cut.WaitForAssertion(() => Assert.False(Loader.TaskPending));

        await cut.InvokeAsync(() => cut.Instance.Follow("Apply", 50112, veil: true));
        await cut.InvokeAsync(() => Complete(50112, TaskExecutionStatus.Success));
        cut.WaitForAssertion(() => Assert.Contains("ServerTaskSucceeded", cut.Markup));
        Assert.True(Loader.TaskPending, "a change waits for the server's next state");
    }

    [Fact]
    public async Task TheApachePage_WaitsForTheNewState_AfterAChange_ButNotAfterAConfigurationTest()
    {
        await LoadServerAsync();
        _handler.SetJsonResponse(HttpMethod.Post, "apache/action", new ServiceTaskResponse { TaskId = 50131 });
        var cut = Render<ServerApacheSection>(p => p
            .AddCascadingValue("ServerLoader", Loader)
            .Add(x => x.ServerId, 5)
            .Add(x => x.Server, Loader.Server!));
        var execute = typeof(ServerApacheSection).GetMethod("ExecuteActionAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        await cut.InvokeAsync(async () => await (Task)execute.Invoke(cut.Instance, [ApacheAction.TestConfig, null])!);
        await cut.InvokeAsync(() => Seen(50131));
        await cut.InvokeAsync(() => Complete(50131, TaskExecutionStatus.Success));
        cut.WaitForAssertion(() => Assert.False(Loader.TaskPending), TimeSpan.FromSeconds(30));

        await cut.InvokeAsync(async () => await (Task)execute.Invoke(cut.Instance, [ApacheAction.DisableSite, "alpha.conf"])!);
        await cut.InvokeAsync(() => Seen(50132));
        await cut.InvokeAsync(() => Complete(50132, TaskExecutionStatus.Success));
        cut.WaitForAssertion(() => Assert.Contains("ServerTaskSucceeded", cut.Markup), TimeSpan.FromSeconds(30));
        // R-19: a site change is a one-off action: it waits for the server's next state in the progress block, without veil.
        Assert.False(Loader.TaskPending, "a one-off site change veils nothing");
        Assert.NotEmpty(cut.FindAll(".server-task-progress"));
    }

    [Theory]
    [InlineData(ApacheAction.Restart, false)]
    [InlineData(ApacheAction.Reload, false)]
    [InlineData(ApacheAction.Start, false)]
    [InlineData(ApacheAction.Stop, false)]
    [InlineData(ApacheAction.TestConfig, false)]
    [InlineData(ApacheAction.EnableSite, false)]
    [InlineData(ApacheAction.DisableSite, false)]
    [InlineData(ApacheAction.DeleteSite, false)]
    [InlineData(ApacheAction.DisableModule, false)]
    public async Task R37_TheApachePage_NeverVeils_NotEvenARestart(ApacheAction action, bool veiled)
    {
        await LoadServerAsync();
        _handler.SetJsonResponse(HttpMethod.Post, "apache/action", new ServiceTaskResponse { TaskId = 50141 });
        var cut = Render<ServerApacheSection>(p => p
            .AddCascadingValue("ServerLoader", Loader)
            .Add(x => x.ServerId, 5)
            .Add(x => x.Server, Loader.Server!));

        await cut.InvokeAsync(() => cut.Instance.ExecuteActionAsync(action, action is ApacheAction.Restart or ApacheAction.Reload or ApacheAction.Start or ApacheAction.Stop or ApacheAction.TestConfig ? null : "alpha.conf"));
        await cut.InvokeAsync(() => Seen(50141));

        Assert.Equal(veiled, Loader.TaskPending);
        Assert.NotEmpty(cut.FindAll(".server-task-progress"));
    }

    [Fact]
    public async Task R19_ReadingTheApacheLogs_ShowsTheProgressBlock_WithoutVeil()
    {
        await LoadServerAsync();
        _handler.SetResponse("api/servers/5/apache/logs", System.Net.HttpStatusCode.OK);
        var cut = Render<ServerApacheSection>(p => p
            .AddCascadingValue("ServerLoader", Loader)
            .Add(x => x.ServerId, 5)
            .Add(x => x.Server, Loader.Server!));

        await cut.InvokeAsync(() => cut.Instance.FetchLogsAsync());
        await cut.InvokeAsync(() => Seen(50142));

        Assert.False(Loader.TaskPending, "a log read is a one-off action: no veil");
        Assert.NotEmpty(cut.FindAll(".server-task-progress"));
    }

    [Fact]
    public async Task R37_AnApacheRestartOrDisableAdoptedOnArrival_ShowsProgress_WithoutVeil()
    {
        Tracker.Upsert(new ServerTaskDto { Id = 50151, ServerId = 5, Name = "Apache Restart", Status = TaskExecutionStatus.Running });
        var restart = RenderApacheFollower();
        Assert.False(Loader.TaskPending, "restarting Apache is not a blocking task");
        Assert.NotEmpty(restart.FindAll(".server-task-progress"));
        await DisposeComponentsAsync();
        Complete(50151, TaskExecutionStatus.Success);

        Tracker.Upsert(new ServerTaskDto { Id = 50152, ServerId = 5, Name = "Apache DisableSite - a.conf", Status = TaskExecutionStatus.Running });
        var disable = RenderApacheFollower();
        Assert.False(Loader.TaskPending);
        Assert.NotEmpty(disable.FindAll(".server-task-progress"));
    }

    [Theory]
    [InlineData(ServerTaskNames.PatchUpgrade, true)]
    [InlineData(ServerTaskNames.ScheduledReboot, true)]
    [InlineData(ServerTaskNames.PatchCheck, false)]
    [InlineData("Apache Restart", false)]
    [InlineData("Service Restart - nginx", false)]
    [InlineData("Mail - add domain example.org", false)]
    [InlineData("Firewall enable", false)]
    public async Task R37_OnlyTheBlockingTasks_VeilWhenAdoptedOnArrival(string taskName, bool veiled)
    {
        Tracker.Upsert(new ServerTaskDto { Id = 50161, ServerId = 5, Name = taskName, Status = TaskExecutionStatus.Running });

        var cut = Render<ServerTaskFollower>(p => p
            .AddCascadingValue("ServerLoader", Loader)
            .Add(x => x.ServerId, 5)
            .Add(x => x.NamePrefix, string.Empty)
            .Add(x => x.AdoptPrefixes, [taskName]));

        Assert.Equal(veiled, Loader.TaskPending);
        Assert.Equal(veiled, ServerBlockingTasks.Veils(taskName));
        Assert.NotEmpty(cut.FindAll(".server-task-progress"));
        await DisposeComponentsAsync();
        Assert.False(Loader.TaskPending);
    }

    private IRenderedComponent<ServerTaskFollower> RenderApacheFollower() =>
        Render<ServerTaskFollower>(p => p
            .AddCascadingValue("ServerLoader", Loader)
            .Add(x => x.ServerId, 5)
            .Add(x => x.NamePrefix, "Apache"));

    // ---- R-19: one progress block per zone, whatever the number of tasks -------------------------------

    [Fact]
    public async Task R19_ALoneOneOffTask_ShowsOneProgressBlock_NamedAfterTheTask_WithoutVeil()
    {
        var cut = RenderFollower();

        await cut.InvokeAsync(() => cut.Instance.Expect("Revoke certificate a.example"));
        await cut.InvokeAsync(() => Seen(50161));

        Assert.False(Loader.TaskPending);
        Assert.Single(cut.FindAll(".server-task-follower"));
        Assert.Single(cut.FindAll(".server-task-progress"));
        Assert.Equal("Revoke certificate a.example", cut.Find(".server-task-follower strong").TextContent);
        Assert.Empty(cut.FindAll(".server-task-count"));
    }

    [Fact]
    public async Task R19_SeveralTasks_ShowOneBlockSayingHowManyRun_WithoutVeil()
    {
        var cut = RenderFollower();
        await cut.InvokeAsync(() => cut.Instance.Expect("Revoke certificate a.example"));
        await cut.InvokeAsync(() => Seen(50171));
        await cut.InvokeAsync(() => cut.Instance.Expect("Delete certificate b.example"));
        await cut.InvokeAsync(() => Seen(50172));
        await cut.InvokeAsync(() => cut.Instance.Expect("Disable site c.conf"));
        await cut.InvokeAsync(() => Seen(50173));

        Assert.False(Loader.TaskPending);
        Assert.Single(cut.FindAll(".server-task-follower"));
        Assert.Single(cut.FindAll(".server-task-progress"));
        Assert.Contains("ServerTasksRunning", cut.Find(".server-task-count").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R19_WhenOneOfTwoTasksEnds_TheBlockGoesOnWithTheOtherAndDoesNotAnnounceDone()
    {
        var cut = RenderFollower();
        await cut.InvokeAsync(() => cut.Instance.Expect("Revoke certificate a.example"));
        await cut.InvokeAsync(() => Seen(50181));
        await cut.InvokeAsync(() => cut.Instance.Expect("Delete certificate b.example"));
        await cut.InvokeAsync(() => Seen(50182));

        await cut.InvokeAsync(() => Complete(50182, TaskExecutionStatus.Success));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".server-task-count")));
        Assert.DoesNotContain("ServerTaskSucceeded", cut.Markup, StringComparison.Ordinal);
        Assert.Single(cut.FindAll(".server-task-progress"));
        Assert.Contains("/tasks/50181", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R19_AFailedTask_ShowsItsCauseAtOnce_EvenWhileAnotherRuns()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/logs/task/50192", new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = 50192, Message = "AH00526: Syntax error on line 7" }
        });
        var cut = RenderFollower();
        await cut.InvokeAsync(() => cut.Instance.Expect("Revoke certificate a.example"));
        await cut.InvokeAsync(() => Seen(50191));
        await cut.InvokeAsync(() => cut.Instance.Expect("Delete certificate b.example"));
        await cut.InvokeAsync(() => Seen(50192));

        await cut.InvokeAsync(() => Complete(50192, TaskExecutionStatus.Failed));

        cut.WaitForAssertion(() => Assert.Contains("AH00526", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".server-task-progress"));
    }

    [Fact]
    public async Task R19_AOneOffTaskOnAnInventoryPage_KeepsItsProgressUntilTheNextStateArrives_ThenLiftsWithoutEverVeiling()
    {
        await LoadServerAsync();
        var cut = RenderFollower(settle: true);
        await cut.InvokeAsync(() => cut.Instance.Expect("Disable site"));
        await cut.InvokeAsync(() => Seen(50201));

        await cut.InvokeAsync(() => Complete(50201, TaskExecutionStatus.Success));
        cut.WaitForAssertion(() => Assert.Contains("ServerTaskSucceeded", cut.Markup));
        Assert.True(cut.Instance.Progressing, "the task ended, but the server's state has not arrived yet");
        Assert.False(Loader.TaskPending);

        await Task.Delay(30, Xunit.TestContext.Current.CancellationToken);
        await Loader.RefreshAsync(5, Xunit.TestContext.Current.CancellationToken);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".server-task-progress")));
        Assert.False(Loader.TaskPending);
        Assert.Contains("ServerTaskSucceeded", cut.Markup, StringComparison.Ordinal);
    }

    // ---- a page whose data is not the server state reads it again once that state has arrived ---------

    private int FirewallReads() =>
        _handler.RequestDetails.Count(r => r.Method == "GET" && r.Url.EndsWith("api/servers/5/firewall", StringComparison.Ordinal));

    [Fact]
    public async Task TheFirewallPage_ReadsTheRulesAgain_WhenTheServersNewStateHasArrived_BeforeTheVeilLifts()
    {
        await LoadServerAsync();
        _handler.SetJsonResponse(HttpMethod.Get, "firewall", new ServerFirewallDto
        {
            Installed = true,
            Active = true,
            StatusKnown = true,
            FirewallManagementAvailable = true,
            Rules = []
        });
        _handler.SetJsonResponse(HttpMethod.Post, "firewall/allow", new ServiceTaskResponse { TaskId = 50120 });
        var cut = Render<ServerFirewallSection>(p => p
            .AddCascadingValue("ServerLoader", Loader)
            .Add(x => x.ServerId, 5)
            .Add(x => x.FirewallManagementAvailable, true));
        cut.WaitForAssertion(() => Assert.Equal(1, FirewallReads()));
        await cut.Find("form").SubmitAsync();
        cut.WaitForAssertion(() => Assert.Contains("/tasks/50120", cut.Markup));

        await cut.InvokeAsync(() => Tracker.HandleTerminal(
            new TaskCompletedNotification { TaskId = 50120, ServerId = 5, TaskName = "Firewall - Allow", Status = TaskExecutionStatus.Success }));
        cut.WaitForAssertion(() => Assert.Contains("ServerTaskSucceeded", cut.Markup));
        // R-19: adding a rule is a one-off action: no veil, but its progress block lasts until the new state has arrived.
        var follower = cut.FindComponent<ServerTaskFollower>().Instance;
        Assert.True(follower.Progressing, "the agent's new state has not arrived yet");
        Assert.False(Loader.TaskPending);
        Assert.Equal(1, FirewallReads());

        await Task.Delay(30, Xunit.TestContext.Current.CancellationToken);
        await Loader.RefreshAsync(5, Xunit.TestContext.Current.CancellationToken);

        cut.WaitForAssertion(() => Assert.False(follower.Progressing));
        Assert.Equal(2, FirewallReads());
    }

    [Fact]
    public async Task APageWithoutAnInventory_LiftsTheVeilAtTheEndOfTheTask()
    {
        await LoadServerAsync();
        var cut = RenderFollower(settle: false);
        await cut.InvokeAsync(() => cut.Instance.Expect("Disable site", veil: true));
        await cut.InvokeAsync(() => Seen(50107));

        await cut.InvokeAsync(() => Complete(50107, TaskExecutionStatus.Success));

        cut.WaitForAssertion(() => Assert.False(Loader.TaskPending));
    }
}
