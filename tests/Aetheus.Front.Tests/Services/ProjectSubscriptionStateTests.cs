// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Notifications;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Services;

/// <summary>Following or leaving a project flips the shared state only when the server accepted it,
/// and never while the state is unknown or a toggle is already running.</summary>
public sealed class ProjectSubscriptionStateTests : BunitContext
{
    private const string Subscriptions = "api/notifications/me/subscriptions";
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectSubscriptionStateTests() => _handler = BunitTestHelper.RegisterServices(this);

    private OmniOverlayService Toasts => Services.GetRequiredService<OmniOverlayService>();

    private async Task<ProjectSubscriptionState> LoadedAsync(bool subscribed)
    {
        _handler.SetJsonResponse(HttpMethod.Get, Subscriptions,
            subscribed ? new List<ProjectSubscriptionDto> { new() { ProjectId = 7 } } : []);
        var state = new ProjectSubscriptionState(Services.GetRequiredService<ApiClient>(), Services.GetRequiredService<NotifyHelper>());
        Assert.True(await state.LoadAsync(7));
        Assert.Equal(subscribed, state.Subscribed);
        return state;
    }

    [Fact]
    public async Task AnUnknownState_TogglesNothing()
    {
        var state = new ProjectSubscriptionState(Services.GetRequiredService<ApiClient>(), Services.GetRequiredService<NotifyHelper>());

        await state.ToggleAsync();

        Assert.Empty(_handler.Requests);
        Assert.Null(state.Subscribed);
    }

    [Fact]
    public async Task LeavingAFollowedProject_ClearsTheState()
    {
        var state = await LoadedAsync(subscribed: true);
        _handler.SetResponse(HttpMethod.Delete, $"{Subscriptions}/7", HttpStatusCode.NoContent);

        await state.ToggleAsync();

        Assert.False(state.Subscribed);
        Assert.False(state.Busy);
        Assert.Equal(OmniSeverity.Success, Assert.Single(Toasts.Toasts()).Severity);
    }

    [Fact]
    public async Task ARefusedLeave_KeepsTheProjectFollowed()
    {
        var state = await LoadedAsync(subscribed: true);
        _handler.SetResponse(HttpMethod.Delete, $"{Subscriptions}/7", HttpStatusCode.InternalServerError);

        await state.ToggleAsync();

        Assert.True(state.Subscribed);
        Assert.Equal(OmniSeverity.Danger, Assert.Single(Toasts.Toasts()).Severity);
    }

    [Fact]
    public async Task FollowingAProject_SetsTheState()
    {
        var state = await LoadedAsync(subscribed: false);
        _handler.SetJsonResponse(HttpMethod.Put, $"{Subscriptions}/7", new ProjectSubscriptionDto { ProjectId = 7 });

        await state.ToggleAsync();

        Assert.True(state.Subscribed);
        Assert.Equal(OmniSeverity.Success, Assert.Single(Toasts.Toasts()).Severity);
    }

    [Fact]
    public async Task ARefusedFollow_LeavesTheProjectUnfollowed()
    {
        var state = await LoadedAsync(subscribed: false);
        _handler.SetResponse(HttpMethod.Put, $"{Subscriptions}/7", HttpStatusCode.Conflict);

        await state.ToggleAsync();

        Assert.False(state.Subscribed);
        Assert.Equal(OmniSeverity.Danger, Assert.Single(Toasts.Toasts()).Severity);
    }
}
