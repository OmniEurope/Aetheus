// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// A project event raised through <see cref="NotificationService.SendEventAsync"/> writes one delivery per
/// subscriber whose preferences accept it, recorded as NotConfigured because no transport exists.
/// </summary>
public sealed class UserNotificationDispatchTests : IDisposable
{
    private const int ProjectId = 10;
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    private readonly AppDbContext _db;
    private readonly INotificationRepository _channelRepo = Substitute.For<INotificationRepository>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly NotificationService _sut;

    public UserNotificationDispatchTests()
    {
        var time = new FakeTimeProvider(Now);
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            time);
        _authz.HasPermissionAsync(Arg.Any<string>(), ResourceType.Project, ProjectId, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _channelRepo.GetRulesForEventAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _channelRepo.GetActiveAdministratorEmailsAsync(Arg.Any<CancellationToken>()).Returns(new List<string>());
        var users = new UserNotificationService(new UserNotificationRepository(_db), _authz, time);
        _sut = new NotificationService(
            _channelRepo,
            Substitute.For<IAuditService>(),
            Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<ILogger<NotificationService>>(),
            Substitute.For<IEncryptionService>(),
            time,
            users,
            Substitute.For<INotificationEmailer>());
        Seed();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ProjectEvent_WritesNotConfiguredRows_OnlyForSubscribersWhosePreferencesAcceptIt()
    {
        await _sut.SendEventAsync(
            NotificationEventTypes.PipelineFailed,
            new { PipelineRunId = 5, ProjectId },
            TestContext.Current.CancellationToken);

        var rows = await _db.NotificationDeliveries.OrderBy(d => d.RecipientUserId)
            .ToListAsync(TestContext.Current.CancellationToken);
        // alice (1): no saved row, pipeline.failed is on by default. carol (3): explicit on.
        // bob (2): explicit off. dave (4): inactive. eve (5): not subscribed.
        Assert.Equal([1, 3], rows.Select(r => r.RecipientUserId));
        Assert.All(rows, row =>
        {
            Assert.Equal(NotificationDeliveryStatus.NotConfigured, row.Status);
            Assert.Null(row.SentAt);
            Assert.Null(row.ErrorMessage);
            Assert.Null(row.ChannelId);
            Assert.Null(row.ReadAt);
            Assert.Equal(NotificationEventTypes.PipelineFailed, row.EventType);
            Assert.Equal("pipeline.failed in project Shop", row.Subject);
            Assert.Contains("\"PipelineRunId\":5", row.PayloadJson, StringComparison.Ordinal);
            Assert.Equal(Now.UtcDateTime, row.CreatedAt);
        });
        Assert.DoesNotContain(rows, row => row.Status == NotificationDeliveryStatus.Sent);
    }

    [Fact]
    public async Task EventOffByDefault_ReachesOnlyTheSubscriberWhoTurnedItOn()
    {
        await _sut.SendEventAsync(
            NotificationEventTypes.PipelineSucceeded,
            new { ProjectId },
            TestContext.Current.CancellationToken);

        var recipients = await _db.NotificationDeliveries.Select(d => d.RecipientUserId)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([2], recipients);
    }

    [Fact]
    public async Task EventWithoutProjectId_WritesNoRow_AndStillReachesTheAdminRules()
    {
        await _sut.SendEventAsync("alert.triggered", new { ServerId = 3 }, TestContext.Current.CancellationToken);
        await _sut.SendEventAsync(
            NotificationEventTypes.PipelineFailed,
            new { ProjectId = (int?)null },
            TestContext.Current.CancellationToken);

        Assert.Empty(_db.NotificationDeliveries);
        await _channelRepo.Received(1).GetRulesForEventAsync("alert.triggered", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubscriberWhoLostReadAccess_GetsNoRow()
    {
        _authz.HasPermissionAsync("carol", ResourceType.Project, ProjectId, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        await _sut.SendEventAsync(
            NotificationEventTypes.PipelineFailed,
            new { ProjectId },
            TestContext.Current.CancellationToken);

        var recipients = await _db.NotificationDeliveries.Select(d => d.RecipientUserId)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([1], recipients);
    }

    [Fact]
    public async Task EventTypeOutsideTheCatalogue_WritesNoRow()
    {
        await _sut.SendEventAsync("custom.unknown", new { ProjectId }, TestContext.Current.CancellationToken);

        Assert.Empty(_db.NotificationDeliveries);
    }

    [Fact]
    public async Task EmailRule_DoesNotStopTheUserRows_WhichStayUnsentWithoutTransport()
    {
        var channel = new NotificationChannel { Id = 7, Name = "mail", Type = NotificationChannelType.Email };
        _channelRepo.GetRulesForEventAsync(NotificationEventTypes.PipelineFailed, Arg.Any<CancellationToken>())
            .Returns([new NotificationRule { Id = 1, EventType = NotificationEventTypes.PipelineFailed, Channel = channel }]);

        await _sut.SendEventAsync(
            NotificationEventTypes.PipelineFailed,
            new { ProjectId },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, await _db.NotificationDeliveries.CountAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(_db.NotificationDeliveries, row => row.Status == NotificationDeliveryStatus.Sent);
    }

    private void Seed()
    {
        _db.Users.AddRange(
            new User { Id = 1, Username = "alice" },
            new User { Id = 2, Username = "bob" },
            new User { Id = 3, Username = "carol" },
            new User { Id = 4, Username = "dave", IsActive = false },
            new User { Id = 5, Username = "eve" });
        _db.Projects.AddRange(
            new Project { Id = ProjectId, Name = "Shop" },
            new Project { Id = 11, Name = "Other" });
        _db.ProjectSubscriptions.AddRange(
            new ProjectSubscription { UserId = 1, ProjectId = ProjectId },
            new ProjectSubscription { UserId = 2, ProjectId = ProjectId },
            new ProjectSubscription { UserId = 3, ProjectId = ProjectId },
            new ProjectSubscription { UserId = 4, ProjectId = ProjectId },
            new ProjectSubscription { UserId = 5, ProjectId = 11 });
        _db.UserNotificationPreferences.AddRange(
            new UserNotificationPreference { UserId = 2, EventType = NotificationEventTypes.PipelineFailed, IsEnabled = false },
            new UserNotificationPreference { UserId = 2, EventType = NotificationEventTypes.PipelineSucceeded, IsEnabled = true },
            new UserNotificationPreference { UserId = 3, EventType = NotificationEventTypes.PipelineFailed, IsEnabled = true });
        _db.SaveChanges();
    }
}
