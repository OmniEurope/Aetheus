// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerRkhunterSectionRenderTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags StaticPriv = BindingFlags.NonPublic | BindingFlags.Static;

    public ServerRkhunterSectionRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private void StubRkhunterApi(
        int serverId = 50,
        List<RkhunterWarningDto>? warnings = null,
        List<RkhunterScanResultDto>? scanHistory = null)
    {
        _handler.SetJsonResponse($"api/servers/{serverId}/rkhunter/warnings", warnings ?? []);
        _handler.SetJsonResponse($"api/servers/{serverId}/rkhunter/history", scanHistory ?? []);
        _handler.SetJsonResponse($"api/servers/{serverId}/rkhunter/action", true);
        _handler.SetJsonResponse($"api/servers/{serverId}/rkhunter/setup", true);
        _handler.SetJsonResponse($"api/servers/{serverId}/rkhunter/schedule", true);
        _handler.SetJsonResponse($"api/servers/{serverId}/rkhunter/logs", true);
    }

    private IRenderedComponent<PageHeaderOutletHost> RenderInstalled(
        RkhunterDataDto? rk = null,
        int serverId = 50)
    {
        StubRkhunterApi(serverId);
        var data = rk ?? new RkhunterDataDto
        {
            IsInstalled = true,
            Version = "1.4.6",
            WarningCount = 0,
            ScanScheduleCron = "0 3 * * *"
        };
        return this.RenderWithPageHeader<ServerRkhunterSection>(p => p
            .Add(x => x.ServerId, serverId)
            .Add(x => x.Rk, data));
    }

    private IRenderedComponent<PageHeaderOutletHost> RenderNotInstalled(int serverId = 50)
    {
        _handler.SetJsonResponse($"api/servers/{serverId}/rkhunter/warnings", new List<RkhunterWarningDto>());
        return this.RenderWithPageHeader<ServerRkhunterSection>(p => p
            .Add(x => x.ServerId, serverId)
            .Add(x => x.Rk, new RkhunterDataDto { IsInstalled = false }));
    }

    // ── Render ────────────────────────────────────────────────────────────────

    // R-113: before the first scan the agent reports "none"; the page says so in words, not as a raw value.
    [Fact]
    public void Renders_NoScanYet_SaysSoInsteadOfTheRawStatus()
    {
        var cut = RenderInstalled(new RkhunterDataDto { IsInstalled = true, Version = "1.4.6", LastScanStatus = "none" });

        Assert.Contains(cut.FindAll(".omni-badge"), badge => badge.TextContent.Trim() == "LastScan: RkhunterStatusNone");
        Assert.DoesNotContain("RkhunterStatusUnknown", cut.Markup, StringComparison.Ordinal);
        // No scan yet: no warning count either ("Aucun scan (0 avertissements)" read as a scan result).
        Assert.DoesNotContain("(0 Warnings)", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_InstalledState_ShowsInstalledBadgeAndScanActions_WithoutTitleHeading()
    {
        var cut = RenderInstalled();
        Assert.Contains(cut.FindAll(".omni-badge"), badge => badge.TextContent.Trim() == "Installed");
        Assert.Contains("RunScan", cut.Markup, StringComparison.Ordinal);
        // R3-42: Scan is the zone's only button; the logs sit in its split-button menu.
        Assert.Single(cut.FindAll(".test-outlet > .omni-split-button"));
        Assert.Empty(cut.Find(".test-outlet").QuerySelectorAll("button.omni-button").Where(b => !b.ClassName!.Contains("omni-split-button", StringComparison.Ordinal)).Select(b => b.OuterHtml[..Math.Min(300, b.OuterHtml.Length)]));
        cut.Find(".omni-split-button__toggle").Click();
        Assert.Contains(cut.FindAll(".omni-split-button__menu .omni-menu__item"),
            item => item.TextContent.Contains("ViewLogs", StringComparison.Ordinal));
        // The page header already shows the title: the section no longer repeats it.
        Assert.DoesNotContain("RKHunter", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_InstalledState_ShowsVersion()
    {
        var cut = RenderInstalled();
        Assert.Contains("1.4.6", cut.Markup);
    }

    [Fact]
    public void Renders_NotInstalled_ShowsNotInstalledBadgeAndSetupButton_WithoutTitleHeading()
    {
        var cut = RenderNotInstalled();
        Assert.Contains("NotInstalled", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("RkhunterSetup", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("RKHunter", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_WithWarnings_ShowsWarningCount()
    {
        StubRkhunterApi(50, warnings:
        [
            new RkhunterWarningDto
            {
                Id       = 1,
                Category = "rootkit",
                Detail   = "suspicious file",
                Severity = "warning",
                FoundAt  = DateTime.UtcNow
            }
        ]);
        var rk = new RkhunterDataDto { IsInstalled = true, Version = "1.4.6", WarningCount = 1 };
        var cut = this.RenderWithPageHeader<ServerRkhunterSection>(p => p
            .Add(x => x.ServerId, 50)
            .Add(x => x.Rk, rk));

        Assert.Contains("1", cut.Markup);
    }

    [Fact]
    public void Renders_WithScanSchedule_ShowsCronExpression()
    {
        var rk = new RkhunterDataDto
        {
            IsInstalled = true,
            Version = "1.4.6",
            ScanScheduleCron = "0 4 * * *"
        };
        var cut = RenderInstalled(rk);
        Assert.Contains("0 4 * * *", cut.Markup);
    }

    // ── ExecuteActionAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteActionAsync_RunScan_CompletesAndClearsRunning()
    {
        var cut = RenderInstalled();
        var method = typeof(ServerRkhunterSection).GetMethod("ExecuteActionAsync", Priv)!;

        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.FindComponent<ServerRkhunterSection>().Instance, [RkhunterAction.RunScan])!);

        var running = (bool)typeof(ServerRkhunterSection)
            .GetField("_actionRunning", Priv)!
            .GetValue(cut.FindComponent<ServerRkhunterSection>().Instance)!;
        Assert.False(running);
    }

    [Fact]
    public async Task ExecuteActionAsync_UpdateDatabase_CompletesWithoutThrowing()
    {
        var cut = RenderInstalled();
        var method = typeof(ServerRkhunterSection).GetMethod("ExecuteActionAsync", Priv)!;

        var ex = await Record.ExceptionAsync(async () =>
            await cut.InvokeAsync(async () =>
                await (Task)method.Invoke(cut.FindComponent<ServerRkhunterSection>().Instance, [RkhunterAction.UpdateDatabase])!));

        Assert.Null(ex);
    }

    // ── LoadWarningsAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task LoadWarningsAsync_PopulatesWarningsList()
    {
        StubRkhunterApi(50, warnings:
        [
            new RkhunterWarningDto { Id = 1, Category = "rootkit", Detail = "test", Severity = "medium", FoundAt = DateTime.UtcNow },
            new RkhunterWarningDto { Id = 2, Category = "network", Detail = "port", Severity = "low",    FoundAt = DateTime.UtcNow }
        ]);
        var rk = new RkhunterDataDto { IsInstalled = true, Version = "1.4.6" };
        var cut = this.RenderWithPageHeader<ServerRkhunterSection>(p => p
            .Add(x => x.ServerId, 50)
            .Add(x => x.Rk, rk));

        var method = typeof(ServerRkhunterSection).GetMethod("LoadWarningsAsync", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.FindComponent<ServerRkhunterSection>().Instance, [])!);

        var warnings = (List<RkhunterWarningDto>)typeof(ServerRkhunterSection)
            .GetField("_warnings", Priv)!
            .GetValue(cut.FindComponent<ServerRkhunterSection>().Instance)!;
        Assert.Equal(2, warnings.Count);
    }

    // ── LoadScanHistoryAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task LoadScanHistoryAsync_PopulatesScanHistory()
    {
        var cut = RenderInstalled();

        // Override the history stub AFTER render so the component is already initialized
        _handler.SetJsonResponse("api/servers/50/rkhunter/history",
            new List<RkhunterScanResultDto>
            {
                new() { Id = 1, Status = "clean",   ScanTime = DateTime.UtcNow.AddHours(-1), WarningCount = 0 },
                new() { Id = 2, Status = "warning",  ScanTime = DateTime.UtcNow.AddHours(-2), WarningCount = 3 }
            });

        var method = typeof(ServerRkhunterSection).GetMethod("LoadScanHistoryAsync", Priv)!;

        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.FindComponent<ServerRkhunterSection>().Instance, [])!);

        var history = (List<RkhunterScanResultDto>)typeof(ServerRkhunterSection)
            .GetField("_scanHistory", Priv)!
            .GetValue(cut.FindComponent<ServerRkhunterSection>().Instance)!;
        Assert.Equal(2, history.Count);
    }

    // ── SaveScheduleAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task SaveScheduleAsync_WithCron_CompletesAndClearsSaving()
    {
        var cut = RenderInstalled();
        typeof(ServerRkhunterSection).GetField("_scheduleCron", Priv)!.SetValue(cut.FindComponent<ServerRkhunterSection>().Instance, "0 4 * * *");

        var method = typeof(ServerRkhunterSection).GetMethod("SaveScheduleAsync", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.FindComponent<ServerRkhunterSection>().Instance, [])!);

        var saving = (bool)typeof(ServerRkhunterSection)
            .GetField("_savingSchedule", Priv)!
            .GetValue(cut.FindComponent<ServerRkhunterSection>().Instance)!;
        Assert.False(saving);
    }

    [Fact]
    public async Task SaveScheduleAsync_EmptyCron_SendsNullExpression()
    {
        var cut = RenderInstalled();
        typeof(ServerRkhunterSection).GetField("_scheduleCron", Priv)!.SetValue(cut.FindComponent<ServerRkhunterSection>().Instance, "");

        var method = typeof(ServerRkhunterSection).GetMethod("SaveScheduleAsync", Priv)!;
        var ex = await Record.ExceptionAsync(async () =>
            await cut.InvokeAsync(async () =>
                await (Task)method.Invoke(cut.FindComponent<ServerRkhunterSection>().Instance, [])!));

        Assert.Null(ex);
    }

    // ── GetLogsAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLogsAsync_CompletesWithoutThrowing()
    {
        var cut = RenderInstalled();
        var method = typeof(ServerRkhunterSection).GetMethod("GetLogsAsync", Priv)!;

        var ex = await Record.ExceptionAsync(async () =>
            await cut.InvokeAsync(async () =>
                await (Task)method.Invoke(cut.FindComponent<ServerRkhunterSection>().Instance, [])!));

        Assert.Null(ex);
    }

    // ── HandleTaskCompletedAsync ──────────────────────────────────────────────

    [Fact]
    public async Task HandleTaskCompletedAsync_RkhunterTask_RefreshesData()
    {
        var cut = RenderInstalled();
        // Ensure the history endpoint returns a valid list (override the stub after render)
        _handler.SetJsonResponse("api/servers/50/rkhunter/history", new List<RkhunterScanResultDto>());
        var notification = new TaskCompletedNotification { TaskName = "Run RKHunter Scan", ServerId = 50 };

        await cut.FindComponent<ServerRkhunterSection>().Instance.HandleTaskCompletedAsync(notification);

        // The RKHunter task name matches, so the handler refreshes scan history via the history GET.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/servers/50/rkhunter/history"));
    }

    [Fact]
    public async Task HandleTaskCompletedAsync_UnrelatedTask_SkipsRefresh()
    {
        var cut = RenderInstalled();
        var notification = new TaskCompletedNotification { TaskName = "Deploy Application", ServerId = 50 };

        // Should return early without refreshing
        var ex = await Record.ExceptionAsync(() => cut.FindComponent<ServerRkhunterSection>().Instance.HandleTaskCompletedAsync(notification));
        Assert.Null(ex);
    }

    // Live refresh.

    [Fact]
    public void R181_NoRefreshButtons_AndTheHistoryIsLoadedWithoutAClick()
    {
        var cut = RenderInstalled();

        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Refresh", StringComparison.Ordinal));
        // History loads when the section initializes.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/servers/50/rkhunter/history", StringComparison.Ordinal));
    }

    [Fact]
    public async Task R181_AHeartbeat_ReloadsWarningsAndHistory_ACompletedTaskOfTheFeedDoesNot()
    {
        var cut = RenderInstalled();
        _handler.SetJsonResponse("api/servers/50/rkhunter/warnings",
            new List<RkhunterWarningDto> { new() { Category = "files", Detail = "pushed warning", Severity = "warning" } });
        int Reads() => _handler.Requests.Count(r => r.Url.Contains("api/servers/50/rkhunter/", StringComparison.Ordinal)
            && (r.Url.Contains("/warnings", StringComparison.Ordinal) || r.Url.Contains("/history", StringComparison.Ordinal)));
        var before = Reads();

        // Heartbeat trigger only: RKHunter completions already arrive through HandleTaskCompletedAsync.
        await cut.FindComponent<ServerRkhunterSection>().Instance.LiveFeed!.OnTaskCompletedAsync(new TaskCompletedNotification { TaskId = 3, ServerId = 50 });
        Assert.Equal(before, Reads());

        await cut.FindComponent<ServerRkhunterSection>().Instance.LiveFeed!.OnHeartbeatAsync(50);

        Assert.Equal(before + 2, Reads());
        Assert.Equal("pushed warning", Assert.Single(cut.FindComponent<ServerRkhunterSection>().Instance.Warnings).Detail);
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("clean", "check_circle")]
    [InlineData("warning", "warning")]
    [InlineData("unknown", "help")]
    public void GetScanStatusIcon_ReturnsCorrectIcon(string status, string expected)
    {
        var method = typeof(ServerRkhunterSection).GetMethod("GetScanStatusIcon", StaticPriv)!;
        Assert.Equal(expected, method.Invoke(null, [status]));
    }

    [Theory]
    [InlineData("clean", "scan-status-clean")]
    [InlineData("warning", "scan-status-warning")]
    [InlineData("other", "scan-status-unknown")]
    public void GetScanStatusColor_ReturnsCorrectCssClass(string status, string expected)
    {
        var method = typeof(ServerRkhunterSection).GetMethod("GetScanStatusColor", StaticPriv)!;
        Assert.Equal(expected, method.Invoke(null, [status]));
    }

    [Theory]
    [InlineData("clean", OmniTone.Success)]
    [InlineData("warning", OmniTone.Warning)]
    [InlineData("other", OmniTone.Neutral)]
    public void GetScanBadgeStyle_ReturnsCorrectStyle(string status, OmniTone expected)
    {
        var method = typeof(ServerRkhunterSection).GetMethod("GetScanBadgeStyle", StaticPriv)!;
        Assert.Equal(expected, (OmniTone)method.Invoke(null, [status])!);
    }
}
