// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>R-30: an automated launch refused at preflight (here a port registered to another project) leaves a
/// failed run in the pipeline's own runs list and a notification for the project's followers, read back from
/// PostgreSQL through the real repository and the real notification service. The unit tests of the recorder
/// use a mocked repository and notification service; this is the real path between the layers.
/// The first test was written after the fact: the recorder already existed on develop.</summary>
public sealed class RefusedAutomatedLaunchIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private const string Reason = "This run cannot complete with the current configuration: Port 10000 on server 'vps' "
        + "is already registered to 'w10' (declared 2026-11-03).";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class StepClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<(int PipelineId, int FollowerId, int ProjectId)> SeedAsync()
    {
        await ResetAndMigrateAsync();
        await using var seed = NewContext();
        var organization = new Organization { Name = "Refusals", Slug = $"refusals-{Guid.NewGuid():N}" };
        seed.Organizations.Add(organization);
        await seed.SaveChangesAsync(Ct);
        var project = new Project { Name = "Aetheus", OrganizationId = organization.Id };
        var follower = new User
        {
            Username = "follower",
            Email = "follower@example.test",
            PasswordHash = "not-used",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        seed.Projects.Add(project);
        seed.Users.Add(follower);
        await seed.SaveChangesAsync(Ct);
        seed.ProjectSubscriptions.Add(new ProjectSubscription { UserId = follower.Id, ProjectId = project.Id, CreatedAt = DateTime.UtcNow });
        var pipeline = new Pipeline
        {
            Name = "aetheus-nightly",
            ProjectId = project.Id,
            YamlDefinition = "name: aetheus-nightly\nstages: []",
            CreatedByUsername = "follower"
        };
        seed.Pipelines.Add(pipeline);
        await seed.SaveChangesAsync(Ct);
        return (pipeline.Id, follower.Id, project.Id);
    }

    private PipelineRepository PipelinesOver(AppDbContext db, TimeProvider clock) =>
        new(db, clock, NullLogger<PipelineRepository>.Instance,
            new Aetheus.Back.Components.Tasks.PipelineTaskLifecycleRepository(db, clock),
            new PipelineRunLineageRepository(db));

    private async Task RecordAsync(int pipelineId, int projectId, TimeProvider clock, string source = "Scheduler")
    {
        await using var db = NewContext();
        var authorization = Substitute.For<IResourceAuthorizationService>();
        authorization.GetUsernamesWithPermissionAsync(
                Arg.Any<IReadOnlyCollection<string>>(), ResourceType.Project, projectId, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(call => new HashSet<string>(call.ArgAt<IReadOnlyCollection<string>>(0), StringComparer.Ordinal));
        var pipelines = PipelinesOver(db, clock);
        var recorder = new PipelineRefusedLaunchRecorder(
            pipelines, Substitute.For<IHubContext<PipelineHub>>(), Substitute.For<IAuditService>(),
            new PipelineRunNotificationPublisher(
                pipelines, new UserNotificationService(new UserNotificationRepository(db), authorization, clock)),
            clock, NullLogger<PipelineRefusedLaunchRecorder>.Instance, new DbTransactionScope(db));

        var pipeline = await db.Pipelines.AsNoTracking().SingleAsync(candidate => candidate.Id == pipelineId, Ct);
        await recorder.RecordAsync(pipeline, null, source, Reason, null, Ct);
    }

    [Fact]
    public async Task ARefusedScheduledLaunch_IsAFailedRunInTheRunsList_AndAnUnreadNotificationForAFollower()
    {
        var (pipelineId, followerId, projectId) = await SeedAsync();
        await RecordAsync(pipelineId, projectId, TimeProvider.System);

        await using var verify = NewContext();
        var (runs, total) = await PipelinesOver(verify, TimeProvider.System).GetRunsPagedAsync(pipelineId, 1, 25, null, Ct);
        Assert.Equal(1, total);
        Assert.Equal(PipelineStatus.Failed, runs[0].Status);

        var stored = await verify.PipelineRuns.AsNoTracking().SingleAsync(run => run.PipelineId == pipelineId, Ct);
        Assert.Contains("registered to", stored.WarningsJson!, StringComparison.Ordinal);
        Assert.Contains("w10", stored.WarningsJson!, StringComparison.Ordinal);

        var delivery = await verify.NotificationDeliveries.AsNoTracking().SingleAsync(row => row.RecipientUserId == followerId, Ct);
        Assert.Equal("pipeline.launch-refused", delivery.EventType);
        Assert.Contains("w10", delivery.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("aetheus-nightly", delivery.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R30_AnIdenticalRefusalEveryNight_StaysOneRun_ShowsTheLastNightAndItsCount_AndTellsTheFollowerEachNight()
    {
        var (pipelineId, followerId, projectId) = await SeedAsync();
        var clock = new StepClock(new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero));

        await RecordAsync(pipelineId, projectId, clock);
        // A burst in the same minute (a frequent schedule, several pushes): grouped, nobody told twice.
        clock.Now = clock.Now.AddSeconds(20);
        await RecordAsync(pipelineId, projectId, clock);
        // The next four nights, 00:00 UTC each.
        for (var night = 1; night <= 4; night++)
        {
            clock.Now = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero).AddDays(night);
            await RecordAsync(pipelineId, projectId, clock);
        }

        await using var verify = NewContext();
        var (runs, total) = await PipelinesOver(verify, TimeProvider.System).GetRunsPagedAsync(pipelineId, 1, 25, null, Ct);
        Assert.Equal(1, total);
        var run = await verify.PipelineRuns.AsNoTracking().SingleAsync(candidate => candidate.PipelineId == pipelineId, Ct);
        // The run carries its LAST refusal, so the list shows 2026-11-03 and not 2026-10-30.
        Assert.Equal(new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc), run.StartedAt.ToUniversalTime());
        Assert.Equal(run.StartedAt, run.CompletedAt);
        var warnings = System.Text.Json.JsonSerializer.Deserialize<string[]>(run.WarningsJson!)!;
        Assert.Equal("6", StoredProblem.ParameterOf(warnings[1], "count"));
        Assert.Equal(run.StartedAt, runs[0].StartedAt);

        // First refusal, then one per later night; the burst of the first night told nobody a second time.
        var deliveries = await verify.NotificationDeliveries.AsNoTracking()
            .Where(row => row.RecipientUserId == followerId && row.EventType == "pipeline.launch-refused")
            .ToListAsync(Ct);
        Assert.Equal(5, deliveries.Count);
    }
}
