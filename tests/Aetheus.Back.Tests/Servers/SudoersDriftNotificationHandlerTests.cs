// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Components.Servers.Handlers;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Servers;

/// <summary>Exercise the notification service and repository to persist sudoers drift for active
/// administrators and dispatch alert.triggered rules independently of project
/// subscriptions.</summary>
public sealed class SudoersDriftNotificationHandlerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Now));
    private readonly AppDbContext _db;
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly SudoersDriftNotificationHandler _sut;

    public SudoersDriftNotificationHandlerTests()
    {
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            _time);
        var admin = new Role { Id = 1, Name = "Admin" };
        var viewer = new Role { Id = 2, Name = "Viewer" };
        _db.Roles.AddRange(admin, viewer);
        _db.Users.AddRange(
            new User { Id = 1, Username = "alice", IsActive = true },
            new User { Id = 2, Username = "bob", IsActive = false },
            new User { Id = 3, Username = "carol", IsActive = true },
            new User { Id = 4, Username = "dave", IsActive = true });
        _db.UserRoles.AddRange(
            new UserRole { UserId = 1, RoleId = 1 },
            new UserRole { UserId = 2, RoleId = 1 },
            new UserRole { UserId = 3, RoleId = 2 },
            new UserRole { UserId = 4, RoleId = 1 });
        _db.SaveChanges();
        _sut = new SudoersDriftNotificationHandler(
            new UserNotificationService(
                new UserNotificationRepository(_db), Substitute.For<IResourceAuthorizationService>(), _time),
            _notifications);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Drift_IsRecordedForEveryActiveAdministrator_AndSentToTheAlertRules()
    {
        var ct = TestContext.Current.CancellationToken;

        await _sut.HandleAsync(
            new SudoersDriftDetectedEvent(7, "web-1", "aetheus-agent", "Sudoers drop-in changed out-of-band", false, Now),
            ct);

        var deliveries = await _db.NotificationDeliveries.OrderBy(item => item.RecipientUserId).ToListAsync(ct);
        Assert.Equal([1, 4], deliveries.Select(item => item.RecipientUserId));
        Assert.All(deliveries, delivery =>
        {
            Assert.Equal("alert.triggered", delivery.EventType);
            Assert.Equal("Sec-Audit: sudoers drift on server web-1", delivery.Subject);
            Assert.Equal(NotificationDeliveryStatus.NotConfigured, delivery.Status);
            Assert.Null(delivery.ReadAt);
            using var payload = JsonDocument.Parse(delivery.PayloadJson);
            Assert.Equal(7, payload.RootElement.GetProperty("ServerId").GetInt32());
            Assert.Equal("Critical", payload.RootElement.GetProperty("Severity").GetString());
            Assert.Equal("aetheus-agent", payload.RootElement.GetProperty("Files").GetString());
        });
        await _notifications.Received(1).SendEventAsync("alert.triggered", Arg.Any<object>(), ct);
    }

    [Fact]
    public async Task Reminder_SaysTheDriftIsStillPresent()
    {
        var ct = TestContext.Current.CancellationToken;

        await _sut.HandleAsync(
            new SudoersDriftDetectedEvent(7, "web-1", "aetheus-agent", "Sudoers drop-in changed out-of-band", true, Now),
            ct);

        Assert.All(await _db.NotificationDeliveries.ToListAsync(ct), delivery =>
            Assert.Equal("Sec-Audit: sudoers drift still present on server web-1", delivery.Subject));
    }
}
