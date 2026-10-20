// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R1-72, decision D2, on PostgreSQL: the retention rule (dating fallback, correlated rollback
/// window, navigation and outbox subqueries) translates, the archive update repeats it, and archiving a
/// release keeps the row and its commit while its artifacts and runs are deleted (the run's foreign keys
/// set to null by the database). The 90-day run purge no longer cascades into a live release's runs and
/// artifacts.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReleaseRetentionIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime July = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private sealed record Seeded(int ProjectId, int PipelineId);

    private async Task<Seeded> SeedProjectAsync(AppDbContext db, CancellationToken ct)
    {
        var organization = new Organization { Name = "Retention", Slug = $"retention-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = $"retention-{Guid.NewGuid():N}", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        var pipeline = new Pipeline { Name = "ci", ProjectId = project.Id, YamlDefinition = "name: ci\ntrigger: manual\nstages: []" };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);
        return new Seeded(project.Id, pipeline.Id);
    }

    private static Release NewRelease(int projectId, ReleaseStatus status, DateTime detectedAt, DateTime? publishedAt, int? createdByRunId = null) =>
        new()
        {
            ProjectId = projectId,
            Version = Guid.NewGuid().ToString("N")[..8],
            Status = status,
            DetectedAt = detectedAt,
            PublishedAt = publishedAt,
            CreatedByPipelineRunId = createdByRunId,
            CommitHash = Commit
        };

    [Fact]
    public async Task OldUnprotectedReleases_AreArchived_WithTheirDataDeleted_AndTheProtectedOnesStay()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        int published, undated, undatable, deployed, rolledBackTo, newest, artifactId, runId;
        await using (var db = NewContext())
        {
            var seeded = await SeedProjectAsync(db, ct);
            var run = new PipelineRun { PipelineId = seeded.PipelineId, Status = PipelineStatus.Success, StartedAt = July };
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(ct);
            var artifact = new PipelineArtifact
            {
                PipelineId = seeded.PipelineId,
                ProjectId = seeded.ProjectId,
                PipelineRunId = run.Id,
                Name = "payload",
                FilePath = "1/1/1/payload.zip",
                RetentionExpiresAt = July
            };

            var releases = new[]
            {
                NewRelease(seeded.ProjectId, ReleaseStatus.Published, July, July),
                NewRelease(seeded.ProjectId, ReleaseStatus.Published, DateTime.MinValue, null, run.Id),
                NewRelease(seeded.ProjectId, ReleaseStatus.Published, DateTime.MinValue, null),
                NewRelease(seeded.ProjectId, ReleaseStatus.Deployed, July, July),
                NewRelease(seeded.ProjectId, ReleaseStatus.Published, July, July)
            };
            releases[0].Artifacts.Add(artifact);
            db.Releases.AddRange(releases);
            await db.SaveChangesAsync(ct);
            var latest = NewRelease(seeded.ProjectId, ReleaseStatus.Published, July, July);
            db.Releases.Add(latest);
            await db.SaveChangesAsync(ct);
            db.ReleaseRollbacks.Add(new ReleaseRollback
            {
                SourceReleaseId = releases[3].Id,
                TargetReleaseId = releases[4].Id,
                PipelineId = seeded.PipelineId,
                RequestedAt = July
            });
            await db.SaveChangesAsync(ct);
            (published, undated, undatable, deployed, rolledBackTo, newest, artifactId, runId) =
                (releases[0].Id, releases[1].Id, releases[2].Id, releases[3].Id, releases[4].Id, latest.Id, artifact.Id, run.Id);
        }

        await using (var db = NewContext())
        {
            var repository = new ReleaseRetentionRepository(db);
            var cutoff = Now.AddDays(-30);
            var candidates = await repository.GetExpiredReleaseIdsAsync(cutoff, Now, 100, ct);
            Assert.Equal([published, undated], candidates);

            Assert.Equal(2, await repository.ArchiveExpiredAsync(candidates, cutoff, Now, ct));
            Assert.Equal(0, await repository.ArchiveExpiredAsync(candidates, cutoff, Now, ct));
            Assert.Empty(await repository.GetExpiredReleaseIdsAsync(cutoff, Now, 100, ct));
        }

        await using var check = NewContext();
        var remaining = await check.Releases.AsNoTracking().OrderBy(release => release.Id).ToListAsync(ct);
        Assert.Equal([published, undated, undatable, deployed, rolledBackTo, newest], remaining.Select(release => release.Id));
        Assert.All(remaining.Where(release => release.Id == published || release.Id == undated), release =>
        {
            Assert.Equal(ReleaseStatus.Archived, release.Status);
            Assert.Equal(Now, release.ArchivedAt);
            Assert.Equal(Commit, release.CommitHash);
        });
        Assert.All(remaining.Where(release => release.Id != published && release.Id != undated),
            release => Assert.NotEqual(ReleaseStatus.Archived, release.Status));
        Assert.False(await check.PipelineArtifacts.AnyAsync(a => a.Id == artifactId, ct));
        Assert.False(await check.PipelineRuns.AnyAsync(r => r.Id == runId, ct));
        Assert.Null(remaining.Single(release => release.Id == undated).CreatedByPipelineRunId);
    }

    [Fact]
    public async Task TheRunPurge_KeepsTheRunsAndArtifactsOfALiveRelease_AndDeletesThoseOfAnArchivedOne()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var old = Now.AddDays(-120);
        int liveRunId, payloadRunId, archivedRunId, artifactId;
        await using (var db = NewContext())
        {
            var seeded = await SeedProjectAsync(db, ct);
            var liveRun = new PipelineRun { PipelineId = seeded.PipelineId, Status = PipelineStatus.Success, StartedAt = old };
            var payloadRun = new PipelineRun { PipelineId = seeded.PipelineId, Status = PipelineStatus.Success, StartedAt = old };
            var archivedRun = new PipelineRun { PipelineId = seeded.PipelineId, Status = PipelineStatus.Success, StartedAt = old };
            db.PipelineRuns.AddRange(liveRun, payloadRun, archivedRun);
            await db.SaveChangesAsync(ct);
            var artifact = new PipelineArtifact
            {
                PipelineId = seeded.PipelineId,
                ProjectId = seeded.ProjectId,
                PipelineRunId = payloadRun.Id,
                Name = "payload",
                FilePath = "1/1/2/payload.zip"
            };
            var live = NewRelease(seeded.ProjectId, ReleaseStatus.Deployed, old, old, liveRun.Id);
            live.Artifacts.Add(artifact);
            db.Releases.AddRange(live, NewRelease(seeded.ProjectId, ReleaseStatus.Archived, old, old, archivedRun.Id));
            await db.SaveChangesAsync(ct);
            (liveRunId, payloadRunId, archivedRunId, artifactId) = (liveRun.Id, payloadRun.Id, archivedRun.Id, artifact.Id);
        }

        await using (var db = NewContext())
        {
            var repository = new PipelineRepository(
                db, TimeProvider.System, NullLogger<PipelineRepository>.Instance,
                new PipelineTaskLifecycleRepository(db, TimeProvider.System), new PipelineRunLineageRepository(db));
            Assert.Equal(1, await repository.DeleteRunsOlderThanAsync(Now.AddDays(-90), ct));
        }

        await using var check = NewContext();
        var runs = await check.PipelineRuns.Select(run => run.Id).ToListAsync(ct);
        Assert.Contains(liveRunId, runs);
        Assert.Contains(payloadRunId, runs);
        Assert.DoesNotContain(archivedRunId, runs);
        Assert.True(await check.PipelineArtifacts.AnyAsync(a => a.Id == artifactId, ct));
    }
}
