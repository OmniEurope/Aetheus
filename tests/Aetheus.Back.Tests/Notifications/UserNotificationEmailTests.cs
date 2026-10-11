// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Aetheus.Back.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// Recette R1-16: every user notification also leaves by e-mail to the account address, and its row
/// records the real outcome of that e-mail.
/// </summary>
public sealed class UserNotificationEmailTests : IDisposable
{
    private const int ProjectId = 10;
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);

    private readonly AppDbContext _db;
    private readonly INotificationEmailer _emailer = Substitute.For<INotificationEmailer>();
    private readonly UserNotificationService _sut;

    public UserNotificationEmailTests()
    {
        var time = new FakeTimeProvider(Now);
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            time);
        var authz = Substitute.For<IResourceAuthorizationService>();
        authz.HasPermissionAsync(Arg.Any<string>(), ResourceType.Project, ProjectId, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _sut = new UserNotificationService(new UserNotificationRepository(_db), authz, time, emailer: _emailer);

        _db.Users.AddRange(
            new User { Id = 1, Username = "alice", Email = "alice@x.org" },
            new User { Id = 2, Username = "bob", Email = "bob@x.org" },
            new User { Id = 3, Username = "carol" });
        _db.Projects.Add(new Project { Id = ProjectId, Name = "Shop" });
        _db.ProjectSubscriptions.AddRange(
            new ProjectSubscription { UserId = 1, ProjectId = ProjectId },
            new ProjectSubscription { UserId = 2, ProjectId = ProjectId },
            new ProjectSubscription { UserId = 3, ProjectId = ProjectId });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProjectEvent_IsMailedToEachSubscriber_AndEachRowKeepsItsOutcome()
    {
        _emailer.SendAsync(Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var emails = call.Arg<IReadOnlyList<NotificationEmail>>();
                return emails.Select(email => email.To switch
                {
                    "alice@x.org" => EmailSendResult.Sent("no-reply@x.org"),
                    "bob@x.org" => EmailSendResult.Failed("552 5.2.2 mailbox full"),
                    _ => EmailSendResult.NotConfigured(NotificationEmailer.NoAddressMessage)
                }).ToList();
            });

        var written = await _sut.RecordProjectEventAsync(
            NotificationEventTypes.PipelineFailed, $"{{\"ProjectId\":{ProjectId}}}", Ct);

        Assert.Equal(3, written);
        var rows = await _db.NotificationDeliveries.OrderBy(row => row.RecipientUserId).ToListAsync(Ct);
        Assert.Equal(NotificationDeliveryStatus.Sent, rows[0].Status);
        Assert.Equal(Now.UtcDateTime, rows[0].SentAt);
        Assert.Null(rows[0].ErrorMessage);
        Assert.Equal(NotificationDeliveryStatus.Failed, rows[1].Status);
        Assert.Equal("552 5.2.2 mailbox full", rows[1].ErrorMessage);
        Assert.Null(rows[1].SentAt);
        Assert.Equal(NotificationDeliveryStatus.NotConfigured, rows[2].Status);
        Assert.Equal(NotificationEmailer.NoAddressMessage, rows[2].ErrorMessage);

        await _emailer.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<NotificationEmail>>(emails =>
                emails.Count == 3
                && emails[0].To == "alice@x.org"
                && emails[2].To == null
                && emails.All(email => email.Subject == "pipeline.failed in project Shop")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdministratorEvent_IsMailedToTheAdministrators()
    {
        var role = new Role { Id = 1, Name = "Admin" };
        _db.Roles.Add(role);
        _db.UserRoles.Add(new UserRole { UserId = 1, RoleId = 1 });
        await _db.SaveChangesAsync(Ct);
        _emailer.SendAsync(Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>())
            .Returns([EmailSendResult.Sent("no-reply@x.org")]);

        await _sut.RecordAdministratorEventAsync("alert.triggered", "Sudoers drift on vps", "{}", Ct);

        var row = await _db.NotificationDeliveries.SingleAsync(Ct);
        Assert.Equal(1, row.RecipientUserId);
        Assert.Equal(NotificationDeliveryStatus.Sent, row.Status);
        await _emailer.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<NotificationEmail>>(emails => emails.Single().To == "alice@x.org"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SmtpErrorLongerThanTheColumn_IsCut_NotLost()
    {
        _emailer.SendAsync(Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<NotificationEmail>>()
                .Select(_ => EmailSendResult.Failed(new string('x', 2500))).ToList());

        await _sut.RecordProjectEventAsync(NotificationEventTypes.PipelineFailed, $"{{\"ProjectId\":{ProjectId}}}", Ct);

        Assert.All(_db.NotificationDeliveries, row => Assert.Equal(2000, row.ErrorMessage!.Length));
    }
}
