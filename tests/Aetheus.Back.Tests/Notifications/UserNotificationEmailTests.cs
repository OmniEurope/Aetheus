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

/// <summary> Every user notification also leaves by e-mail to the account address, and its row
/// records the real outcome of that e-mail. </summary>
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

    [Fact]
    public async Task R177_AChildResult_IsMailedOnlyToTheSubscriberWhoEnabledTheOption_AndStaysInEveryInbox()
    {
        _db.UserNotificationPreferences.Add(new UserNotificationPreference
        {
            UserId = 2,
            EventType = NotificationEventTypes.PipelineChildResults,
            IsEnabled = true
        });
        await _db.SaveChangesAsync(Ct);
        _emailer.SendAsync(Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>())
            .Returns([EmailSendResult.Sent("no-reply@x.org")]);

        var written = await _sut.RecordProjectEventAsync(
            NotificationEventTypes.PipelineFailed, $"{{\"ProjectId\":{ProjectId}}}", Ct, NotificationEventTypes.PipelineChildResults);

        Assert.Equal(3, written);
        await _emailer.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<NotificationEmail>>(emails => emails.Count == 1 && emails[0].To == "bob@x.org"),
            Arg.Any<CancellationToken>());
        var rows = await _db.NotificationDeliveries.OrderBy(row => row.RecipientUserId).ToListAsync(Ct);
        Assert.Equal(3, rows.Count);
        Assert.Equal(NotificationDeliveryStatus.Sent, rows[1].Status);
        Assert.Equal(NotificationDeliveryStatus.NotConfigured, rows[0].Status);
        Assert.Contains(NotificationEventTypes.PipelineChildResults, rows[0].ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R177_AChildResult_MailsNobodyByDefault()
    {
        var written = await _sut.RecordProjectEventAsync(
            NotificationEventTypes.PipelineFailed, $"{{\"ProjectId\":{ProjectId}}}", Ct, NotificationEventTypes.PipelineChildResults);

        Assert.Equal(3, written);
        await _emailer.DidNotReceive().SendAsync(
            Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>());
        Assert.Equal(3, await _db.NotificationDeliveries.CountAsync(Ct));
    }

    [Fact]
    public async Task AiTaskCompleted_ReachesTheInbox_ButIsNotMailed()
    {
        // Recette R3-16: a finished AI task is announced in the inbox only, no e-mail for now.
        var written = await _sut.RecordProjectEventAsync(
            NotificationEventTypes.AiTaskCompleted, $"{{\"ProjectId\":{ProjectId}}}", Ct);

        Assert.Equal(3, written);
        await _emailer.DidNotReceive().SendAsync(
            Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>());
        // The existing state of a row recorded for the user with nothing sent: no status, no error text.
        Assert.All(_db.NotificationDeliveries, row =>
        {
            Assert.Equal(NotificationDeliveryStatus.NotConfigured, row.Status);
            Assert.Null(row.ErrorMessage);
            Assert.Null(row.SentAt);
        });
    }

    private static string RunPayload(int runId) => $"{{\"PipelineRunId\":{runId},\"ProjectId\":{ProjectId}}}";

    private static string Marker(int runId) => Aetheus.Back.Components.Pipelines.PipelineRunNotificationPublisher.EmailOnceMarker(runId);

    private void MailOutcomes() =>
        _emailer.SendAsync(Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<NotificationEmail>>().Select(email => email.To switch
            {
                "alice@x.org" => EmailSendResult.Sent("no-reply@x.org"),
                "bob@x.org" => EmailSendResult.Failed("451 try later"),
                _ => EmailSendResult.NotConfigured(NotificationEmailer.NoAddressMessage)
            }).ToList());

    [Fact]
    public async Task R177_ARetriedRunEndingTheSameWay_IsNotMailedAgainToWhoeverReceivedIt()
    {
        MailOutcomes();
        await _sut.RecordProjectEventAsync(NotificationEventTypes.PipelineFailed, RunPayload(9), Ct, null, Marker(9));
        _emailer.ClearReceivedCalls();

        var written = await _sut.RecordProjectEventAsync(NotificationEventTypes.PipelineFailed, RunPayload(9), Ct, null, Marker(9));

        // Every inbox gets the second result; only alice, whose first mail was accepted, is not mailed again.
        Assert.Equal(3, written);
        await _emailer.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<NotificationEmail>>(emails => emails.Count == 2
                && emails.Any(email => email.To == "bob@x.org") && emails.All(email => email.To != "alice@x.org")),
            Arg.Any<CancellationToken>());
        var aliceSecond = await _db.NotificationDeliveries.Where(row => row.RecipientUserId == 1).OrderBy(row => row.Id).LastAsync(Ct);
        Assert.Equal(NotificationDeliveryStatus.NotConfigured, aliceSecond.Status);
        Assert.Contains("already e-mailed", aliceSecond.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R177_ARetriedRunEndingDifferently_IsMailedItsNewResult()
    {
        MailOutcomes();
        await _sut.RecordProjectEventAsync(NotificationEventTypes.PipelineFailed, RunPayload(9), Ct, null, Marker(9));
        _emailer.ClearReceivedCalls();

        await _sut.RecordProjectEventAsync(NotificationEventTypes.PipelineRejected, RunPayload(9), Ct, null, Marker(9));

        await _emailer.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<NotificationEmail>>(emails => emails.Count == 3 && emails.Any(email => email.To == "alice@x.org")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R177_AnotherRunsResult_IsMailedEvenWhenItsIdStartsTheSame()
    {
        MailOutcomes();
        await _sut.RecordProjectEventAsync(NotificationEventTypes.PipelineFailed, RunPayload(9), Ct, null, Marker(9));
        _emailer.ClearReceivedCalls();

        await _sut.RecordProjectEventAsync(NotificationEventTypes.PipelineFailed, RunPayload(90), Ct, null, Marker(90));

        await _emailer.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<NotificationEmail>>(emails => emails.Count == 3 && emails.Any(email => email.To == "alice@x.org")),
            Arg.Any<CancellationToken>());
    }
}
