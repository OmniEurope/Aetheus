// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Notifications;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Notifications;

/// <summary>A saved channel dialog changes what both grids and the rule filters show, so the page re-reads
/// the filter values, the channels and the rules; a dismissed dialog re-reads nothing.</summary>
public sealed class NotificationsAdminChannelDialogTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialogs;

    public NotificationsAdminChannelDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialogs = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
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
        _handler.SetJsonResponse("api/notifications/rules/filter-values", new NotificationAdminFilterValuesDto());
    }

    private int Reads(string route) => _handler.Requests.Count(request => request.Url.Contains(route, StringComparison.Ordinal));

    private async Task<(int Channels, int Rules, int FilterValues)> CreateChannelAsync(bool? saved)
    {
        var cut = Render<NotificationsAdmin>();
        cut.WaitForState(() => cut.Markup.Contains("alert.triggered"));
        var before = (Reads("api/notifications/channels?"), Reads("api/notifications/rules?"), Reads("rules/filter-values"));
        _dialogs.OpenResult = saved;

        await cut.FindAll("button").First(button => button.TextContent.Contains("Create", StringComparison.Ordinal)).ClickAsync(new());

        Assert.Equal(typeof(NotificationChannelEditDialog), _dialogs.LastComponent);
        Assert.Null(_dialogs.LastParameters!["ChannelId"]);
        return (Reads("api/notifications/channels?") - before.Item1, Reads("api/notifications/rules?") - before.Item2,
            Reads("rules/filter-values") - before.Item3);
    }

    [Fact]
    public async Task ASavedChannel_RereadsTheFilterValuesAndBothGrids()
    {
        var (channels, rules, filterValues) = await CreateChannelAsync(saved: true);

        Assert.Equal(1, filterValues);
        Assert.True(channels >= 1, $"channels re-read {channels} time(s)");
        Assert.True(rules >= 1, $"rules re-read {rules} time(s)");
    }

    [Fact]
    public async Task ADismissedChannelDialog_RereadsNothing()
    {
        var (channels, rules, filterValues) = await CreateChannelAsync(saved: null);

        Assert.Equal((0, 0, 0), (channels, rules, filterValues));
    }
}
