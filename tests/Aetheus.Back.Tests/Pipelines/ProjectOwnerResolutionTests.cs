// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Pipelines;

// Audit F-INF-02: a pipeline auto-synced from committed YAML must be owned by a REAL user (the project's
// org Owner), not the non-existent "system" that the F-EXEC-1b authorization gate can never resolve.
public class ProjectOwnerResolutionTests
{
    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task GetProjectOwnerUsername_ReturnsOrgOwner()
    {
        using var db = NewDb();
        db.Users.Add(new User { Id = 7, Username = "alice", PasswordHash = "x" });
        db.Organizations.Add(new Organization { Id = 3, Name = "Acme" });
        db.OrganizationMembers.Add(new OrganizationMember { Id = 1, OrganizationId = 3, UserId = 7, Role = OrganizationRole.Owner });
        db.OrganizationMembers.Add(new OrganizationMember { Id = 2, OrganizationId = 3, UserId = 8, Role = OrganizationRole.Member });
        db.Projects.Add(new Project { Id = 5, Name = "proj", OrganizationId = 3 });
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var resolver = new PipelineProjectOwnerRepository(db);
        Assert.Equal("alice", await resolver.GetProjectOwnerUsernameAsync(5, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetProjectOwnerUsername_NoOwner_ReturnsNull()
    {
        using var db = NewDb();
        db.Projects.Add(new Project { Id = 5, Name = "proj", OrganizationId = 3 });
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var resolver = new PipelineProjectOwnerRepository(db);
        Assert.Null(await resolver.GetProjectOwnerUsernameAsync(5, ct: TestContext.Current.CancellationToken));
    }
}
