// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Checks the correlated retention query on PostgreSQL: the last three deployed releases
/// retain payloads beyond ordinary expiry.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ArtifactRetentionWindowIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    [Fact]
    public async Task GetExpiredAsync_KeepsTheLastThreeDeployedReleasesAndFreesTheFourth()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var ct = TestContext.Current.CancellationToken;
        var organization = new Organization { Name = "Retention", Slug = $"retention-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = $"retention-{Guid.NewGuid():N}", OrganizationId = organization.Id };
        var pipeline = new Pipeline { Name = $"retention-{Guid.NewGuid():N}" };
        db.Projects.Add(project);
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);
        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var expired = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var artifacts = new List<PipelineArtifact>();
        for (var index = 0; index < 4; index++)
        {
            var artifact = new PipelineArtifact
            {
                PipelineId = pipeline.Id,
                ProjectId = project.Id,
                PipelineRunId = run.Id,
                Name = $"payload-{index}",
                FilePath = $"/artifacts/payload-{index}",
                CreatedAt = expired.AddDays(-10),
                RetentionExpiresAt = expired
            };
            artifacts.Add(artifact);
            db.Releases.Add(new Release
            {
                ProjectId = project.Id,
                Version = $"1.0.{index}",
                Status = index == 3 ? ReleaseStatus.Deployed : ReleaseStatus.Superseded,
                DetectedAt = expired.AddDays(-10),
                PublishedAt = new DateTime(2026, 1, 1 + index, 0, 0, 0, DateTimeKind.Utc),
                Artifacts = [artifact]
            });
        }
        await db.SaveChangesAsync(ct);

        var result = await new ArtifactRepository(db).GetExpiredAsync(
            new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc), 10, ct);

        Assert.Equal(artifacts[0].Id, Assert.Single(result).Id);
    }
}
