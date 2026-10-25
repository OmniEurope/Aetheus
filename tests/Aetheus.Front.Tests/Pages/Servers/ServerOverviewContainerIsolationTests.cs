// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Enforcing containers-only on a runner is confirmed first, relaxing it is not, and the page
/// shows the policy the server answered.</summary>
public sealed class ServerOverviewContainerIsolationTests : BunitContext
{
    private const string PolicyUrl = "api/servers/1/container-isolation-policy";
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerOverviewContainerIsolationTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.GetRequiredService<PermissionService>().SetPermissions([], isAdmin: true);
        // The container isolation tile sits in the runner tab.
        ServerOverviewTabsTests.OpenTab(this, "runner");
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    private OmniOverlayService Toasts => Services.GetRequiredService<OmniOverlayService>();

    private IRenderedComponent<ServerOverviewSection> RenderSection(bool required, Action<ServerDetailDto>? changed = null) =>
        Render<ServerOverviewSection>(p => p
            .Add(x => x.Server, new ServerDetailDto
            {
                Id = 1,
                Name = "runner",
                Hostname = "runner.local",
                Type = ServerType.Normal,
                Status = ServerStatus.Online,
                Tags = [],
                Services = [],
                DockerAvailable = true,
                RequireContainerIsolation = required,
            })
            .Add(x => x.MetricsReceived, true)
            .Add(x => x.ServerChanged, server => changed?.Invoke(server)));

    private static Task ClickAsync(IRenderedComponent<ServerOverviewSection> cut, string label) =>
        cut.FindAll("button").Single(button => button.TextContent.Contains(label, StringComparison.Ordinal)).ClickAsync(new());

    [Fact]
    public async Task EnforcingDeclined_ChangesNothing()
    {
        Dialog.ConfirmResult = false;
        var cut = RenderSection(required: false);

        await ClickAsync(cut, "Enforce");

        Assert.NotNull(Dialog.LastConfirmMessage);
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains(PolicyUrl, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnforcingConfirmed_ShowsTheAnsweredPolicy()
    {
        Dialog.ConfirmResult = true;
        _handler.SetJsonResponse(PolicyUrl, new ServerDto { Id = 1, RequireContainerIsolation = true });
        ServerDetailDto? changed = null;
        var cut = RenderSection(required: false, server => changed = server);

        await ClickAsync(cut, "Enforce");

        Assert.True(changed?.RequireContainerIsolation);
        Assert.Contains("Relax", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(OmniSeverity.Success, Assert.Single(Toasts.Toasts()).Severity);
    }

    [Fact]
    public async Task Relaxing_IsNotConfirmed()
    {
        _handler.SetJsonResponse(PolicyUrl, new ServerDto { Id = 1, RequireContainerIsolation = false });
        var cut = RenderSection(required: true);

        await ClickAsync(cut, "Relax");

        Assert.Null(Dialog.LastConfirmMessage);
        Assert.Contains(_handler.RequestDetails, request => request.Url.Contains(PolicyUrl, StringComparison.Ordinal)
            && request.Body!.Contains("false", StringComparison.Ordinal));
        Assert.Equal(OmniSeverity.Success, Assert.Single(Toasts.Toasts()).Severity);
    }

    [Fact]
    public async Task ARefusedChange_IsReportedAndKeepsThePolicy()
    {
        _handler.SetResponse(PolicyUrl, System.Net.HttpStatusCode.Forbidden);
        var cut = RenderSection(required: true);

        await ClickAsync(cut, "Relax");

        Assert.Equal(OmniSeverity.Danger, Assert.Single(Toasts.Toasts()).Severity);
        Assert.Contains("Relax", cut.Markup, StringComparison.Ordinal);
    }
}
