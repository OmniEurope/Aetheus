// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Installing and uninstalling a service are confirmed, refused for an offline agent, and
/// report whether the task was queued; TeamSpeak installs through its own module.</summary>
public sealed class ServerServicesSectionInstallTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerServicesSectionInstallTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    private OmniOverlayService Toasts => Services.GetRequiredService<OmniOverlayService>();

    private IRenderedComponent<ServerServicesSection> RenderSection(ServerStatus status, Action? changed = null) =>
        Render<ServerServicesSection>(p => p
            .Add(x => x.Server, new ServerDetailDto
            {
                Id = 21,
                Name = "svc",
                Hostname = "10.0.0.21",
                Type = ServerType.Normal,
                Status = status,
                Tags = [],
                Services =
                [
                    new ServiceInfoDto { Name = "docker", IsInstalled = true, IsManageable = true, IsRunning = true },
                    new ServiceInfoDto { Name = "redis", IsInstalled = false, IsManageable = true },
                ],
            })
            .Add(x => x.ServerId, 21)
            .Add(x => x.OnServiceChanged, () => changed?.Invoke()));

    private static Task InvokeAsync(IRenderedComponent<ServerServicesSection> cut, Func<ManageableServiceGrid, EventCallback<string>> pick, string service) =>
        cut.InvokeAsync(() => pick(cut.FindComponents<ManageableServiceGrid>().Select(grid => grid.Instance).First(grid => pick(grid).HasDelegate))
            .InvokeAsync(service));

    private OmniSeverity LastToast => Assert.Single(Toasts.Toasts()).Severity;

    [Fact]
    public async Task AConfirmedInstall_QueuesTheTask()
    {
        Dialog.ConfirmResult = true;
        _handler.SetJsonResponse("api/servers/21/services/install", new ServiceTaskResponse { TaskId = 77 });
        var cut = RenderSection(ServerStatus.Online);

        await InvokeAsync(cut, grid => grid.OnInstall, "redis");

        Assert.Contains(_handler.RequestDetails, r => r.Url.Contains("services/install", StringComparison.Ordinal) && r.Body!.Contains("redis", StringComparison.Ordinal));
        Assert.Equal(OmniSeverity.Success, LastToast);
    }

    [Fact]
    public async Task ARefusedInstall_IsReportedAsAnError()
    {
        Dialog.ConfirmResult = true;
        _handler.SetResponse("api/servers/21/services/install", System.Net.HttpStatusCode.Conflict);
        var cut = RenderSection(ServerStatus.Online);

        await InvokeAsync(cut, grid => grid.OnInstall, "redis");

        Assert.Equal(OmniSeverity.Danger, LastToast);
    }

    [Theory]
    [InlineData(true, OmniSeverity.Success, 1)]
    [InlineData(false, OmniSeverity.Danger, 0)]
    public async Task TeamSpeak_InstallsThroughItsModule(bool accepted, OmniSeverity severity, int changes)
    {
        Dialog.ConfirmResult = true;
        _handler.SetResponse("api/servers/21/teamspeak/setup",
            accepted ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.BadRequest);
        var changed = 0;
        var cut = RenderSection(ServerStatus.Online, () => changed++);

        await InvokeAsync(cut, grid => grid.OnInstall, "teamspeak");

        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("services/install", StringComparison.Ordinal));
        Assert.Equal(severity, LastToast);
        Assert.Equal(changes, changed);
    }

    [Fact]
    public async Task ADeclinedInstall_QueuesNothing()
    {
        Dialog.ConfirmResult = false;
        var cut = RenderSection(ServerStatus.Online);

        await InvokeAsync(cut, grid => grid.OnInstall, "redis");

        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("/services/", StringComparison.Ordinal));
        Assert.Empty(Toasts.Toasts());
    }

    [Fact]
    public async Task AnOfflineAgent_InstallsNothing()
    {
        var cut = RenderSection(ServerStatus.Offline);

        await InvokeAsync(cut, grid => grid.OnInstall, "redis");

        Assert.Null(Dialog.LastConfirmMessage);
        Assert.Equal(OmniSeverity.Danger, LastToast);
    }

    [Theory]
    [InlineData(true, OmniSeverity.Success)]
    [InlineData(false, OmniSeverity.Danger)]
    public async Task AConfirmedUninstall_ReportsWhetherItWasQueued(bool accepted, OmniSeverity severity)
    {
        Dialog.ConfirmResult = true;
        if (accepted) _handler.SetJsonResponse("api/servers/21/services/uninstall", new ServiceTaskResponse { TaskId = 78 });
        else _handler.SetResponse("api/servers/21/services/uninstall", System.Net.HttpStatusCode.Conflict);
        var cut = RenderSection(ServerStatus.Online);

        await InvokeAsync(cut, grid => grid.OnUninstall, "docker");

        Assert.True(Dialog.LastConfirmOptions?.Destructive);
        Assert.Equal(severity, LastToast);
    }

    [Fact]
    public async Task AnUninstallOnAnOfflineAgent_AsksNothing()
    {
        var cut = RenderSection(ServerStatus.Offline);

        await InvokeAsync(cut, grid => grid.OnUninstall, "docker");

        Assert.Null(Dialog.LastConfirmMessage);
    }
}
