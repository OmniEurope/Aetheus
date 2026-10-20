// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Covers visible service-operation progress and primary management or Start
/// actions.</summary>
public class ManageableServiceGridTests : BunitContext
{
    public ManageableServiceGridTests() => BunitTestHelper.RegisterServices(this);

    private static ServiceInfoDto Svc(string name, bool running) => new()
    {
        Name = name,
        Status = running ? "active" : "inactive",
        IsRunning = running,
        IsInstalled = true,
        IsManageable = true,
        Type = ServiceType.Systemd
    };

    private IRenderedComponent<ManageableServiceGrid> RenderGrid(
        ServiceInfoDto service, ServiceActionState? state = null, bool showModuleLink = false) =>
        Render<ManageableServiceGrid>(parameters => parameters
            .Add(grid => grid.Services, [service])
            .Add(grid => grid.Installed, true)
            .Add(grid => grid.ShowModuleLink, showModuleLink)
            .Add(grid => grid.CanManagePackages, true)
            .Add(grid => grid.ServerId, 1)
            .Add(grid => grid.ActionStateOf, name => name == service.Name ? state : null));

    [Theory]
    [InlineData(ServiceActionPhase.Queued, "ServiceActionQueued")]
    [InlineData(ServiceActionPhase.Running, "ServiceActionRunning")]
    public void AnActionInFlight_ShowsAnIndeterminateBar_NamedByTheAction(ServiceActionPhase phase, string textKey)
    {
        var cut = RenderGrid(Svc("nginx", running: false), new ServiceActionState(9, "Start", phase));

        // Running work is identified with a progress bar above the grid.
        var banner = cut.Find(".service-actions-in-flight");
        Assert.Contains(textKey + "Banner", banner.TextContent, StringComparison.Ordinal);
        Assert.Equal("/tasks/9", banner.QuerySelector("a")!.GetAttribute("href"));
        var bar = Assert.Single(cut.FindAll("[role='progressbar']"));
        Assert.Contains("omni-progress--indeterminate", bar.ClassList);
        Assert.NotNull(bar.Closest(".service-actions-in-flight"));
        // The line linked to the task stays beside the bar.
        Assert.Equal("/tasks/9", cut.Find(".service-action-state a").GetAttribute("href"));
    }

