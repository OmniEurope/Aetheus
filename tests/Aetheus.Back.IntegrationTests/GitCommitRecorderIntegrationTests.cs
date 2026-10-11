// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R1-21 on a real PostgreSQL: a commit written by Aetheus marks the repository not empty in
/// one UPDATE of its row, and leaves the caller's other pending changes unsaved (a pipeline being
/// saved or rolled back in the same unit of work is not flushed by it).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class GitCommitRecorderIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task MarkCommitted_ClearsTheEmptyFlag_WithoutFlushingOtherPendingChanges()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        int repositoryId, projectId;
        await using (var db = NewContext())
        {
            var organization = new Organization { Name = "Org R1-21", Slug = "orgr121", Description = "d" };
            db.Organizations.Add(organization);
            await db.SaveChangesAsync(ct);
            var project = new Project { Name = "Ats", Description = "d", OrganizationId = organization.Id };
            db.Projects.Add(project);
            await db.SaveChangesAsync(ct);
            var repository = new GitInternalRepo { ProjectId = project.Id, Name = "ats", Slug = "ats", IsEmpty = true };
            db.GitInternalRepos.Add(repository);
            await db.SaveChangesAsync(ct);
            (repositoryId, projectId) = (repository.Id, project.Id);
        }

        var committedAt = new DateTime(2026, 10, 9, 11, 28, 0, DateTimeKind.Utc);
        await using (var db = NewContext())
        {
            var project = await db.Projects.SingleAsync(item => item.Id == projectId, ct);
            project.Description = "pending, not saved";

            Assert.Equal(projectId, await new GitLightRepository(db).MarkCommittedAsync(repositoryId, committedAt, ct));
        }

        await using (var verify = NewContext())
        {
            var stored = await verify.GitInternalRepos.AsNoTracking().SingleAsync(item => item.Id == repositoryId, ct);
            Assert.False(stored.IsEmpty);
            Assert.Equal(committedAt, stored.LastPushAt);
            Assert.Equal("d", (await verify.Projects.AsNoTracking().SingleAsync(item => item.Id == projectId, ct)).Description);
        }
    }
}
