// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using System.Text.Json;
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

/// <summary>Tests notification delivery through the real service and repository: users follow
/// readable projects, and release and agent-update events reach the corresponding
/// followers.</summary>
public sealed class ProjectNotificationReachTests : IDisposable
{
    private const int Alice = 1;
    private const int Bob = 2;
    private const int Shop = 10;
    private const int Blog = 11;
    private const int Docs = 12;
    private const int SharedServer = 50;
    private const int LoneServer = 51;
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly AppDbContext _db;
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly UserNotificationService _sut;

    public ProjectNotificationReachTests()
    {
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            _time);
        // Every subscriber still reads the project (one query for all of them, RBAC audit finding 12).
        _authz.GetUsernamesWithPermissionAsync(Arg.Any<IReadOnlyCollection<string>>(), ResourceType.Project, Arg.Any<int>(), Permission.Read, Arg.Any<CancellationToken>())
            .Returns(call => new HashSet<string>(call.ArgAt<IReadOnlyCollection<string>>(0), StringComparer.Ordinal));
        _sut = new UserNotificationService(new UserNotificationRepository(_db), _authz, _time);
        Seed();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task FollowProjects_AddsOnlyTheReadableProjects_KeepsTheExistingOne_AndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await _sut.FollowProjectsAsync(Bob, [Shop, Blog], ct);
        var second = await _sut.FollowProjectsAsync(Bob, [Shop, Blog], ct);

