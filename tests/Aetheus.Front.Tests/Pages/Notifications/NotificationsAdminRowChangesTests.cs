// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Notifications;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Notifications;

/// <summary>R-10 on the channels and rules grids: a saved channel or rule shows what the dialog's save answered and a
/// deleted one leaves the table, all before the list is read again and without the grid's blocking bar.</summary>
public sealed class NotificationsAdminRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialogs;

    public NotificationsAdminRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialogs = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _dialogs.ConfirmResult = true;
        _handler.SetJsonResponse("api/notifications/rules/filter-values", new NotificationAdminFilterValuesDto());
        _handler.SetJsonResponse(HttpMethod.Get, "api/notifications/channels?", Channels(Channel(1, "slack-ops")));
        _handler.SetJsonResponse(HttpMethod.Get, "api/notifications/rules?", Rules(Rule(1, "alert.triggered")));
    }

    private static NotificationChannelDto Channel(int id, string name) =>
        new() { Id = id, Name = name, Type = NotificationChannelType.Slack, IsEnabled = true };

    private static NotificationRuleDto Rule(int id, string eventType) =>
        new() { Id = id, NotificationChannelId = 1, ChannelName = "slack-ops", EventType = eventType, IsEnabled = true };

    private static PaginatedResult<NotificationChannelDto> Channels(params NotificationChannelDto[] items) => new() { Items = [.. items], TotalCount = items.Length };

    private static PaginatedResult<NotificationRuleDto> Rules(params NotificationRuleDto[] items) => new() { Items = [.. items], TotalCount = items.Length };

    private TaskCompletionSource<PaginatedResult<T>> Hold<T>(string urlContains)
    {
        var gate = new TaskCompletionSource<PaginatedResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, urlContains, _ => gate.Task);
        return gate;
    }

    private IRenderedComponent<NotificationsAdmin> RenderPage()
    {
        var cut = Render<NotificationsAdmin>();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("slack-ops", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("alert.triggered", cut.Markup, StringComparison.Ordinal);
        });
        return cut;
    }

    private static AetheusDataGrid<T> GridOf<T>(IRenderedComponent<NotificationsAdmin> cut) where T : notnull =>
        cut.FindComponent<AetheusDataGrid<T>>().Instance;

    [Fact]
    public async Task ASavedChannel_ShowsTheAnsweredRowAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = RenderPage();
        var grid = GridOf<NotificationChannelDto>(cut);
        var gate = Hold<NotificationChannelDto>("api/notifications/channels?");
        _dialogs.OpenResult = Channel(2, "ops-mail");

        await cut.FindAll("button").First(button => button.TextContent.Contains("Create", StringComparison.Ordinal)).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("ops-mail", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("slack-ops", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Channels(Channel(1, "slack-ops"), Channel(2, "ops-mail")));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("ops-mail", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASavedRule_ShowsTheAnsweredRowAtOnce_BeforeTheReadBehindItEnds()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/notifications/channels", new List<NotificationChannelDto> { Channel(1, "slack-ops") });
        var cut = RenderPage();
        var grid = GridOf<NotificationRuleDto>(cut);
        var gate = Hold<NotificationRuleDto>("api/notifications/rules?");
        _dialogs.OpenResult = Rule(1, "alert.resolved");

        cut.FindAll(RowEventIsolation.ClickableRows).First(row => row.TextContent.Contains("alert.triggered", StringComparison.Ordinal)).QuerySelector("td")!.Click();

        cut.WaitForAssertion(() => Assert.Contains("alert.resolved", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("alert.triggered", cut.Markup, StringComparison.Ordinal);
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Rules(Rule(1, "alert.resolved")));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("alert.resolved", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeletedRule_LeavesTheGridAtOnce_AndARefusedDeletionKeepsIt()
    {
        var cut = RenderPage();
        var grid = GridOf<NotificationRuleDto>(cut);

        _handler.SetResponse(HttpMethod.Delete, "api/notifications/rules/1", System.Net.HttpStatusCode.Conflict);
        cut.FindAll("button[title='Delete']")[1].Click();
        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestsSnapshot(), request => request.Method == "DELETE"));
        Assert.Contains("alert.triggered", cut.Markup, StringComparison.Ordinal);

        _handler.SetResponse(HttpMethod.Delete, "api/notifications/rules/1", System.Net.HttpStatusCode.NoContent);
        var gate = Hold<NotificationRuleDto>("api/notifications/rules?");
        cut.FindAll("button[title='Delete']")[1].Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("alert.triggered", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Rules());
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.DoesNotContain("alert.triggered", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeletedChannel_LeavesTheGridAtOnce_AndARefusedDeletionKeepsIt()
    {
        var cut = RenderPage();
        var grid = GridOf<NotificationChannelDto>(cut);

        _handler.SetResponse(HttpMethod.Delete, "api/notifications/channels/1", System.Net.HttpStatusCode.Conflict);
        cut.FindAll("button[title='Delete']")[0].Click();
        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestsSnapshot(), request => request.Method == "DELETE"));
        Assert.Contains("slack-ops", cut.Markup, StringComparison.Ordinal);

        _handler.SetResponse(HttpMethod.Delete, "api/notifications/channels/1", System.Net.HttpStatusCode.NoContent);
        var gate = Hold<NotificationChannelDto>("api/notifications/channels?");
        cut.FindAll("button[title='Delete']")[0].Click();

        // The rule row still names its channel; only the channel row's own cell is gone.
        cut.WaitForAssertion(() => Assert.DoesNotContain(cut.FindAll(RowEventIsolation.ClickableRows), row =>
            row.TextContent.Contains("slack-ops", StringComparison.Ordinal) && !row.TextContent.Contains("alert.", StringComparison.Ordinal)));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Channels());
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
    }
}
