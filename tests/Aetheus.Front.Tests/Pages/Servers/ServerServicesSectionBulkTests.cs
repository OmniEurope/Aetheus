// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>The bulk bar confirms once, queues one task per checked service, and says how many of them
/// were queued; nothing is queued when the reader declines or the agent is offline.</summary>
public sealed class ServerServicesSectionBulkTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerServicesSectionBulkTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    private OmniOverlayService Toasts => Services.GetRequiredService<OmniOverlayService>();

    private IRenderedComponent<ServerServicesSection> RenderSection(ServerStatus status, Action? onChanged = null) =>
        Render<ServerServicesSection>(p => p
            .Add(x => x.Server, new ServerDetailDto
            {
                Id = 21,
                Name = "svc",
                Hostname = "10.0.0.21",
                Type = ServerType.Normal,
                Status = status,
                Tags = [],
                Services = [new ServiceInfoDto { Name = "nginx", IsInstalled = true, IsManageable = true, IsRunning = true }],
            })
            .Add(x => x.ServerId, 21)
            .Add(x => x.OnServiceChanged, () => onChanged?.Invoke()));

    private static Task BulkAsync(IRenderedComponent<ServerServicesSection> cut, string kind, params string[] services) =>
        cut.InvokeAsync(() => cut.FindComponents<ManageableServiceGrid>()[0].Instance.OnBulk
            .InvokeAsync(new BulkServiceRequest(kind, services)));

    [Fact]
    public async Task ConfirmedRestart_QueuesEachServiceAndReportsTheCount()
    {
        Dialog.ConfirmResult = true;
        _handler.SetJsonResponse("api/servers/21/services/action", new ServiceTaskResponse { TaskId = 501 });
        var changed = 0;
        var cut = RenderSection(ServerStatus.Online, () => changed++);

        await BulkAsync(cut, "Restart", "nginx", "mysql");

        Assert.Equal(2, _handler.Requests.Count(r => r.Url.Contains("api/servers/21/services/action", StringComparison.Ordinal)));
        var toast = Assert.Single(Toasts.Toasts());
        Assert.Equal(OmniSeverity.Success, toast.Severity);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task ABulkStop_IsConfirmedAsADestructiveAction()
    {
        Dialog.ConfirmResult = false;
        var cut = RenderSection(ServerStatus.Online);

        await BulkAsync(cut, "Stop", "nginx");

        Assert.NotNull(Dialog.LastConfirmOptions);
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("/services/", StringComparison.Ordinal));
        Assert.Empty(Toasts.Toasts());
    }

    [Fact]
    public async Task AnOfflineAgent_QueuesNothing()
    {
        Dialog.ConfirmResult = true;
        var cut = RenderSection(ServerStatus.Offline);

        await BulkAsync(cut, "Restart", "nginx");

        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("/services/", StringComparison.Ordinal));
        var toast = Assert.Single(Toasts.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
    }

    [Fact]
    public async Task NothingQueued_IsReportedAsAnError()
    {
        Dialog.ConfirmResult = true;
        _handler.SetResponse("api/servers/21/services/uninstall", System.Net.HttpStatusCode.Conflict);
        var cut = RenderSection(ServerStatus.Online);

        await BulkAsync(cut, "Uninstall", "nginx");

        var toast = Assert.Single(Toasts.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
    }

    [Fact]
    public async Task AnEmptySelection_AsksNothing()
    {
        var cut = RenderSection(ServerStatus.Online);

        await BulkAsync(cut, "Restart");

        Assert.Null(Dialog.LastConfirmMessage);
    }
}