        Assert.Equal([Shop, Blog], first.Select(s => s.ProjectId).Order());
        Assert.Equal(first.Select(s => s.ProjectId).Order(), second.Select(s => s.ProjectId).Order());
        // Bob already followed Shop: one row each, nothing duplicated, Docs (not readable) untouched.
        Assert.Equal(2, await _db.ProjectSubscriptions.CountAsync(s => s.UserId == Bob, ct));
    }

    [Fact]
    public async Task FollowProjects_WildcardGrant_FollowsEveryProject_EmptyGrantFollowsNothing()
    {
        var ct = TestContext.Current.CancellationToken;

        var none = await _sut.FollowProjectsAsync(Alice, [], ct);
        Assert.Empty(none);

        var all = await _sut.FollowProjectsAsync(Alice, null, ct);
        Assert.Equal([Shop, Blog, Docs], all.Select(s => s.ProjectId).Order());
    }

    [Fact]
    public async Task FollowAllEndpoint_UsesTheCallersReadableProjects()
    {
        var ct = TestContext.Current.CancellationToken;
        var controller = Controller(Alice);
        _authz.GetAccessibleResourceIdsAsync(controller.User, ResourceType.Project, Permission.Read, Arg.Any<CancellationToken>())
            .Returns([Blog]);

        var result = await controller.FollowAll(ct);

        var subscriptions = Assert.IsType<List<ProjectSubscriptionDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var only = Assert.Single(subscriptions);
        Assert.Equal(Blog, only.ProjectId);
        Assert.Equal("Blog", only.ProjectName);
    }

    [Fact]
    public async Task Subscriptions_AreEmpty_ForAUserWhoFollowsNothing()
    {
        // The front shows its "you follow no project" hint from exactly this answer.
        var result = await Controller(Alice).GetSubscriptions(TestContext.Current.CancellationToken);

        Assert.Empty(Assert.IsType<List<ProjectSubscriptionDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
    }

    [Fact]
    public async Task ReleaseDeployed_ReachesTheFollowersOfTheReleaseProject_OnByDefault()
    {
        var ct = TestContext.Current.CancellationToken;

        var written = await _sut.RecordReleaseDeployedAsync(500, pipelineRunId: 77, stageName: "Prod", isRollback: false, ct);

        Assert.Equal(1, written);
        var row = await _db.NotificationDeliveries.SingleAsync(ct);
        Assert.Equal(Bob, row.RecipientUserId);
        Assert.Equal(NotificationEventTypes.ReleaseDeployed, row.EventType);
        Assert.Equal(NotificationDeliveryStatus.NotConfigured, row.Status);
        using var payload = JsonDocument.Parse(row.PayloadJson);
        Assert.Equal(Shop, payload.RootElement.GetProperty("ProjectId").GetInt32());
        Assert.Equal("1.4.0", payload.RootElement.GetProperty("Version").GetString());
        Assert.Equal(77, payload.RootElement.GetProperty("PipelineRunId").GetInt32());
    }

    [Fact]
    public async Task ReleaseDeployed_UnknownRelease_WritesNothing()
    {
        Assert.Equal(0, await _sut.RecordReleaseDeployedAsync(999, null, null, false, TestContext.Current.CancellationToken));
        Assert.Empty(_db.NotificationDeliveries);
    }

    [Fact]
    public async Task ReleaseDeployedEvent_IsHandedToTheUserNotifications()
    {
        var users = Substitute.For<IUserNotificationService>();
        var handler = new ReleaseDeployedNotificationHandler(users);

        await handler.HandleAsync(new ReleaseDeployedEvent(500, 77, "Prod", IsRollback: true), TestContext.Current.CancellationToken);

        await users.Received(1).RecordReleaseDeployedAsync(500, 77, "Prod", true, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AgentUpdateFailed_ReachesTheFollowersOfEveryProjectOfTheServer()
    {
        var ct = TestContext.Current.CancellationToken;
        _db.ProjectSubscriptions.Add(new ProjectSubscription { UserId = Alice, ProjectId = Blog });
        await _db.SaveChangesAsync(ct);
        var publisher = new AgentUpdateNotificationPublisher(_sut, NullLogger<AgentUpdateNotificationPublisher>.Instance);

        await publisher.PublishOutcomeAsync(Request(SharedServer, AgentUpdateRequestStatus.Failed), ct);

        var rows = await _db.NotificationDeliveries.OrderBy(d => d.RecipientUserId).ToListAsync(ct);
        Assert.Equal([Alice, Bob], rows.Select(r => r.RecipientUserId));
        Assert.All(rows, row => Assert.Equal(NotificationEventTypes.AgentUpdateFailed, row.EventType));
        var projects = rows.Select(row =>
        {
            using var payload = JsonDocument.Parse(row.PayloadJson);
            Assert.Equal("wrong-version", payload.RootElement.GetProperty("FailureCode").GetString());
            return payload.RootElement.GetProperty("ProjectId").GetInt32();
        });
        Assert.Equal([Blog, Shop], projects);
    }

    [Fact]
    public async Task AgentUpdate_ServerAttachedToNoProject_NotifiesNobody()
    {
        var publisher = new AgentUpdateNotificationPublisher(_sut, NullLogger<AgentUpdateNotificationPublisher>.Instance);

        await publisher.PublishOutcomeAsync(Request(LoneServer, AgentUpdateRequestStatus.Failed), TestContext.Current.CancellationToken);

        Assert.Empty(_db.NotificationDeliveries);
    }

    [Fact]
    public async Task AgentUpdateCompleted_IsOffByDefault_AndReachesTheFollowerWhoTurnedItOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var publisher = new AgentUpdateNotificationPublisher(_sut, NullLogger<AgentUpdateNotificationPublisher>.Instance);

        await publisher.PublishOutcomeAsync(Request(SharedServer, AgentUpdateRequestStatus.Confirmed), ct);
        Assert.Empty(_db.NotificationDeliveries);

        await _sut.UpdatePreferencesAsync(Bob, new UpdateNotificationPreferencesRequest
        {
            Preferences = [new NotificationPreferenceItem { EventType = NotificationEventTypes.AgentUpdateCompleted, IsEnabled = true }]
        }, ct);
        await publisher.PublishOutcomeAsync(Request(SharedServer, AgentUpdateRequestStatus.Confirmed), ct);

        var row = await _db.NotificationDeliveries.SingleAsync(ct);
        Assert.Equal((Bob, NotificationEventTypes.AgentUpdateCompleted), (row.RecipientUserId, row.EventType));
    }

    [Fact]
    public async Task AgentUpdate_RunningRequest_IsNotPublished_AndAFailingRecordNeverEscapes()
    {
        var ct = TestContext.Current.CancellationToken;
        var users = Substitute.For<IUserNotificationService>();
        users.RecordServerEventAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Func<int, object>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database gone"));
        var publisher = new AgentUpdateNotificationPublisher(users, NullLogger<AgentUpdateNotificationPublisher>.Instance);

        await publisher.PublishOutcomeAsync(Request(SharedServer, AgentUpdateRequestStatus.Handoff), ct);
        await users.DidNotReceive().RecordServerEventAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Func<int, object>>(), Arg.Any<CancellationToken>());

        // The update outcome is already saved by the caller: a notification failure is logged, not thrown.
        await publisher.PublishOutcomeAsync(Request(SharedServer, AgentUpdateRequestStatus.Failed), ct);
        await users.Received(1).RecordServerEventAsync(
            NotificationEventTypes.AgentUpdateFailed, SharedServer, Arg.Any<Func<int, object>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preferences_ListTheNewEvents_WithTheirDefaults()
    {
        var preferences = await _sut.GetPreferencesAsync(Alice, TestContext.Current.CancellationToken);

        var byType = preferences.ToDictionary(p => p.EventType);
        Assert.True(byType[NotificationEventTypes.ReleaseDeployed].IsEnabled);
        Assert.False(byType[NotificationEventTypes.AgentUpdateCompleted].IsEnabled);
        Assert.True(byType[NotificationEventTypes.AgentUpdateFailed].IsEnabled);
        Assert.All(
            [NotificationEventTypes.ReleaseDeployed, NotificationEventTypes.AgentUpdateCompleted, NotificationEventTypes.AgentUpdateFailed],
            eventType => Assert.True(byType[eventType].CarriesProjectId));
    }

    private static AgentUpdateRequest Request(int serverId, AgentUpdateRequestStatus status) => new()
    {
        Id = 900,
        ServerId = serverId,
        Server = new Server { Id = serverId, Name = $"srv-{serverId}" },
        ObservedVersion = "1.0.0",
        TargetVersion = "1.1.0",
        Status = status,
        FailureCode = status == AgentUpdateRequestStatus.Failed ? "wrong-version" : null
    };

    private UserNotificationsController Controller(int userId) =>
        new(_sut, _authz)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString(System.Globalization.CultureInfo.InvariantCulture))],
                        "test"))
                }
            }
        };

    private void Seed()
    {
        _db.Users.AddRange(
            new User { Id = Alice, Username = "alice" },
            new User { Id = Bob, Username = "bob" });
        _db.Projects.AddRange(
            new Project { Id = Shop, Name = "Shop" },
            new Project { Id = Blog, Name = "Blog" },
            new Project { Id = Docs, Name = "Docs" });
        _db.ProjectSubscriptions.Add(new ProjectSubscription { UserId = Bob, ProjectId = Shop });
        // The shared server row exists, in the projects' organization: a server reaches only the projects of its own.
        _db.Servers.Add(new Server { Id = SharedServer, Name = "srv-shared" });
        _db.ProjectServers.AddRange(
            new ProjectServer { Id = 1, ProjectId = Shop, ServerId = SharedServer, Type = ProjectServerType.AgentServer, DisplayName = "web", Host = "web" },
            new ProjectServer { Id = 2, ProjectId = Blog, ServerId = SharedServer, Type = ProjectServerType.AgentServer, DisplayName = "web", Host = "web" },
            new ProjectServer { Id = 3, ProjectId = Docs, ServerId = null, Type = ProjectServerType.ExternalHost, DisplayName = "ext", Host = "ext" });
        _db.Releases.Add(new Release { Id = 500, ProjectId = Shop, Version = "1.4.0" });
        _db.SaveChanges();
    }
}
