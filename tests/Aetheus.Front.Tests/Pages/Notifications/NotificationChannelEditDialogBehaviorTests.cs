// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Reflection;
using Aetheus.Front.Components.Notifications;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Pages.Notifications;

public sealed class NotificationChannelEditDialogBehaviorTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public NotificationChannelEditDialogBehaviorTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(),
            sp.GetRequiredService<IJSRuntime>()));
    }

    [Fact]
    public async Task Create_PostsChannel_AndClosesWithSuccess()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/notifications/channels",
            new NotificationChannelDto { Id = 4, Name = "Ops Slack", Type = NotificationChannelType.Slack });
        var cut = Render<NotificationChannelEditDialog>();
        Set(Model(cut.Instance), "Name", "Ops Slack");
        Set(Model(cut.Instance), "ConfigurationJson", "   ");

        await InvokeSubmitAsync(cut);

        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("api/notifications/channels", StringComparison.Ordinal));
        Assert.True(Spy.Closed);
        Assert.Equal(true, Spy.LastResult);
    }

    [Fact]
    public async Task Edit_LoadsChannel_AndUsesChannelEndpoint()
    {
        var channel = new NotificationChannelDto
        {
            Id = 4,
            Name = "Mail alerts",
            Type = NotificationChannelType.Email,
            ConfigurationJson = "{\"to\":\"ops@example.test\"}",
            IsEnabled = true
        };
        _handler.SetJsonResponse(HttpMethod.Get, "api/notifications/channels/4", channel);
        _handler.SetJsonResponse(HttpMethod.Put, "api/notifications/channels/4", channel);
        var cut = Render<NotificationChannelEditDialog>(p => p.Add(x => x.ChannelId, 4));

        Assert.Contains("Mail alerts", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Active", cut.Markup, StringComparison.Ordinal);
        await InvokeSubmitAsync(cut);

        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.EndsWith("api/notifications/channels/4", StringComparison.Ordinal));
        Assert.True(Spy.Closed);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "LoadFailed")]
    [InlineData(HttpStatusCode.NotFound, "NotificationChannelNotFound")]
    public async Task Edit_LoadFailure_ShowsTheFailure_AndNeverOverwritesTheChannel(HttpStatusCode status, string message)
    {
        // FRONT-004: the blank model ("{}", enabled) used to be submittable after a failed GET.
        _handler.SetResponse(HttpMethod.Get, "api/notifications/channels/4", status);
        _handler.SetJsonResponse(HttpMethod.Put, "api/notifications/channels/4", new NotificationChannelDto { Id = 4 });

        var cut = Render<NotificationChannelEditDialog>(p => p.Add(x => x.ChannelId, 4));

        Assert.Contains(message, cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ChannelConfiguration", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("button[type='submit']"));
        Set(Model(cut.Instance), "Name", "Retyped name");
        await InvokeSubmitAsync(cut);
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "PUT");
    }

    [Fact]
    public void Cancel_ClosesWithFalse()
    {
        var cut = Render<NotificationChannelEditDialog>();
        cut.InvokeAsync(() => typeof(NotificationChannelEditDialog).GetMethod("Cancel", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(cut.Instance, []));
        Assert.True(Spy.Closed);
        Assert.Equal(false, Spy.LastResult);
    }

    private SpyDialogService Spy => (SpyDialogService)Services.GetRequiredService<OmniDialogService>();
    private static object Model(NotificationChannelEditDialog instance) => typeof(NotificationChannelEditDialog)
        .GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Set(object model, string property, object? value) => model.GetType().GetProperty(property)!.SetValue(model, value);
    private static Task InvokeSubmitAsync(IRenderedComponent<NotificationChannelEditDialog> cut) => cut.InvokeAsync(() =>
        (Task)typeof(NotificationChannelEditDialog).GetMethod("SubmitAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(cut.Instance, [])!);
}
