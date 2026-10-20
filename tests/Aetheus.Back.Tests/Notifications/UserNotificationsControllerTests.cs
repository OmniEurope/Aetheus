// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// The <c>/api/notifications/me</c> endpoints over the real service and repository: a caller only ever
/// sees and changes their own deliveries, preferences and subscriptions.
/// </summary>
public sealed class UserNotificationsControllerTests : IDisposable
{
    private const int Alice = 1;
    private const int Bob = 2;
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly AppDbContext _db;
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly UserNotificationsController _sut;
    private int _bobDeliveryId;

    public UserNotificationsControllerTests()
    {
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            _time);
        _sut = new UserNotificationsController(
            new UserNotificationService(new UserNotificationRepository(_db), _authz, _time),
            _authz)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, Alice.ToString(System.Globalization.CultureInfo.InvariantCulture))],
                        "test"))
                }
            }
        };
        Seed();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetMine_ReturnsOnlyTheCallersRows_NewestFirst()
    {
        var result = await _sut.GetMine(new UserNotificationPageRequest(), TestContext.Current.CancellationToken);

        var page = Assert.IsType<PaginatedResult<NotificationDeliveryDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(["third", "second", "first"], page.Items.Select(item => item.Subject));
        Assert.DoesNotContain(page.Items, item => item.Id == _bobDeliveryId);
    }

    [Fact]
    public async Task GetMine_FiltersOnStatusAndUnread()
    {
        var unread = await _sut.GetMine(new UserNotificationPageRequest { UnreadOnly = true }, TestContext.Current.CancellationToken);
        var failed = await _sut.GetMine(
            new UserNotificationPageRequest { Status = NotificationDeliveryStatus.Failed }, TestContext.Current.CancellationToken);

        var unreadPage = (PaginatedResult<NotificationDeliveryDto>)((OkObjectResult)unread.Result!).Value!;
        var failedPage = (PaginatedResult<NotificationDeliveryDto>)((OkObjectResult)failed.Result!).Value!;
        Assert.Equal(["third", "second"], unreadPage.Items.Select(item => item.Subject));
        Assert.Equal(["second"], failedPage.Items.Select(item => item.Subject));
    }

    [Fact]
    public async Task GetMine_HeaderFilters_StatusList_ReadYesNo_AndDateRange()
    {
        // The grid's columns filter through the generic column filters.
        var unreadOfTwoStates = await _sut.GetMine(new UserNotificationPageRequest
        {
            Filters =
            [
                new GridFilter { Field = "Status", Operator = GridFilterOperator.In, Value = $"Failed{GridFilter.ListSeparator}NotConfigured" },
                new GridFilter { Field = "IsRead", Operator = GridFilterOperator.Equals, Value = "False" }
            ]
        }, TestContext.Current.CancellationToken);
        var inRange = await _sut.GetMine(new UserNotificationPageRequest
        {
            Filters =
            [
                new GridFilter
                {
                    Field = "CreatedAt", Operator = GridFilterOperator.GreaterThanOrEqual, Value = "2026-09-21T10:00:02Z",
                    SecondOperator = GridFilterOperator.LessThan, SecondValue = "2026-09-21T10:00:03Z"
                },
                new GridFilter { Field = "EventType", Operator = GridFilterOperator.In, Value = NotificationEventTypes.PipelineFailed }
            ]
        }, TestContext.Current.CancellationToken);

        var unreadPage = (PaginatedResult<NotificationDeliveryDto>)((OkObjectResult)unreadOfTwoStates.Result!).Value!;
        var rangePage = (PaginatedResult<NotificationDeliveryDto>)((OkObjectResult)inRange.Result!).Value!;
        Assert.Equal(["third", "second"], unreadPage.Items.Select(item => item.Subject));
        Assert.Equal(2, unreadPage.TotalCount);
        Assert.Equal(["second"], rangePage.Items.Select(item => item.Subject));
    }

    [Fact]
    public async Task FilterValues_ListTheCallersEventTypes()
    {
        _db.NotificationDeliveries.Add(new NotificationDelivery
        {
            RecipientUserId = Bob,
            EventType = "bob.only",
            Subject = "x",
            Status = NotificationDeliveryStatus.NotConfigured
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _sut.GetFilterValues(TestContext.Current.CancellationToken);

        var values = Assert.IsType<UserNotificationFilterValuesDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal([NotificationEventTypes.PipelineFailed], values.EventTypes);
    }

    [Fact]
    public async Task GetMine_SortsOldestFirst_WhenAsked()
    {
        var result = await _sut.GetMine(new UserNotificationPageRequest { SortDescending = false }, TestContext.Current.CancellationToken);

        var page = (PaginatedResult<NotificationDeliveryDto>)((OkObjectResult)result.Result!).Value!;
        Assert.Equal(["first", "second", "third"], page.Items.Select(item => item.Subject));
    }

    [Fact]
    public async Task GetMine_SortsOnAnyColumn_WithTheIdBreakingTies()
    {
        var byStatus = await _sut.GetMine(
            new UserNotificationPageRequest { SortBy = "Status", SortDescending = false }, TestContext.Current.CancellationToken);
        var bySubject = await _sut.GetMine(
            new UserNotificationPageRequest { SortBy = "subject", SortDescending = true }, TestContext.Current.CancellationToken);

        var statusPage = (PaginatedResult<NotificationDeliveryDto>)((OkObjectResult)byStatus.Result!).Value!;
        var subjectPage = (PaginatedResult<NotificationDeliveryDto>)((OkObjectResult)bySubject.Result!).Value!;
        // Failed (2) before NotConfigured (3); the two NotConfigured rows keep their id order.
        Assert.Equal(["second", "first", "third"], statusPage.Items.Select(item => item.Subject));
        Assert.Equal(["third", "second", "first"], subjectPage.Items.Select(item => item.Subject));
    }

    [Fact]
    public async Task GetMine_ClampsThePageSize()
    {
        var result = await _sut.GetMine(new UserNotificationPageRequest { PageSize = 5000 }, TestContext.Current.CancellationToken);

        var page = (PaginatedResult<NotificationDeliveryDto>)((OkObjectResult)result.Result!).Value!;
        Assert.Equal(PaginationDefaults.MaximumPageSize, page.PageSize);
    }

    [Fact]
    public async Task UnreadCount_MatchesTheUnreadRowsOfTheCaller()
    {
        var result = await _sut.GetUnreadCount(TestContext.Current.CancellationToken);

        Assert.Equal(2, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task MarkRead_AnotherUsersDelivery_Returns404_AndLeavesItUnread()
    {
        var result = await _sut.MarkRead(_bobDeliveryId, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
        var bobRow = await _db.NotificationDeliveries.AsNoTracking()
            .SingleAsync(d => d.Id == _bobDeliveryId, TestContext.Current.CancellationToken);
        Assert.Null(bobRow.ReadAt);
    }

    [Fact]
    public async Task MarkRead_OwnDelivery_SetsReadAt_AndLowersTheCount()
    {
        var target = await _db.NotificationDeliveries.AsNoTracking()
            .SingleAsync(d => d.Subject == "third", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(5));
        var expectedReadAt = _time.GetUtcNow().UtcDateTime;

        var result = await _sut.MarkRead(target.Id, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
        var row = await _db.NotificationDeliveries.AsNoTracking()
            .SingleAsync(d => d.Id == target.Id, TestContext.Current.CancellationToken);
        Assert.Equal(expectedReadAt, row.ReadAt);
        var count = await _sut.GetUnreadCount(TestContext.Current.CancellationToken);
        Assert.Equal(1, ((OkObjectResult)count.Result!).Value);
    }

    [Fact]
    public async Task MarkAllRead_SetsReadAtOnEveryCallerRow_AndOnNoOtherUsersRow()
    {
        var result = await _sut.MarkAllRead(TestContext.Current.CancellationToken);

        Assert.Equal(2, Assert.IsType<OkObjectResult>(result.Result).Value);
        var rows = await _db.NotificationDeliveries.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.All(rows.Where(r => r.RecipientUserId == Alice), row => Assert.NotNull(row.ReadAt));
        Assert.Null(rows.Single(r => r.RecipientUserId == Bob).ReadAt);
        var count = await _sut.GetUnreadCount(TestContext.Current.CancellationToken);
        Assert.Equal(0, ((OkObjectResult)count.Result!).Value);
    }

    [Fact]
    public async Task Preferences_DefaultToFailedAndApprovalOn_AndSaveServerSide()
    {
        var defaults = (List<NotificationPreferenceDto>)((OkObjectResult)(await _sut.GetPreferences(TestContext.Current.CancellationToken)).Result!).Value!;
        // R-522: a refused automatic launch is on by default too, it is the only trace of the refusal.
        // R2-034: so are a deployed release and a failed agent update; a completed update is off.
        // R3-16: and a finished AI task run, whose notification links to its report.
        // R1-77: and a rejected run, a final result beside failed and succeeded; the child results option is off.
        Assert.Equal(
            [NotificationEventTypes.PipelineFailed, NotificationEventTypes.PipelineRejected, NotificationEventTypes.PipelineApprovalRequested, NotificationEventTypes.PipelineLaunchRefused,
             NotificationEventTypes.ReleaseDeployed, NotificationEventTypes.AgentUpdateFailed, NotificationEventTypes.AiTaskCompleted],
            defaults.Where(p => p.IsEnabled).Select(p => p.EventType));
        Assert.All(defaults, p => Assert.False(p.IsSaved));

        await _sut.UpdatePreferences(new UpdateNotificationPreferencesRequest
        {
            Preferences =
            [
                new NotificationPreferenceItem { EventType = NotificationEventTypes.PipelineFailed, IsEnabled = false },
                new NotificationPreferenceItem { EventType = NotificationEventTypes.PipelineSucceeded, IsEnabled = true }
            ]
        }, TestContext.Current.CancellationToken);

        var saved = (List<NotificationPreferenceDto>)((OkObjectResult)(await _sut.GetPreferences(TestContext.Current.CancellationToken)).Result!).Value!;
        Assert.Equal(
            [NotificationEventTypes.PipelineSucceeded, NotificationEventTypes.PipelineRejected, NotificationEventTypes.PipelineApprovalRequested, NotificationEventTypes.PipelineLaunchRefused,
             NotificationEventTypes.ReleaseDeployed, NotificationEventTypes.AgentUpdateFailed, NotificationEventTypes.AiTaskCompleted],
            saved.Where(p => p.IsEnabled).Select(p => p.EventType));
        Assert.Equal(2, await _db.UserNotificationPreferences.CountAsync(p => p.UserId == Alice, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Preferences_UnknownEventType_IsRejected()
    {
        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() => _sut.UpdatePreferences(
            new UpdateNotificationPreferencesRequest
            {
                Preferences = [new NotificationPreferenceItem { EventType = "no.such.event", IsEnabled = true }]
            },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Subscribe_WithoutReadPermission_IsForbidden()
    {
        var result = await _sut.Subscribe(10, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(_db.ProjectSubscriptions);
    }

    [Fact]
    public async Task Subscribe_ListAndUnsubscribe_TouchOnlyTheCallersSubscription()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 10, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _db.ProjectSubscriptions.Add(new ProjectSubscription { UserId = Bob, ProjectId = 10 });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var first = await _sut.Subscribe(10, TestContext.Current.CancellationToken);
        var again = await _sut.Subscribe(10, TestContext.Current.CancellationToken);

        Assert.Equal("Shop", Assert.IsType<ProjectSubscriptionDto>(((OkObjectResult)first.Result!).Value).ProjectName);
        Assert.IsType<OkObjectResult>(again.Result);
        var mine = (List<ProjectSubscriptionDto>)((OkObjectResult)(await _sut.GetSubscriptions(TestContext.Current.CancellationToken)).Result!).Value!;
        Assert.Equal([10], mine.Select(s => s.ProjectId));

        Assert.IsType<NoContentResult>(await _sut.Unsubscribe(10, TestContext.Current.CancellationToken));
        Assert.IsType<NotFoundResult>(await _sut.Unsubscribe(10, TestContext.Current.CancellationToken));
        Assert.Equal([Bob], await _db.ProjectSubscriptions.Select(s => s.UserId).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Subscribe_UnknownProject_Returns404()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 99, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.Subscribe(99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    private void Seed()
    {
        _db.Users.AddRange(new User { Id = Alice, Username = "alice" }, new User { Id = Bob, Username = "bob" });
        _db.Projects.Add(new Project { Id = 10, Name = "Shop" });
        _db.SaveChanges();

        AddDelivery(Alice, "first", NotificationDeliveryStatus.NotConfigured, readAt: Now.UtcDateTime);
        AddDelivery(Alice, "second", NotificationDeliveryStatus.Failed, readAt: null);
        AddDelivery(Alice, "third", NotificationDeliveryStatus.NotConfigured, readAt: null);
        _bobDeliveryId = AddDelivery(Bob, "bob's", NotificationDeliveryStatus.NotConfigured, readAt: null);
        _db.ChangeTracker.Clear();
    }

    private int AddDelivery(int userId, string subject, NotificationDeliveryStatus status, DateTime? readAt)
    {
        // CreatedAt comes from the context's clock: advance it so the rows have a distinct order.
        _time.Advance(TimeSpan.FromSeconds(1));
        var delivery = new NotificationDelivery
        {
            RecipientUserId = userId,
            EventType = NotificationEventTypes.PipelineFailed,
            Subject = subject,
            Status = status,
            ReadAt = readAt
        };
        _db.NotificationDeliveries.Add(delivery);
        _db.SaveChanges();
        return delivery.Id;
    }
}
