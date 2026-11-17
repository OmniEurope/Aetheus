// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Aetheus.Back.Services.Email;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Decision of 2026-11-16: each user chooses for himself whether a pipeline's failure is e-mailed to him;
/// a pipeline started by a schedule (a nightly) mails nothing by default. The in-app notification is unchanged.</summary>
public sealed class PipelineFailureMailPreferenceTests : IDisposable
{
    private const int ProjectId = 10;
    private const int Manual = 100;
    private const int Nightly = 101;
    private const int OtherManual = 102;

    private readonly AppDbContext _db;
    private readonly INotificationEmailer _emailer = Substitute.For<INotificationEmailer>();
    private readonly UserNotificationService _sut;

    public PipelineFailureMailPreferenceTests()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, time);
        var authz = Substitute.For<IResourceAuthorizationService>();
        authz.GetUsernamesWithPermissionAsync(Arg.Any<IReadOnlyCollection<string>>(), ResourceType.Project, ProjectId, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(call => new HashSet<string>(call.ArgAt<IReadOnlyCollection<string>>(0), StringComparer.Ordinal));
        _emailer.SendAsync(Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<NotificationEmail>>().Select(_ => EmailSendResult.Sent("no-reply@x.org")).ToList());
        _sut = new UserNotificationService(new UserNotificationRepository(_db), authz, time, emailer: _emailer);

        _db.Users.AddRange(
            new User { Id = 1, Username = "alice", Email = "alice@x.org" },
            new User { Id = 2, Username = "bob", Email = "bob@x.org" },
            new User { Id = 3, Username = "carol", Email = "carol@x.org" });
        _db.Projects.Add(new Project { Id = ProjectId, Name = "Shop" });
        _db.Pipelines.AddRange(
            new Pipeline { Id = Manual, Name = "ci", ProjectId = ProjectId, TriggerType = PipelineTriggerType.Manual },
            new Pipeline { Id = Nightly, Name = "nightly", ProjectId = ProjectId, TriggerType = PipelineTriggerType.Schedule },
            new Pipeline { Id = OtherManual, Name = "deploy", ProjectId = ProjectId, TriggerType = PipelineTriggerType.Webhook });
        _db.ProjectSubscriptions.AddRange(
            new ProjectSubscription { UserId = 1, ProjectId = ProjectId },
            new ProjectSubscription { UserId = 2, ProjectId = ProjectId },
            new ProjectSubscription { UserId = 3, ProjectId = ProjectId });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<int> FailureOfAsync(int pipelineId) =>
        _sut.RecordProjectEventAsync(
            NotificationEventTypes.PipelineFailed, $"{{\"PipelineRunId\":1,\"PipelineId\":{pipelineId},\"ProjectId\":{ProjectId}}}", Ct);

    private static bool MailedExactly(IReadOnlyList<NotificationEmail> emails, params string[] addresses) =>
        emails.Select(email => email.To).Order().SequenceEqual(addresses.Order());

    [Fact]
    public async Task APipeline_FailureMailsAreSentToEveryoneByDefault()
    {
        Assert.Equal(3, await FailureOfAsync(Manual));

        await _emailer.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<NotificationEmail>>(emails => MailedExactly(emails, "alice@x.org", "bob@x.org", "carol@x.org")), Ct);
    }

    [Fact]
    public async Task AUserWhoTurnedItOffForAPipeline_IsNotMailed_OthersStillAre_AndEveryoneKeepsTheInAppNotification()
    {
        await _sut.SetPipelineFailureMailPreferenceAsync(2, Manual, false, Ct);

        var written = await FailureOfAsync(Manual);

        Assert.Equal(3, written);
        await _emailer.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<NotificationEmail>>(emails => MailedExactly(emails, "alice@x.org", "carol@x.org")), Ct);
        var rows = await _db.NotificationDeliveries.OrderBy(row => row.RecipientUserId).ToListAsync(Ct);
        Assert.Equal(3, rows.Count);
        Assert.Equal(NotificationDeliveryStatus.NotConfigured, rows[1].Status);
        Assert.Contains("turned off failure e-mails for this pipeline", rows[1].ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(NotificationDeliveryStatus.Sent, rows[0].Status);
    }

    [Fact]
    public async Task TheChoiceIsPerPipeline_AnotherPipelineStillMailsTheUser()
    {
        await _sut.SetPipelineFailureMailPreferenceAsync(2, Manual, false, Ct);

        await FailureOfAsync(OtherManual);

        await _emailer.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<NotificationEmail>>(emails => MailedExactly(emails, "alice@x.org", "bob@x.org", "carol@x.org")), Ct);
    }

    [Fact]
    public async Task APipelineChoice_DoesNotTurnTheWholeFailureTypeOffOrOnForTheUser()
    {
        await _sut.SetPipelineFailureMailPreferenceAsync(2, Manual, false, Ct);

        var preferences = await _sut.GetPreferencesAsync(2, Ct);

        var failed = preferences.Single(preference => preference.EventType == NotificationEventTypes.PipelineFailed);
        Assert.True(failed.IsEnabled);
        Assert.False(failed.IsSaved);
    }

    [Fact]
    public async Task AScheduledPipeline_MailsNobodyByDefault_ButStaysInEveryInbox()
    {
        var written = await FailureOfAsync(Nightly);

        Assert.Equal(3, written);
        await _emailer.DidNotReceive().SendAsync(Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>());
        var rows = await _db.NotificationDeliveries.ToListAsync(Ct);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Contains("started by a schedule", row.ErrorMessage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AScheduledPipeline_MailsOnlyTheUserWhoTurnedItBackOn()
    {
        await _sut.SetPipelineFailureMailPreferenceAsync(3, Nightly, true, Ct);

        await FailureOfAsync(Nightly);

        await _emailer.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<NotificationEmail>>(emails => MailedExactly(emails, "carol@x.org")), Ct);
    }

    [Fact]
    public async Task Preference_ReportsTheEffectiveValue_TheDefault_AndWhetherTheUserSavedOne()
    {
        var manual = await _sut.GetPipelineFailureMailPreferenceAsync(1, Manual, Ct);
        var nightly = await _sut.GetPipelineFailureMailPreferenceAsync(1, Nightly, Ct);
        Assert.True(manual!.IsEnabled);
        Assert.False(manual.IsSaved);
        Assert.False(nightly!.IsEnabled);
        Assert.True(nightly.IsScheduled);
        Assert.False(nightly.DefaultEnabled);

        var turnedOn = await _sut.SetPipelineFailureMailPreferenceAsync(1, Nightly, true, Ct);
        Assert.True(turnedOn!.IsEnabled);
        Assert.True(turnedOn.IsSaved);
        Assert.True((await _sut.GetPipelineFailureMailPreferenceAsync(1, Nightly, Ct))!.IsEnabled);
        // Each user has his own: another user still has the default.
        Assert.False((await _sut.GetPipelineFailureMailPreferenceAsync(2, Nightly, Ct))!.IsEnabled);
    }

    [Fact]
    public async Task Preference_OfAnUnknownPipeline_IsNull()
    {
        Assert.Null(await _sut.GetPipelineFailureMailPreferenceAsync(1, 999, Ct));
        Assert.Null(await _sut.SetPipelineFailureMailPreferenceAsync(1, 999, true, Ct));
        Assert.Empty(await _db.UserPipelineNotificationPreferences.ToListAsync(Ct));
    }

    [Fact]
    public async Task Choices_ListOnlyTheUsersOwn_AmongTheReadablePipelines()
    {
        await _sut.SetPipelineFailureMailPreferenceAsync(1, Manual, false, Ct);
        await _sut.SetPipelineFailureMailPreferenceAsync(1, Nightly, true, Ct);
        await _sut.SetPipelineFailureMailPreferenceAsync(2, OtherManual, false, Ct);

        var all = await _sut.GetPipelineFailureMailChoicesAsync(1, null, Ct);
        var readable = await _sut.GetPipelineFailureMailChoicesAsync(1, [Nightly], Ct);

        Assert.Equal(["ci", "nightly"], all.Select(choice => choice.PipelineName));
        Assert.Equal([Nightly], readable.Select(choice => choice.PipelineId));
        Assert.True(readable[0].IsEnabled);
        Assert.False(readable[0].DefaultEnabled);
    }

    [Fact]
    public async Task Controller_ChangesOnlyTheCallersOwnChoice_AndOnlyOnAReadablePipeline()
    {
        var service = Substitute.For<IUserNotificationService>();
        var authz = Substitute.For<IResourceAuthorizationService>();
        authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, Manual, Permission.Read, Arg.Any<CancellationToken>()).Returns(true);
        authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, Nightly, Permission.Read, Arg.Any<CancellationToken>()).Returns(false);
        service.SetPipelineFailureMailPreferenceAsync(2, Manual, false, Arg.Any<CancellationToken>())
            .Returns(new PipelineFailureMailPreferenceDto { PipelineId = Manual });
        var controller = new UserNotificationsController(service, authz)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "2")], "test"))
                }
            }
        };

        var allowed = await controller.SetPipelineFailureMail(Manual, new SetPipelineFailureMailPreferenceRequest { IsEnabled = false }, Ct);
        var refused = await controller.SetPipelineFailureMail(Nightly, new SetPipelineFailureMailPreferenceRequest { IsEnabled = false }, Ct);

        Assert.IsType<OkObjectResult>(allowed.Result);
        Assert.IsType<ForbidResult>(refused.Result);
        await service.Received(1).SetPipelineFailureMailPreferenceAsync(2, Manual, false, Arg.Any<CancellationToken>());
        await service.DidNotReceive().SetPipelineFailureMailPreferenceAsync(Arg.Any<int>(), Nightly, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
