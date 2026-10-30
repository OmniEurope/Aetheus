// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Render and method coverage for ServerServicesSection - focuses on uncovered branches:
/// GetStatusBadgeStyle variants, HandleTaskCompleted, AppendLog, CloseLogsAsync,
/// OnRowRender, OpenServiceModule navigation, log buffer overflow.
/// </summary>
public class ServerServicesSectionRenderTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly BunitTestHelper.TestHandler _handler;

    public ServerServicesSectionRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/servers/21/services/action", new { taskId = 101 });
        _handler.SetJsonResponse("api/servers/21/services/install", new { taskId = 102 });
        _handler.SetJsonResponse("api/servers/21/services/uninstall", new { taskId = 103 });
        _handler.SetJsonResponse("api/servers/21/services/logs", new { taskId = 42 });
    }

    private static ServerDetailDto MakeServer(params ServiceInfoDto[] services) => new()
    {
        Id = 21,
        Name = "svc-render-srv",
        Hostname = "10.0.0.21",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        Tags = [],
        Services = services.ToList()
    };

    private static ServerDetailDto MakeServer(bool packageMgmt, params ServiceInfoDto[] services) => new()
    {
        Id = 21,
        Name = "svc-render-srv",
        Hostname = "10.0.0.21",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        // Both capabilities ship together via the server-management module; mirror that here so the
        // "enabled" case leaves no disabled-tooltip (the teamspeak Install tooltip reuses the same key).
        Tags = [],
        Services = services.ToList(),
        PackageManagementAvailable = packageMgmt,
        TeamspeakSetupAvailable = packageMgmt
    };

    private static ServiceInfoDto Svc(string name, bool installed = true, bool manageable = true,
        bool running = true, string status = "active", ServiceType type = ServiceType.Systemd) => new()
        {
            Name = name,
            Status = status,
            IsRunning = running,
            IsInstalled = installed,
            IsManageable = manageable,
            Type = type
        };

    private IRenderedComponent<ServerServicesSection> RenderSection(ServerDetailDto? server = null)
    {
        var s = server ?? MakeServer();
        return Render<ServerServicesSection>(p => p
            .Add(x => x.Server, s)
            .Add(x => x.ServerId, s.Id));
    }

    [Fact]
    public void R3_27_OpeningTheMailModuleFromServices_StaysAClientSideNavigation()
    {
        // R3-27: the start screen seen on /servers/5/mail is a full document load. The in-app path
        // Services -> Messagerie must not be one: it pushes a history entry without forceLoad.
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("/servers/21/services");
        var cut = RenderSection(MakeServer(Svc("postfix")));

        cut.WaitForElement("button[title='OpenModule: postfix']").Click();

        var last = nav.History.First();
        Assert.Equal("/servers/21/mail", last.Uri);
        Assert.False(last.Options.ForceLoad);
        Assert.False(last.Options.ReplaceHistoryEntry);
    }

    [Fact]
    public void R3_59_TheOtherServicesTab_SaysThatAPackageWithoutAServiceIsNotListed_AndPointsToTheUpdates()
    {
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("/servers/21/services?tab=other");

        var cut = RenderSection(MakeServer(Svc("cron", manageable: false)));

        var notice = cut.Find(".services-packages-not-listed");
        Assert.Contains("ServicesPackagesNotListed", notice.TextContent, StringComparison.Ordinal);
        Assert.Equal("/servers/21/updates", notice.QuerySelector("a")!.GetAttribute("href"));
        // Second part of R3-59: the complete read-only package list sits in the same tab.
        var inventory = cut.Find(".server-package-inventory");
        Assert.NotNull(inventory.QuerySelector(".package-inventory-readonly"));
    }

    // ── GetStatusBadgeStyle ──────────────────────────────────────────────────

    [Theory]
    // Running services use blue, stopped services red and scheduled services Info.
    [InlineData("active", true, OmniTone.Accent)]
    [InlineData("scheduled", false, OmniTone.Info)]
    [InlineData("idle", false, OmniTone.Neutral)]
    [InlineData("dead", false, OmniTone.Danger)]
    [InlineData("installed", false, OmniTone.Danger)]
    [InlineData("generated", false, OmniTone.Danger)]
    [InlineData("masked", false, OmniTone.Neutral)]
    [InlineData("activating", false, OmniTone.Warning)]
    [InlineData("failed", false, OmniTone.Danger)]
    public void GetStatusBadgeStyle_AllVariants(string status, bool isRunning, OmniTone expected)
    {
        var result = ManageableServiceGrid.GetStatusBadgeStyle(status, isRunning);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("running", false, "ServiceStatusRunning")]
    [InlineData("dead", false, "ServiceStatusStopped")]
    [InlineData("installed", false, "ServiceStatusStopped")]
    [InlineData("Stopped", false, "ServiceStatusStopped")]
    [InlineData("masked", false, "ServiceStatusMasked")]
    [InlineData("failed", false, "ServiceStatusFailed")]
    [InlineData("scheduled", false, "ServiceStatusScheduled")]
    [InlineData("idle", false, "ServiceStatusIdle")]
    [InlineData("anything", true, "ServiceStatusRunning")]
    [InlineData("a-word-systemd-adds-later", false, null)]
    public void R507_ARawStatus_HasItsLabel_OrIsShownAsWritten(string status, bool isRunning, string? key)
    {
        Assert.Equal(key, Aetheus.Front.Components.Servers.ServiceStatusPresentation.LabelKey(status, isRunning));
    }

    [Fact]
    public void GetStatusBadgeStyle_Running_AlwaysBlue()
    {
        // A running service reads blue, whatever its unit state says.
        Assert.Equal(OmniTone.Accent, ManageableServiceGrid.GetStatusBadgeStyle("scheduled", true));
    }

    // ── Render with full service data ────────────────────────────────────────

    [Fact]
    public void Renders_WithDockerRunning_ShowsDockerName()
    {
        var cut = RenderSection(MakeServer(Svc("docker"), Svc("apache2")));
        Assert.Contains("docker", cut.Markup);
    }

    [Fact]
    public void Renders_WithNotInstalledService_IncludedInModuleTab()
    {
        var cut = RenderSection(MakeServer(Svc("certbot", installed: false)));
        // Not installed services should appear in the module-not-installed list
        Assert.Contains("certbot", cut.Markup);
    }

    [Fact]
    public void Renders_WindowsServiceType_FormattedCorrectly()
    {
        Assert.Equal("Windows", ManageableServiceGrid.FormatType(ServiceType.WindowsService));
    }

    [Fact]
    public void Renders_UnknownServiceType_FallsBackToToString()
    {
        var unknown = (ServiceType)999;
        var result = ManageableServiceGrid.FormatType(unknown);
        Assert.Equal("999", result);
    }

    [Fact]
    public void Renders_SystemServices_ShowsSystemCount()
    {
        var cut = RenderSection(MakeServer(
            Svc("systemd-journald", manageable: false),
            Svc("docker")));
        Assert.Equal(1, cut.Instance.SystemCount);
    }

    // ── Package-manage capability gate ───────────────────────────────────────

    [Fact]
    public void Renders_PackageMgmtDisabled_ShowsCapabilityBannerAndGatesInstall()
    {
        // Default agent posture (no server-management): the capability is OFF, so the section must warn
        // and NOT present install/uninstall as live actions (they would always 400 server-side).
        var cut = RenderSection(MakeServer(packageMgmt: false, Svc("certbot", installed: false)));
        Assert.False(cut.Instance.CanManagePackages);
        // The stub localizer echoes the key, so the banner/disabled-tooltip text is the key itself.
        Assert.Contains("ServiceInstallCapabilityDisabled", cut.Markup);
        // The notice + live heartbeat render as two tiles: the warning-accented card and the
        // heartbeat-pulse tile beside it (the services tables stay tables).
        Assert.Contains("capability-warning-tile", cut.Markup);
        Assert.Contains("heartbeat-pulse", cut.Markup);
    }

    [Fact]
    public void Renders_PackageMgmtEnabled_NoCapabilityBanner()
    {
        var cut = RenderSection(MakeServer(packageMgmt: true, Svc("certbot", installed: false)));
        Assert.True(cut.Instance.CanManagePackages);
        Assert.DoesNotContain("ServiceInstallCapabilityDisabled", cut.Markup);
    }

    [Fact]
    public void Renders_OfflineServer_DisablesEveryMutatingServiceAction()
    {
        var server = MakeServer(
            packageMgmt: true,
            Svc("nginx", running: true),
            Svc("certbot", installed: false)) with
        { Status = ServerStatus.Offline };

        var cut = RenderSection(server);

        var mutating = cut.FindAll("button[title='AgentOfflineActionBlocked']").ToList();
        Assert.NotEmpty(mutating);
        Assert.All(mutating, button => Assert.True(button.HasAttribute("disabled")));
        Assert.Contains("AgentOfflineActionBlocked", cut.Markup);
    }

    [Fact]
    public void SuccessfulInstall_OffersImmediateStartPrompt()
    {
        BunitTestHelper.UseImmediateDialogs(this);
        var cut = RenderSection(MakeServer(packageMgmt: true, Svc("nginx", installed: false)));
        cut.Instance.TrackPendingInstall("nginx", 102);

        cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            ServerId = 21,
            TaskId = 102,
            TaskName = "Install - nginx",
            Status = TaskExecutionStatus.Success
        }));

        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        cut.WaitForState(() => dialog.OpenCount == 1);
        Assert.Equal("ServiceInstalledTitle", dialog.LastTitle);
        Assert.NotNull(dialog.LastConfirmMessage);
        Assert.Contains("ConfirmStartAfterInstall", dialog.LastConfirmMessage);
    }

    // ── HandleTaskCompleted ──────────────────────────────────────────────────

    [Fact]
    public void HandleTaskCompleted_TheFollowedTask_Succeeded_SaysSoOnTheRow()
    {
        var cut = RenderSection(MakeServer(Svc("docker")));
        cut.Instance.Actions.Track("docker", 1, "Start");
        cut.Render();
        Assert.Contains("ServiceActionQueued", cut.Markup);

        cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            ServerId = 21,
            TaskId = 1,
            TaskName = "Start - docker",
            Status = TaskExecutionStatus.Success
        }));

        Assert.Equal(ServiceActionPhase.Succeeded, cut.Instance.Actions.Of("docker", [])!.Phase);
        cut.WaitForAssertion(() => Assert.Contains("ServiceActionSucceeded", cut.Markup));
    }

    [Fact]
    public void HandleTaskCompleted_TheFollowedTask_Failed_LeavesItsErrorOnTheRow()
    {
        // Service-action failures expose exit code and task link on their row while releasing the
        // action buttons.
        var cut = RenderSection(MakeServer(Svc("docker")));
        cut.Instance.Actions.Track("docker", 7, "Install");

        cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            ServerId = 21,
            TaskId = 7,
            TaskName = "Install - docker",
            Status = TaskExecutionStatus.Failed,
            ExitCode = 100
        }));

        var state = cut.Instance.Actions.Of("docker", [])!;
        Assert.Equal(ServiceActionPhase.Failed, state.Phase);
        Assert.Equal(100, state.ExitCode);
        Assert.False(state.InFlight);
        cut.WaitForAssertion(() =>
        {
            var line = cut.Find(".service-action-state--failed a");
            Assert.Contains("ServiceActionFailedExit", line.TextContent);
            Assert.Equal("/tasks/7", line.GetAttribute("href"));
        });
    }

    [Fact]
    public void HandleTaskCompleted_AnotherTask_KeepsTheActionInFlight()
    {
        var cut = RenderSection(MakeServer(Svc("nginx")));
        cut.Instance.Actions.Track("nginx", 1, "Start");

        // A different task id (an unrelated operation completing first) must NOT end our action.
        cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            ServerId = 21,
            TaskId = 2,
            TaskName = "Start - apache2",
            Status = TaskExecutionStatus.Success
        }));

        Assert.True(cut.Instance.Actions.Of("nginx", [])!.InFlight);
    }

    [Fact]
    public void HandleTaskCompleted_PreviousServer_DoesNotMutateCurrentState()
    {
        var cut = RenderSection(MakeServer(Svc("nginx")));
        cut.Instance.Actions.Track("nginx", 1, "Start");

        cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            ServerId = 22,
            TaskId = 1,
            TaskName = "Start - nginx",
            Status = TaskExecutionStatus.Success
        }));

        Assert.True(cut.Instance.Actions.Of("nginx", [])!.InFlight);
    }

    [Fact]
    public void TwoActionsAtOnce_AreEachFollowedOnTheirOwnRow()
    {
        // Batch actions retain independent per-service progress.
        var cut = RenderSection(MakeServer(Svc("nginx"), Svc("apache2")));
        cut.Instance.Actions.Track("nginx", 11, "Restart");
        cut.Instance.Actions.Track("apache2", 12, "Stop");

        cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            ServerId = 21,
            TaskId = 12,
            TaskName = "Stop - apache2",
            Status = TaskExecutionStatus.Success
        }));

        Assert.Equal(ServiceActionPhase.Queued, cut.Instance.Actions.Of("nginx", [])!.Phase);
        Assert.Equal(ServiceActionPhase.Succeeded, cut.Instance.Actions.Of("apache2", [])!.Phase);
        // The agent started the first task: the live task list turns its row to "running".
        var live = new List<ServerTaskDto> { new() { Id = 11, Status = TaskExecutionStatus.Running } };
        Assert.Equal(ServiceActionPhase.Running, cut.Instance.Actions.Of("nginx", live)!.Phase);
    }

    [Fact]
    public void TheListTheSuccessRefreshes_KeepsTheSuccessLineOnScreen()
    {
        // Completion feedback remains readable while server state refreshes.
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        Services.AddSingleton<TimeProvider>(clock);
        var cut = RenderSection(MakeServer(Svc("docker")));
        cut.Instance.Actions.Track("docker", 11, "Start");
        cut.Instance.HandleTaskCompleted(new TaskCompletedNotification { ServerId = 21, TaskId = 11, Status = TaskExecutionStatus.Success });

        clock.Advance(TimeSpan.FromSeconds(1));
        cut.Render(parameters => parameters
            .Add(component => component.Server, MakeServer(Svc("docker")))
            .Add(component => component.ServerId, 21));

        Assert.Equal(ServiceActionPhase.Succeeded, cut.Instance.Actions.Of("docker", [])!.Phase);
        cut.WaitForAssertion(() => Assert.Contains("ServiceActionSucceeded", cut.Markup));
    }

    [Fact]
    public void ANewServiceList_DropsTheSuccessLineOnceRead_AndKeepsTheFailure()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        Services.AddSingleton<TimeProvider>(clock);
        var cut = RenderSection(MakeServer(Svc("nginx"), Svc("apache2")));
        cut.Instance.Actions.Track("nginx", 11, "Start");
        cut.Instance.Actions.Track("apache2", 12, "Start");
        cut.Instance.HandleTaskCompleted(new TaskCompletedNotification { ServerId = 21, TaskId = 11, Status = TaskExecutionStatus.Success });
        cut.Instance.HandleTaskCompleted(new TaskCompletedNotification { ServerId = 21, TaskId = 12, Status = TaskExecutionStatus.Failed, ExitCode = 5 });

        // A later heartbeat brings a new list: the started service now shows its own state.
        clock.Advance(Aetheus.Front.Components.Servers.ServiceActionTracker.SuccessShownAtLeast);
        cut.Render(parameters => parameters
            .Add(component => component.Server, MakeServer(Svc("nginx"), Svc("apache2")))
            .Add(component => component.ServerId, 21));

        Assert.Null(cut.Instance.Actions.Of("nginx", []));
        Assert.Equal(ServiceActionPhase.Failed, cut.Instance.Actions.Of("apache2", [])!.Phase);
    }

    [Fact]
    public void ServerChange_ClearsBusyAndLogState()
    {
        var cut = RenderSection(MakeServer(Svc("nginx")));
        cut.Instance.Actions.Track("nginx", 12, "Start");
        typeof(ServerServicesSection).GetField("_currentLogTaskId", Priv)!.SetValue(cut.Instance, 44);
        typeof(ServerServicesSection).GetField("_logsVisible", Priv)!.SetValue(cut.Instance, true);

        var next = MakeServer(Svc("apache2")) with { Id = 22 };
        cut.Render(parameters => parameters
            .Add(component => component.Server, next)
            .Add(component => component.ServerId, 22));

        Assert.Null(cut.Instance.Actions.Of("nginx", []));
        Assert.Null(typeof(ServerServicesSection).GetField("_currentLogTaskId", Priv)!.GetValue(cut.Instance));
        Assert.False((bool)typeof(ServerServicesSection).GetField("_logsVisible", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task StartLogStreamAsync_WaitsForExistingRestartToFinish()
    {
        var cut = RenderSection(MakeServer(Svc("nginx")));
        _handler.SetResponse(HttpMethod.Post, "api/servers/21/services/logs",
            System.Net.HttpStatusCode.InternalServerError);
        typeof(ServerServicesSection).GetField("_logsServiceName", Priv)!.SetValue(cut.Instance, "nginx");
        var gate = (SemaphoreSlim)typeof(ServerServicesSection)
            .GetField("_logStreamGate", Priv)!.GetValue(cut.Instance)!;
        await gate.WaitAsync(Xunit.TestContext.Current.CancellationToken);
        var method = typeof(ServerServicesSection).GetMethod("StartLogStreamAsync", Priv)!;
        var start = cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        Assert.False(start.IsCompleted);
        gate.Release();
        await start;
    }

    [Fact]
    public void HandleTaskCompleted_MatchingLogTaskId_ClearsLoading()
    {
        var cut = RenderSection();
        typeof(ServerServicesSection).GetField("_currentLogTaskId", Priv)!.SetValue(cut.Instance, 99);
        typeof(ServerServicesSection).GetField("_logsLoading", Priv)!.SetValue(cut.Instance, true);
        bool Loading() => (bool)typeof(ServerServicesSection).GetField("_logsLoading", Priv)!.GetValue(cut.Instance)!;
        var taskLog = new TaskCompletionSource<List<TaskLogDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/logs/task/99", _ => taskLog.Task);

        cut.Instance.HandleTaskCompleted(new TaskCompletedNotification
        {
            ServerId = 21,
            TaskId = 99,
            TaskName = "Logs task",
            Status = TaskExecutionStatus.Success
        });

        // Recette R3-44: the pane stays busy until the task's own log has been read, so the loading state
        // clears asynchronously, after that read.
        Assert.True(Loading());
        taskLog.SetResult([new TaskLogDto { Id = 1, TaskId = 99, Message = "log output here" }]);
        cut.WaitForAssertion(() => Assert.False(Loading()));
    }

    /// <summary>Recette R-116: opening the log viewer while the read is pending veils the log zone (a block of sensible
    /// height, nothing in it yet); no loader is added.</summary>
    [Fact]
    public async Task PendingLogRead_VeilsTheEmptyLogZone_AndAddsNoLoader()
    {
        var queued = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Post, "api/servers/21/services/logs", _ => queued.Task);
        var cut = RenderSection();

        var opening = cut.InvokeAsync(() => cut.Instance.ViewServiceLogsAsync("nginx"));

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=service-logs-zone]").GetAttribute("aria-busy")));
        Assert.Contains("omni-busy", cut.Find("[data-testid=service-logs-zone]").ClassName, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".aetheus-loader-logo"));
        Assert.Empty(cut.FindAll("[data-testid=service-logs-zone] .docker-logs"));

        queued.SetResult(new { taskId = 42 });
        // The test hub cannot start, so the end of the read is not the subject here, only the veil while it is pending.
        await Record.ExceptionAsync(() => opening);
    }

    [Fact]
    public void HandleTaskCompleted_MatchingLogTaskId_UsesOutput_WhenLogsEmpty()
    {
        var cut = RenderSection();
        typeof(ServerServicesSection).GetField("_currentLogTaskId", Priv)!.SetValue(cut.Instance, 55);
        typeof(ServerServicesSection).GetField("_logsLoading", Priv)!.SetValue(cut.Instance, true);
        typeof(ServerServicesSection).GetField("_logsContent", Priv)!.SetValue(cut.Instance, "");

        cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            ServerId = 21,
            TaskId = 55,
            TaskName = "log task",
            Output = "actual log output",
            Status = TaskExecutionStatus.Success
        }));

        // The task log is read on the renderer's dispatcher after the call returns: wait for it.
        cut.WaitForAssertion(() => Assert.Equal("actual log output",
            (string)typeof(ServerServicesSection).GetField("_logsContent", Priv)!.GetValue(cut.Instance)!));
    }

    // ── AppendLog ────────────────────────────────────────────────────────────

    [Fact]
    public void AppendLog_NullMessage_ProducesNothing()
    {
        var cut = RenderSection();
        var method = typeof(ServerServicesSection).GetMethod("AppendLog", Priv)!;
        method.Invoke(cut.Instance, [null]);
        var content = (string)typeof(ServerServicesSection).GetField("_logsContent", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(content);
    }

    [Fact]
    public void AppendLog_Message_AppendsToBuffer()
    {
        var cut = RenderSection();
        var method = typeof(ServerServicesSection).GetMethod("AppendLog", Priv)!;
        method.Invoke(cut.Instance, ["hello world"]);
        var content = (string)typeof(ServerServicesSection).GetField("_logsContent", Priv)!.GetValue(cut.Instance)!;
        Assert.Contains("hello world", content);
        var buffer = (ServiceLogBuffer)typeof(ServerServicesSection).GetField("_logBuffer", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(1, buffer.LineCount);
    }

    [Fact]
    public void AppendLog_MaxLinesExceeded_TruncatesAndContinues()
    {
        var cut = RenderSection();
        var method = typeof(ServerServicesSection).GetMethod("AppendLog", Priv)!;
        // Fill the buffer up to its bound so the next line starts it over.
        for (var line = 0; line < ServiceLogBuffer.MaxLines; line++)
            method.Invoke(cut.Instance, [$"line {line}"]);
        method.Invoke(cut.Instance, ["overflow line"]);
        var content = (string)typeof(ServerServicesSection).GetField("_logsContent", Priv)!.GetValue(cut.Instance)!;
        Assert.Contains("truncated", content);
        Assert.Contains("overflow line", content);
    }

    // ── CloseLogsAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task CloseLogsAsync_ClearsLogsState()
    {
        var cut = RenderSection();
        typeof(ServerServicesSection).GetField("_logsVisible", Priv)!.SetValue(cut.Instance, true);
        typeof(ServerServicesSection).GetField("_logsLoading", Priv)!.SetValue(cut.Instance, true);

        var method = typeof(ServerServicesSection).GetMethod("CloseLogsAsync", Priv)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        Assert.False((bool)typeof(ServerServicesSection).GetField("_logsVisible", Priv)!.GetValue(cut.Instance)!);
        Assert.False((bool)typeof(ServerServicesSection).GetField("_logsLoading", Priv)!.GetValue(cut.Instance)!);
        Assert.Empty((string)typeof(ServerServicesSection).GetField("_logsContent", Priv)!.GetValue(cut.Instance)!);
    }

    // ── OnRowRender ──────────────────────────────────────────────────────────

    // Row greying belongs to the reusable ManageableServiceGrid.
    [Fact]
    public void OnRowRender_NotInstalled_AddsColorClass()
    {
        var cut = Render<ManageableServiceGrid>(parameters => parameters
            .Add(component => component.Services, [new ServiceInfoDto { Name = "nginx", IsInstalled = false }]));
        Assert.Contains("omni-u-text-muted", cut.Find("tr[data-omni-row-index='0']").ClassList);
    }

    [Fact]
    public void OnRowRender_Installed_DoesNotAddColorClass()
    {
        var cut = Render<ManageableServiceGrid>(parameters => parameters
            .Add(component => component.Services, [new ServiceInfoDto { Name = "nginx", IsInstalled = true }]));
        Assert.DoesNotContain("omni-u-text-muted", cut.Find("tr[data-omni-row-index='0']").ClassList);
    }

    // ── DisposeAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_NoHub_DoesNotThrow()
    {
        var cut = RenderSection();
        await cut.Instance.DisposeAsync();
        // Disposal is idempotent: a second dispose after the first must not throw either
        // (no hub connection was ever established, so there is nothing to tear down twice).
        var ex = await Record.ExceptionAsync(async () => await cut.Instance.DisposeAsync());
        Assert.Null(ex);
    }

    // ── OnLogLinesChanged / OnFollowChangedAsync ─────────────────────────────

    [Fact]
    public async Task OnLogLinesChanged_UpdatesLinesAndRestartsLogStream()
    {
        var cut = RenderSection();
        // A live log pane is required for the restart to re-request logs (guard: _logsServiceName).
        typeof(ServerServicesSection).GetField("_logsServiceName", Priv)!.SetValue(cut.Instance, "nginx");

        var method = typeof(ServerServicesSection).GetMethod("OnLogLinesChanged", Priv)!;
        // The re-stream reaches the log-hub start (which fails under the test hub); the logs POST is
        // emitted before that, so tolerate the hub failure and assert the observable request.
        var exception = await Record.ExceptionAsync(
            () => cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [200])!));

        Assert.Null(exception);
        Assert.Equal(200, (int)typeof(ServerServicesSection).GetField("_logLines", Priv)!.GetValue(cut.Instance)!);
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/21/services/logs"));
    }

    [Fact]
    public async Task OnFollowChangedAsync_UpdatesFollowAndRestartsLogStream()
    {
        var cut = RenderSection();
        typeof(ServerServicesSection).GetField("_logsServiceName", Priv)!.SetValue(cut.Instance, "nginx");

        var method = typeof(ServerServicesSection).GetMethod("OnFollowChangedAsync", Priv)!;
        var exception = await Record.ExceptionAsync(
            () => cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [true])!));

        Assert.Null(exception);
        Assert.True(cut.Instance.IsFollowingLogs);
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/21/services/logs"));
    }

    [Fact]
    public async Task FollowLogsToggle_ThroughRenderedControl_RestartsStream()
    {
        var cut = RenderSection();
        await cut.InvokeAsync(() => cut.Instance.ViewServiceLogsAsync("nginx"));
        cut.Render();
        // The viewer opens live; the toggle is how a user freezes it, then resumes it.
        Assert.True(cut.Instance.IsFollowingLogs);
        await cut.InvokeAsync(() => cut.Instance.OnFollowChangedAsync(false));
        cut.Render();
        _handler.Requests.Clear();

        var exception = Record.Exception(
            () => cut.Find("button.omni-switch.omni-switch--text-first").Click());

        Assert.Null(exception);
        // Click() dispatches the handler without awaiting it, unlike the sibling test that invokes
        // OnFollowChangedAsync directly. Asserting immediately raced the continuation and failed only
        // under full-suite load; waiting keeps the same discriminating power, because a toggle that
        // never restarts the stream still never satisfies these assertions.
        //
        // The wait was not enough: this still failed once under full-suite load, and the report said
        // only that the wait expired. That does not distinguish a handler that never ran, a restart
        // refused by the _logsServiceName guard, and a restart still blocked on the log-stream gate.
        // The timeout is deliberately left at the default - raising it would thin the flake out
        // without ever saying which of the three it was - and a failure now carries the fields
        // StartLogStreamAsync actually branches on instead.
        try
        {
            cut.WaitForAssertion(() =>
            {
                Assert.True(cut.Instance.IsFollowingLogs);
                Assert.Contains(_handler.Requests,
                    r => r.Method == "POST" && r.Url.Contains("api/servers/21/services/logs"));
            });
        }
        catch (Bunit.Extensions.WaitForHelpers.WaitForFailedException ex)
        {
            throw new InvalidOperationException(DescribeLogStreamState(cut), ex);
        }
    }

    [Fact]
    public async Task R181_TheViewerRereadsASnapshotOnATimer_NeverAFollowSession_AndNeverPilesUpReads()
    {
        var cut = RenderSection();
        await cut.InvokeAsync(() => cut.Instance.ViewServiceLogsAsync("nginx"));
        cut.Render();

        Assert.True(cut.Instance.IsFollowingLogs);
        Assert.Equal(TimeSpan.FromSeconds(30), ServiceLogAutoRefresh.Interval);
        Assert.Empty(cut.FindAll("button[aria-label='Refresh']"));
        var logRequests = _handler.RequestDetails.Where(r => r.Url.Contains("api/servers/21/services/logs", StringComparison.Ordinal)).ToList();
        Assert.Single(logRequests);
        Assert.Contains("\"follow\":false", logRequests[0].Body!, StringComparison.OrdinalIgnoreCase);
        int LogRequests() => _handler.Requests.Count(r => r.Method == "POST" && r.Url.Contains("api/servers/21/services/logs"));

        // The first read has not come back: a tick must not queue a second task behind it.
        await cut.InvokeAsync(cut.Instance.RefreshLogsTickAsync);
        Assert.Equal(1, LogRequests());

        // Once it has, the next tick reads again.
        cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 42,
            ServerId = 21,
            TaskName = "Logs - nginx",
            Status = TaskExecutionStatus.Success
        }));
        await cut.InvokeAsync(cut.Instance.RefreshLogsTickAsync);
        cut.WaitForAssertion(() => Assert.Equal(2, LogRequests()));

        // Switched off, ticks do nothing.
        cut.Instance.HandleTaskCompleted(_handler.Logged(new TaskCompletedNotification
        {
            TaskId = 42,
            ServerId = 21,
            TaskName = "Logs - nginx",
            Status = TaskExecutionStatus.Success
        }));
        await cut.InvokeAsync(() => cut.Instance.OnFollowChangedAsync(false));
        await cut.InvokeAsync(cut.Instance.RefreshLogsTickAsync);
        Assert.Equal(2, LogRequests());
    }

    /// <summary>
    /// State dump for the follow-toggle wait above, read through the rendered output rather than the
    /// component's fields: PrivateReflectionBudgetTests refuses a new GetField, and each private field
    /// worth naming here has a visible counterpart anyway. The combination separates the three ways
    /// the restart can go unobserved. IsFollowingLogs still false means the toggle's handler never
    /// ran. A closed pane means CloseLogs won the race, and StartLogStreamAsync then returns on its
    /// _logsServiceName guard before ever posting. An open pane with the toggle on and no POST
    /// recorded means the restart was still in flight when the wait expired, which is the case a
    /// longer timeout would have hidden.
    /// </summary>
    private string DescribeLogStreamState(IRenderedComponent<ServerServicesSection> cut)
    {
        // The whole log card is behind @if (_logsVisible), so its line-count selector is present
        // exactly when the pane is open.
        var paneOpen = cut.FindAll(".log-lines-select").Count > 0;
        var toggle = cut.FindAll("button.omni-switch.omni-switch--text-first").FirstOrDefault();
        var toggleChecked = toggle is null ? "absent" : toggle.GetAttribute("aria-checked") ?? "absent";
        var requests = _handler.Requests.Count == 0
            ? "(none)"
            : string.Join(", ", _handler.Requests.Select(r => $"{r.Method} {r.Url}"));

        return "The follow toggle did not restart the log stream before the wait expired. "
            + $"IsFollowingLogs={cut.Instance.IsFollowingLogs}, logs pane open={paneOpen}, "
            + $"toggle checked={toggleChecked}, renders={cut.RenderCount}. "
            + $"Requests recorded since the toggle: {requests}.";
    }
}
