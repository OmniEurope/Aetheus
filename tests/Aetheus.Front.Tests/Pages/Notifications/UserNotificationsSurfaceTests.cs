// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Notifications;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Notifications;

/// <summary>Verifies that the bell, menu and notifications page share UserNotificationsFeed,
/// display API data and mark all notifications read through the API.</summary>
public sealed class UserNotificationsSurfaceTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private int _unread = 3;

    public UserNotificationsSurfaceTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/notifications/me/unread-count", _ => Task.FromResult(_unread));
        _handler.SetJsonResponse(HttpMethod.Get, "api/notifications/me/filter-values",
            new UserNotificationFilterValuesDto { EventTypes = [NotificationEventTypes.PipelineFailed] });
    }

    [Fact]
    public async Task Page_HeaderFilters_AreSentAsColumnFilters()
    {
        // The endpoint applies delivery-state, read-state, event and date-range filters.
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/notifications/me?", [Delivery(1, "one")]);
        var page = Render<NotificationsInbox>();
        var grid = page.FindComponent<AetheusDataGrid<NotificationDeliveryDto>>();
        var separator = Aetheus.Shared.Components.Shared.GridFilter.ListSeparator;

        await page.InvokeAsync(() => grid.Instance.LoadData.InvokeAsync(new GridLoadArgs
        {
            Filters =
            [
                new GridFilterDescriptor(nameof(NotificationDeliveryDto.Status), $"Failed{separator}Pending", OmniDataGridFilterOperator.In),
                new GridFilterDescriptor(nameof(NotificationDeliveryDto.IsRead), "False", OmniDataGridFilterOperator.Equals),
                new GridFilterDescriptor(nameof(NotificationDeliveryDto.CreatedAt), "2026-09-01",
                    OmniDataGridFilterOperator.GreaterThanOrEquals, OmniDataGridFilterOperator.LessThan, "2026-09-22")
            ]
        }));

        page.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
        {
            var url = Uri.UnescapeDataString(request.Url);
            return url.Contains("api/notifications/me?", StringComparison.Ordinal)
                && url.Contains("Filters[0].Field=Status", StringComparison.Ordinal)
                && url.Contains($"Filters[0].Value=Failed{separator}Pending", StringComparison.Ordinal)
                && url.Contains("Filters[1].Field=IsRead", StringComparison.Ordinal)
                && url.Contains("Filters[1].Value=False", StringComparison.Ordinal)
                && url.Contains("Filters[2].Field=CreatedAt", StringComparison.Ordinal)
                && url.Contains("Filters[2].SecondValue=2026-09-22", StringComparison.Ordinal)
                && !url.Contains("status=", StringComparison.Ordinal);
        }));
        Assert.Contains(_handler.Requests, request => request.Url.Contains("api/notifications/me/filter-values", StringComparison.Ordinal));
    }

    private static NotificationDeliveryDto Delivery(int id, string subject, NotificationDeliveryStatus status = NotificationDeliveryStatus.NotConfigured) => new()
    {
        Id = id,
        EventType = NotificationEventTypes.PipelineFailed,
        Subject = subject,
        Status = status,
        CreatedAt = new DateTime(2026, 9, 21, 8, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void Bell_ShowsTheUnreadCountOfTheFeed()
    {
        var bell = Render<NotificationBell>();

        bell.WaitForAssertion(() => Assert.Equal("3", bell.Find(".notification-bell-count").TextContent.Trim()));
        Assert.Equal(3, Services.GetRequiredService<UserNotificationsFeed>().UnreadCount);
    }

    /// <summary>The unread count remains available when the delivery list request fails or its
    /// response cannot be parsed.</summary>
    [Theory]
    [InlineData("error")]
    [InlineData("unparsable")]
    public void Bell_ShowsTheUnreadCount_WhenTheNewestDeliveriesCannotBeRead(string failure)
    {
        if (failure == "error")
            _handler.SetResponse(HttpMethod.Get, "api/notifications/me?", System.Net.HttpStatusCode.InternalServerError);
        else
            _handler.SetRawResponse(HttpMethod.Get, "api/notifications/me?", "<!doctype html>", "application/json");

        var bell = Render<NotificationBell>();

        bell.WaitForAssertion(() => Assert.Equal("3", bell.Find(".notification-bell-count").TextContent.Trim()));
        Assert.Equal(3, Services.GetRequiredService<UserNotificationsFeed>().UnreadCount);
    }

    [Fact]
    public async Task Front005_AnAnswerRequestedForThePreviousAccount_IsDroppedAfterSignOutAndSignIn()
    {
        var countOfA = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestOfA = new TaskCompletionSource<PaginatedResult<NotificationDeliveryDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/notifications/me/unread-count", _ => countOfA.Task);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/notifications/me?", _ => latestOfA.Task);
        var feed = Services.GetRequiredService<UserNotificationsFeed>();
        var auth = Services.GetRequiredService<AuthStateProvider>();
        var inFlight = feed.RefreshAsync();

        await auth.LogoutAsync();
        await auth.LoginAsync(TokenFor("second-account"));
        countOfA.SetResult(9);
        latestOfA.SetResult(new PaginatedResult<NotificationDeliveryDto> { Items = [Delivery(1, "subject of the first account")], TotalCount = 1 });
        await inFlight;

        Assert.Equal(0, feed.UnreadCount);
        Assert.Empty(feed.Latest);

        // The count keeps working for the account now signed in.
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/notifications/me/unread-count", _ => Task.FromResult(2));
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/notifications/me?", [Delivery(2, "subject of the second account")]);
        await feed.RefreshAsync();
        Assert.Equal(2, feed.UnreadCount);
        Assert.Equal("subject of the second account", Assert.Single(feed.Latest).Subject);
    }

    private static string TokenFor(string username)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"HS256\"}"));
        var payload = Encode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new { sub = username, unique_name = username, exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }));
        return $"{header}.{payload}.signature";
    }

    [Fact]
    public async Task Feed_KeepsTheLatestDeliveries_WhenOnlyTheCountCannotBeRead()
    {
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/notifications/me?", [Delivery(5, "kept")]);
        _handler.SetAsyncJsonResponse<int>(HttpMethod.Get, "api/notifications/me/unread-count",
            _ => Task.FromException<int>(new HttpRequestException("unread count unavailable")));
        var feed = Services.GetRequiredService<UserNotificationsFeed>();

        await feed.RefreshAsync();

        Assert.Equal("kept", Assert.Single(feed.Latest).Subject);
        Assert.Equal(0, feed.UnreadCount);
    }

    [Fact]
    public void MenuEntry_ShowsTheSameCountAsTheBell()
    {
        var bell = Render<NotificationBell>();
        var entry = Render<NotificationsNavItem>();

        entry.WaitForAssertion(() => Assert.Equal("3", entry.Find("#nav-notification-count").TextContent.Trim()));
        Assert.Equal(bell.Find(".notification-bell-count").TextContent.Trim(), entry.Find("#nav-notification-count").TextContent.Trim());
    }

    [Fact]
    public void R461_TheBellAndTheMenuEntry_StartTheFeedWithOnePairOfRequests()
    {
        var bell = Render<NotificationBell>();
        var entry = Render<NotificationsNavItem>();

        entry.WaitForAssertion(() => Assert.Equal("3", entry.Find("#nav-notification-count").TextContent.Trim()));
        Assert.Equal("3", bell.Find(".notification-bell-count").TextContent.Trim());
        Assert.Single(_handler.Requests, request => request.Url.Contains("api/notifications/me/unread-count", StringComparison.Ordinal));
    }

    [Fact]
    public async Task R461_ABurstOfPushes_IsServedByOneRefresh()
    {
        // The coalescing window runs on a simulated clock: the burst lands inside it however slow the
        // machine is, and the window closes when the clock is moved.
        var clock = new ObservedFakeClock(DateTimeOffset.UnixEpoch);
        Services.AddSingleton<TimeProvider>(clock);
        var window = TimeSpan.FromMilliseconds(UserNotificationsFeed.PushCoalesceMilliseconds);
        var feed = Services.GetRequiredService<UserNotificationsFeed>();
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/notifications/me?", [Delivery(1, "one")]);
        await feed.EnsureStartedAsync();
        _unread = 5;
        var changed = new StateSignal();
        feed.OnChanged += changed.Pulse;

        feed.RefreshFromPush();
        feed.RefreshFromPush();
        feed.RefreshFromPush();

        Assert.Equal(1, clock.Pending(window));
        clock.Advance(window);
        await changed.WhenAsync(() => feed.UnreadCount == 5, "the coalesced refresh");
        // The one window closed and no other is open: no further refresh can come.
        Assert.Equal(1, clock.Armed(window));
        Assert.Equal(0, clock.Pending(window));
        Assert.Equal(2, _handler.Requests.Count(request => request.Url.Contains("api/notifications/me/unread-count", StringComparison.Ordinal)));
    }

    [Fact]
    public void Page_ListsOnlyTheRowsTheApiReturned_WithTheirStatusText()
    {
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/notifications/me?",
            [Delivery(1, "pipeline.failed in project Shop"), Delivery(2, "pipeline.failed in project Blog")]);

        var page = Render<NotificationsInbox>();

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".notification-status-badge").Count));
        Assert.Contains("pipeline.failed in project Shop", page.Markup, StringComparison.Ordinal);
        Assert.Contains("pipeline.failed in project Blog", page.Markup, StringComparison.Ordinal);
        // The status is a text label, never a colour alone; the fake localizer echoes the key.
        Assert.All(page.FindAll(".notification-status-badge"),
            badge => Assert.Contains("Enum_NotificationDeliveryStatus_NotConfigured", badge.TextContent, StringComparison.Ordinal));
        Assert.Contains("NotificationsNotSentNotice", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void OpeningAnUnreadNotification_MarksItRead_AndUpdatesTheCount()
    {
        // Opening a notification reads it; its count follows, with no toast.
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/notifications/me?", [Delivery(7, "unread one")]);
        _handler.SetAsyncJsonResponse(HttpMethod.Post, "api/notifications/me/7/read", _ =>
        {
            _unread = 2;
            return Task.FromResult(true);
        });
        var page = Render<NotificationsInbox>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tr[data-omni-row-index='0']")));

        page.Find("tr[data-omni-row-index='0']").Click();

        page.WaitForAssertion(() => Assert.Contains(_handler.Requests,
            request => request.Method == "POST" && request.Url.EndsWith("api/notifications/me/7/read", StringComparison.Ordinal)));
        page.WaitForAssertion(() => Assert.Equal(2, Services.GetRequiredService<UserNotificationsFeed>().UnreadCount));
    }

    [Fact]
    public void OpeningAReadNotification_SendsNoMarkRead()
    {
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/notifications/me?",
            [Delivery(8, "read one") with { ReadAt = new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc) }]);
        var page = Render<NotificationsInbox>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tr[data-omni-row-index='0']")));

        page.Find("tr[data-omni-row-index='0']").Click();

        Assert.DoesNotContain(_handler.Requests, request => request.Method == "POST");
    }

    [Fact]
    public void MarkAllRead_CallsTheEndpoint_AndResetsTheCount()
    {
        _handler.SetAsyncJsonResponse(HttpMethod.Post, "api/notifications/me/read-all", _ =>
        {
            _unread = 0;
            return Task.FromResult(3);
        });
        var bell = Render<NotificationBell>();
        bell.WaitForAssertion(() => Assert.Equal("3", bell.Find(".notification-bell-count").TextContent.Trim()));

        bell.Find(".notification-bell-btn").Click();
        bell.Find(".notification-bell-mark-all").Click();

        bell.WaitForAssertion(() => Assert.Empty(bell.FindAll(".notification-bell-count")));
        Assert.Contains(_handler.Requests, request => request.Method == "POST" && request.Url.EndsWith("api/notifications/me/read-all", StringComparison.Ordinal));
        Assert.Equal(0, Services.GetRequiredService<UserNotificationsFeed>().UnreadCount);
    }

    [Fact]
    public void Inbox_RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/notifications/me?", [Delivery(1, "one")]);

        var page = Render<NotificationsInbox>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(RowEventIsolation.ClickableRows)));

        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(page.FindAll(RowEventIsolation.ClickableRows)) >= 1);
    }
}
