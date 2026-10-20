// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Recette R3-59: the Services page lists every installed package, read-only (no action on any row),
/// under OE's veil while the agent reads, and never shows a partial read as the full list.</summary>
public sealed class ServerPackageInventoryTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerPackageInventoryTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private TaskTrackerService Tracker => Services.GetRequiredService<TaskTrackerService>();

    private static ServerDetailDto Server(bool canList = true, string os = "Debian GNU/Linux 12") => new()
    {
        Id = 21,
        Name = "pkg-srv",
        Hostname = "10.0.0.21",
        Status = ServerStatus.Online,
        LastHeartbeat = DateTime.Now,
        OsDescription = os,
        Tags = [],
        AgentCapabilities = canList ? [AgentCapabilities.PackageInventory, AgentCapabilities.ShellExecution] : [AgentCapabilities.ShellExecution]
    };

    private IRenderedComponent<ServerPackageInventory> RenderInventory(ServerDetailDto? server = null) =>
        Render<ServerPackageInventory>(p => p.Add(x => x.Server, server ?? Server()).Add(x => x.ServerId, 21));

    private void StubQueue(int taskId) =>
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/21/packages/inventory", new ServiceTaskResponse { TaskId = taskId });

    private void StubOutput(int taskId, params string[] lines) =>
        _handler.SetJsonResponse($"api/logs/task/{taskId}",
            lines.Select((line, index) => new TaskLogDto { Id = index + 1, TaskId = taskId, Message = line }).ToList());

    private Task CompleteAsync(int taskId, TaskExecutionStatus status) =>
        Renderer.Dispatcher.InvokeAsync(() => Tracker.HandleTerminal(new TaskCompletedNotification
        {
            TaskId = taskId,
            ServerId = 21,
            TaskName = InstalledPackages.TaskName,
            Status = status
        }));

    [Fact]
    public void R3_59_TheListIsMarkedReadOnly_WithAHelpMark()
    {
        var cut = RenderInventory();

        Assert.Contains("ReadOnly", cut.Find(".package-inventory-readonly").TextContent, StringComparison.Ordinal);
        Assert.Equal("InstalledPackagesReadOnlyHelp", cut.Find(".package-inventory-help").GetAttribute("aria-label"));
        Assert.NotEmpty(cut.FindAll("button.package-inventory-load"));
    }

    [Fact]
    public async Task R3_59_TheRead_RunsUnderTheVeil_ThenShowsEveryPackage_WithNoAction()
    {
        StubQueue(400);
        StubOutput(400,
            "PACKAGE\tmongodb-mongosh\t2.3.1",
            "PACKAGE\tcurl\t7.88.1-10",
            "PACKAGE\tapache2\t2.4.62-1",
            "TOTAL\t3");
        var cut = RenderInventory();

        cut.Find("button.package-inventory-load").Click();

        cut.WaitForAssertion(() => Assert.True(cut.Instance.Busy));
        var card = cut.Find(".server-package-inventory");
        Assert.Contains("omni-busy", card.ClassList);
        Assert.Equal("true", card.GetAttribute("aria-busy"));
        Assert.Contains("/tasks/400", cut.Markup, StringComparison.Ordinal);

        await CompleteAsync(400, TaskExecutionStatus.Success);

        cut.WaitForAssertion(() => Assert.False(cut.Instance.Busy));
        var grid = cut.Find(".package-inventory-grid");
        Assert.Contains("mongodb-mongosh", grid.TextContent, StringComparison.Ordinal);
        Assert.Contains("7.88.1-10", grid.TextContent, StringComparison.Ordinal);
        Assert.Contains("apache2", grid.TextContent, StringComparison.Ordinal);
        Assert.Equal(["apache2", "curl", "mongodb-mongosh"], cut.Instance.Report!.Packages.Select(p => p.Name));
        // Read-only: the grid body carries no button at all (no uninstall, no row action).
        Assert.Empty(grid.QuerySelectorAll("tbody button"));
        Assert.Empty(cut.FindAll(".package-inventory-failed"));
    }

    [Fact]
    public async Task R3_59_AReadWhoseTotalIsMissing_IsNeverShownAsTheList()
    {
        StubQueue(401);
        StubOutput(401, "PACKAGE\tcurl\t7.88.1-10", "[agent] 12 log line(s) dropped while the backend was unreachable");
        var cut = RenderInventory();
        cut.Find("button.package-inventory-load").Click();
        cut.WaitForAssertion(() => Assert.True(cut.Instance.Busy));

        await CompleteAsync(401, TaskExecutionStatus.Success);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".package-inventory-failed")));
        Assert.Contains("dropped", cut.Find(".package-inventory-failed").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".package-inventory-grid"));
        Assert.Null(cut.Instance.Report);
    }

    [Fact]
    public async Task R3_59_AFailedRead_ShowsItsCause()
    {
        StubQueue(402);
        StubOutput(402, "No package database could be read (dpkg-query, rpm)");
        var cut = RenderInventory();
        cut.Find("button.package-inventory-load").Click();
        cut.WaitForAssertion(() => Assert.True(cut.Instance.Busy));

        await CompleteAsync(402, TaskExecutionStatus.Failed);

        cut.WaitForAssertion(() => Assert.Contains("No package database could be read",
            cut.Find(".package-inventory-failed").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void R3_59_BackOnThePage_ARunningReadIsFollowedAgain()
    {
        Tracker.Upsert(new ServerTaskDto { Id = 403, ServerId = 21, Name = InstalledPackages.TaskName, Status = TaskExecutionStatus.Running });

        var cut = RenderInventory();

        Assert.True(cut.Instance.Busy);
    }

    [Fact]
    public void R3_59_AnAgentTooOld_SaysSo_AndOffersNoButton()
    {
        var cut = RenderInventory(Server(canList: false));

        Assert.NotEmpty(cut.FindAll(".package-inventory-agent-old"));
        Assert.Empty(cut.FindAll("button.package-inventory-load"));
    }

    [Fact]
    public void R3_59_AWindowsServer_HasNoPackageList()
    {
        var cut = RenderInventory(Server(os: "Microsoft Windows 10.0.26100"));

        Assert.Empty(cut.FindAll(".server-package-inventory"));
    }
}