    [Theory]
    [InlineData(ServiceActionPhase.Succeeded, "ServiceActionSucceeded")]
    [InlineData(ServiceActionPhase.Failed, "ServiceActionFailed")]
    public void AFinishedAction_HasNoBar_AndKeepsItsLine(ServiceActionPhase phase, string textKey)
    {
        var cut = RenderGrid(Svc("nginx", running: true), new ServiceActionState(9, "Start", phase));

        Assert.Empty(cut.FindAll("[role='progressbar']"));
        Assert.Empty(cut.FindAll(".service-actions-in-flight"));
        Assert.Contains(textKey, cut.Find(".service-action-state a").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void NoAction_NoBar()
    {
        var cut = RenderGrid(Svc("nginx", running: true));

        Assert.Empty(cut.FindAll("[role='progressbar']"));
        Assert.Empty(cut.FindAll(".service-action-state"));
    }

    [Fact]
    public void OnAStoppedService_StartIsTheRowsOnlyBlueAction()
    {
        var cut = RenderGrid(Svc("nginx", running: false));

        var start = cut.Find("button[title='Start']");
        Assert.Contains("omni-button--primary", start.ClassList);
        var actions = start.ParentElement!;
        Assert.Single(actions.QuerySelectorAll("button.omni-button--primary"));
    }

    [Fact]
    public void OnARunningService_StopReplacesStart_AndNoActionIsBlue()
    {
        var cut = RenderGrid(Svc("nginx", running: true));

        Assert.Empty(cut.FindAll("button[title='Start']"));
        var stop = cut.Find("button[title='Stop']");
        Assert.Contains("omni-button--danger", stop.ClassList);
        Assert.Empty(stop.ParentElement!.QuerySelectorAll("button.omni-button--primary"));
    }

    [Fact]
    public void OpeningTheServicesManagement_IsBlue()
    {
        var cut = RenderGrid(Svc("docker", running: true), showModuleLink: true);

        var open = cut.Find("button[aria-label='OpenModule: docker']");
        Assert.Contains("omni-button--primary", open.ClassList);
    }

    /// <summary>
    /// SITE-FUNC-HOME-CAPABILITY-001: on the module grid the row opens the service's module, so every
    /// control of the row (checkbox, buttons, task link) must keep its click and key to itself.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void RowControls_DoNotLetTheirClickOrKeyReachTheRow(bool installed, bool showModuleLink)
    {
        var service = Svc("docker", running: false);
        var cut = Render<ManageableServiceGrid>(parameters => parameters
            .Add(grid => grid.Services, [service])
            .Add(grid => grid.Installed, installed)
            .Add(grid => grid.ShowModuleLink, showModuleLink)
            .Add(grid => grid.CanManagePackages, true)
            .Add(grid => grid.ServerOnline, true)
            .Add(grid => grid.ServerId, 1)
            .Add(grid => grid.ActionStateOf, _ => new ServiceActionState(9, "Start", ServiceActionPhase.Succeeded)));

        var controls = cut.FindAll("tbody tr button, tbody tr input, tbody tr a").ToList();
        Assert.NotEmpty(controls);
        foreach (var control in controls)
            Aetheus.Front.Tests.TestDoubles.RowEventIsolation.AssertRowDoesNotReceive(control, "onclick", "onkeydown");
    }

    [Fact]
    public void PostfixOfAManagedMailStack_CannotBeUninstalledHere_AndSaysWhere()
    {
        // Recette R3-24/R3-25: the Mail page reinstalls or removes the stack whole.
        var cut = Render<ManageableServiceGrid>(parameters => parameters
            .Add(grid => grid.Services, [Svc("postfix", running: true)])
            .Add(grid => grid.Installed, true)
            .Add(grid => grid.CanManagePackages, true)
            .Add(grid => grid.MailStackManaged, true)
            .Add(grid => grid.ServerOnline, true)
            .Add(grid => grid.ServerId, 1));

        var uninstall = cut.FindMenuItem("Uninstall")!;
        Assert.True(uninstall.HasAttribute("disabled"));
    }

    [Fact]
    public void R353_TheInstalledGrid_DrawsTheFooterBarWithTheExport()
    {
        var cut = RenderGrid(Svc("nginx", running: true));

        // R3-53: the bar under the rows (OE frame) carries the Markdown export, like the other grids.
        Assert.NotEmpty(cut.FindAll(".omni-data-grid__frame"));
    }

    [Fact]
    public void R3_41_ARunningRow_KeepsStopVisible_AndRestartLogsAndUninstallInItsMenu()
    {
        var cut = RenderGrid(Svc("nginx", running: true));

        var row = cut.Find("button[title='Stop']").ParentElement!;
        var standalone = row.QuerySelectorAll("button:not(.omni-menu__item):not(.omni-overflow-menu__trigger)")
            .Select(b => b.GetAttribute("title")).ToList();
        Assert.Contains("Stop", standalone);
        Assert.DoesNotContain("Restart", standalone);
        Assert.DoesNotContain("ViewLogs", standalone);
        Assert.DoesNotContain("Uninstall", standalone);
        Assert.NotNull(cut.FindMenuItem("Restart"));
        Assert.NotNull(cut.FindMenuItem("ViewLogs"));
        Assert.NotNull(cut.FindMenuItem("Uninstall"));
    }

    [Fact]
    public void R3_41_TheBulkBar_ShowsStartAsTheOneAction_AndKeepsTheOthersInItsMenu()
    {
        var cut = RenderGrid(Svc("nginx", running: true));

        cut.Find("input[aria-label='nginx']").Change(true);

        var bar = cut.Find(".service-bulk-bar");
        var standalone = bar.QuerySelectorAll("button:not(.omni-menu__item):not(.omni-overflow-menu__trigger)")
            .Select(b => b.TextContent.Trim()).ToList();
        Assert.Equal(["Start"], standalone);
        Assert.NotNull(cut.FindMenuItem("Stop"));
        Assert.NotNull(cut.FindMenuItem("Restart"));
        Assert.NotNull(cut.FindMenuItem("Uninstall"));
        Assert.NotNull(cut.FindMenuItem("ClearSelection"));
    }
}
