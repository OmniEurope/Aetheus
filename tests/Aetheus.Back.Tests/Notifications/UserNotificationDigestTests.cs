// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Aetheus.Back.Services.Email;
using Aetheus.Shared.Components.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Recette R-26: an event that lists several projects (<c>analysis.cve.detected</c>) reaches each follower as ONE
/// delivery and ONE mail, narrowed to the projects they follow and can still read. The recipients, subscriptions, preferences
/// and deliveries come from the real service and repository on an in-memory database; the sender is a double, so nothing
/// here proves an SMTP delivery.</summary>
public sealed class UserNotificationDigestTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 7, 0, 0, TimeSpan.Zero);

    private readonly AppDbContext _db;
    private readonly INotificationEmailer _emailer = Substitute.For<INotificationEmailer>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly UserNotificationService _sut;

    public UserNotificationDigestTests()
    {
        var time = new FakeTimeProvider(Now);
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, time);
        // Every follower reads every project, unless a test says otherwise.
        _authz.GetUsernamesWithPermissionAsync(Arg.Any<IReadOnlyCollection<string>>(), ResourceType.Project, Arg.Any<int>(), Permission.Read, Arg.Any<CancellationToken>())
            .Returns(call => new HashSet<string>(call.ArgAt<IReadOnlyCollection<string>>(0), StringComparer.Ordinal));
        _emailer.SendAsync(Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<NotificationEmail>>().Select(_ => EmailSendResult.Sent("no-reply@x.org")).ToList());
        _sut = new UserNotificationService(new UserNotificationRepository(_db), _authz, time, emailer: _emailer);

        _db.Users.AddRange(
            new User { Id = 1, Username = "alice", Email = "alice@x.org" },
            new User { Id = 2, Username = "bob", Email = "bob@x.org" },
            new User { Id = 3, Username = "carol", Email = "carol@x.org" });
        _db.Projects.AddRange(new Project { Id = 10, Name = "Alpha" }, new Project { Id = 11, Name = "Beta" }, new Project { Id = 12, Name = "Gamma" });
        _db.ProjectSubscriptions.AddRange(
            new ProjectSubscription { UserId = 1, ProjectId = 10 },
            new ProjectSubscription { UserId = 1, ProjectId = 11 },
            new ProjectSubscription { UserId = 2, ProjectId = 11 },
            new ProjectSubscription { UserId = 3, ProjectId = 12 });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Payload(params int[] projectIds) => JsonSerializer.Serialize(new
    {
        Digest = true,
        Projects = projectIds.Select(id => new { ProjectId = id, OrganizationId = 1, ProjectName = $"P{id}", Severity = "High" })
    });

    private static int[] ProjectsOf(NotificationDelivery delivery) =>
        [.. JsonDocument.Parse(delivery.PayloadJson).RootElement.GetProperty("Projects").EnumerateArray().Select(project => project.GetProperty("ProjectId").GetInt32())];

    [Fact]
    public async Task AFollowerOfSeveralProjects_GetsOneDeliveryAndOneMail_NotOnePerProject()
    {
        var written = await _sut.RecordProjectEventAsync(NotificationEventTypes.CveDetected, Payload(10, 11), Ct);

        // Alice follows both projects and Bob one: two deliveries, not three; Carol follows neither.
        Assert.Equal(2, written);
        var rows = await _db.NotificationDeliveries.OrderBy(row => row.RecipientUserId).ToListAsync(Ct);
        Assert.Equal([1, 2], rows.Select(row => row.RecipientUserId));
        Assert.Equal([10, 11], ProjectsOf(rows[0]));
        Assert.Equal([11], ProjectsOf(rows[1]));
        Assert.All(rows, row => Assert.Equal(NotificationDeliveryStatus.Sent, row.Status));
        await _emailer.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<NotificationEmail>>(emails =>
                emails.Count == 2 && emails.Select(email => email.To).SequenceEqual(new[] { "alice@x.org", "bob@x.org" })
                && emails.All(email => email.EventType == NotificationEventTypes.CveDetected)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AProjectTheFollowerCanNoLongerRead_IsLeftOutOfTheirMail()
    {
        _authz.GetUsernamesWithPermissionAsync(Arg.Any<IReadOnlyCollection<string>>(), ResourceType.Project, 10, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(StringComparer.Ordinal));

        await _sut.RecordProjectEventAsync(NotificationEventTypes.CveDetected, Payload(10, 11), Ct);

        var alice = await _db.NotificationDeliveries.SingleAsync(row => row.RecipientUserId == 1, Ct);
        Assert.Equal([11], ProjectsOf(alice));
        Assert.DoesNotContain("P10", alice.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFollowerWhoSwitchedTheEventOff_GetsNothing()
    {
        _db.UserNotificationPreferences.Add(new UserNotificationPreference { UserId = 2, EventType = NotificationEventTypes.CveDetected, IsEnabled = false });
        await _db.SaveChangesAsync(Ct);

        var written = await _sut.RecordProjectEventAsync(NotificationEventTypes.CveDetected, Payload(10, 11), Ct);

        Assert.Equal(1, written);
        Assert.Equal(1, (await _db.NotificationDeliveries.SingleAsync(Ct)).RecipientUserId);
    }

    [Fact]
    public async Task AnEventWithNoProjectAtAll_WritesNothing()
    {
        Assert.Equal(0, await _sut.RecordProjectEventAsync(NotificationEventTypes.CveDetected, """{"Digest":true,"Projects":[]}""", Ct));
        Assert.Equal(0, await _sut.RecordProjectEventAsync(NotificationEventTypes.CveDetected, "{}", Ct));
        Assert.Empty(_db.NotificationDeliveries);
        await _emailer.DidNotReceiveWithAnyArgs().SendAsync(default!, Ct);
    }
}
