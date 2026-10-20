// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Notifications;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Notifications;

public class NotificationsAdminTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public NotificationsAdminTests() => _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);

    private void Seed()
    {
        _handler.SetJsonResponse("api/notifications/channels", new PaginatedResult<NotificationChannelDto>
        {
            Items = [new() { Id = 1, Name = "slack-ops", Type = NotificationChannelType.Slack, IsEnabled = true, RuleCount = 1 }],
            TotalCount = 1,
            Page = 1,
            PageSize = 25
        });
        _handler.SetJsonResponse("api/notifications/rules", new PaginatedResult<NotificationRuleDto>
        {
            Items = [new() { Id = 1, NotificationChannelId = 1, ChannelName = "slack-ops", EventType = "alert.triggered", IsEnabled = true }],
            TotalCount = 1,
            Page = 1,
            PageSize = 25
        });
    }

    [Fact]
    public void Renders_ChannelsAndRules()
    {
        Seed();

        var cut = Render<NotificationsAdmin>();

        cut.WaitForState(() => cut.Markup.Contains("slack-ops"));
        Assert.Contains("slack-ops", cut.Markup);
        Assert.Contains("alert.triggered", cut.Markup);
        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/notifications/channels") &&
            request.Url.Contains("page=1") && request.Url.Contains("pageSize=25"));
        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/notifications/rules") &&
            request.Url.Contains("page=1") && request.Url.Contains("pageSize=25"));
    }

    [Fact]
    public void TestChannel_NotConfigured_ShowsHonestBadge_NotSent()
    {
        Seed();
        _handler.SetJsonResponse("api/notifications/channels/1/test",
            new NotificationTestResultDto { Status = NotificationTestStatus.NotConfigured, Message = "no transport" });

        var cut = Render<NotificationsAdmin>();
        cut.WaitForState(() => cut.Markup.Contains("slack-ops"));

        cut.FindMenuItem("TestConnection")!.Click();

        cut.WaitForState(() => cut.Markup.Contains("Enum_NotificationTestStatus_NotConfigured"));
        Assert.DoesNotContain("Enum_NotificationTestStatus_Sent", cut.Markup);
    }
}
