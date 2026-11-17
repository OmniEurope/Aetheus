// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Reflection;
using Aetheus.Front.Components.Notifications;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Notifications;

public sealed class NotificationRuleEditDialogBehaviorTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public NotificationRuleEditDialogBehaviorTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(),
            sp.GetRequiredService<IJSRuntime>()));
    }

    [Fact]
    public async Task Create_DefaultsToFirstChannel_PostsRule_AndCloses()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/notifications/rules", new NotificationRuleDto
        {
            Id = 6,
            NotificationChannelId = 3,
            EventType = "pipeline.failed"
        });
        var channels = new List<NotificationChannelDto>
        {
            new() { Id = 3, Name = "Ops", Type = NotificationChannelType.Slack }
        };
        var cut = Render<NotificationRuleEditDialog>(p => p.Add(x => x.Channels, channels));
        Set(Model(cut.Instance), "EventType", "pipeline.failed");
        Set(Model(cut.Instance), "FilterJson", "   ");

        await InvokeSubmitAsync(cut);

        Assert.Equal(3, Get<int>(Model(cut.Instance), "NotificationChannelId"));
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("api/notifications/rules", StringComparison.Ordinal));
        Assert.True(Spy.Closed);
    }

    [Fact]
    public async Task Edit_PrefillsRule_AndUsesRuleEndpoint()
    {
        var rule = new NotificationRuleDto
        {
            Id = 6,
            NotificationChannelId = 3,
            ChannelName = "Ops",
            EventType = "pipeline.succeeded",
            FilterJson = "{\"projectId\":7}",
            IsEnabled = true
        };
        _handler.SetJsonResponse(HttpMethod.Put, "api/notifications/rules/6", rule);
        var cut = Render<NotificationRuleEditDialog>(p => p.Add(x => x.Rule, rule));

        Assert.Contains("pipeline.succeeded", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Active", cut.Markup, StringComparison.Ordinal);
        await InvokeSubmitAsync(cut);

        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.EndsWith("api/notifications/rules/6", StringComparison.Ordinal));
        Assert.True(Spy.Closed);
        // R-10: the dialog closes with the rule the server saved, so the list draws it at once.
        Assert.Equal(6, Assert.IsType<NotificationRuleDto>(Spy.LastResult).Id);
    }

    [Fact]
    public async Task Create_ApiFailure_ShowsErrorAndDoesNotClose()
    {
        _handler.SetResponse(HttpMethod.Post, "api/notifications/rules", HttpStatusCode.InternalServerError);
        var cut = Render<NotificationRuleEditDialog>();
        Set(Model(cut.Instance), "EventType", "alert.triggered");

        await InvokeSubmitAsync(cut);

        Assert.False(Spy.Closed);
        Assert.Contains(Services.Toasts(),
            message => message.Severity == OmniSeverity.Danger);
    }

    [Fact]
    public void Cancel_ClosesWithFalse()
    {
        var cut = Render<NotificationRuleEditDialog>();
        cut.InvokeAsync(() => typeof(NotificationRuleEditDialog).GetMethod("Cancel", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(cut.Instance, []));
        Assert.True(Spy.Closed);
        Assert.Equal(false, Spy.LastResult);
    }

    private SpyDialogService Spy => (SpyDialogService)Services.GetRequiredService<OmniDialogService>();
    private static object Model(NotificationRuleEditDialog instance) => typeof(NotificationRuleEditDialog)
        .GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Set(object model, string property, object? value) => model.GetType().GetProperty(property)!.SetValue(model, value);
    private static T Get<T>(object model, string property) => (T)model.GetType().GetProperty(property)!.GetValue(model)!;
    private static Task InvokeSubmitAsync(IRenderedComponent<NotificationRuleEditDialog> cut) => cut.InvokeAsync(() =>
        (Task)typeof(NotificationRuleEditDialog).GetMethod("SubmitAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(cut.Instance, [])!);
}
