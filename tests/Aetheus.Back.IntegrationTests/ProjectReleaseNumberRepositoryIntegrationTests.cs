// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R2-110: the per-project release counter behind <c>BUILD_RELEASENUMBER</c>. As for the build
/// number, the contracts that matter exist only against PostgreSQL: two releases of one project started
/// together never take the same number, and the reservation joins an ambient transaction.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProjectReleaseNumberRepositoryIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    [Fact]
    public async Task TheFirstReleaseIsZero_AndTheLatestFollowsTheAllocations()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var projectId = await SeedProjectAsync(db);
        var sut = new ProjectReleaseNumberRepository(db);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(0, await sut.GetLatestAsync(projectId, ct));
        Assert.Equal(0, await sut.AllocateNextAsync(projectId, ct));
        Assert.Equal(1, await sut.AllocateNextAsync(projectId, ct));
        Assert.Equal(1, await sut.GetLatestAsync(projectId, ct));
    }

    [Fact]
    public async Task ConcurrentReleases_NeverTakeTheSameNumber()
    {
        await ResetAndMigrateAsync();
        await using var seed = NewContext();
        var projectId = await SeedProjectAsync(seed);

        var numbers = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = NewContext();
            return await new ProjectReleaseNumberRepository(db)
                .AllocateNextAsync(projectId, TestContext.Current.CancellationToken);
        }));

        Assert.Equal(Enumerable.Range(0, 8).Select(number => (int?)number), numbers.Order());
    }

    [Fact]
    public async Task AllocateNext_InsideAnAmbientTransaction_JoinsIt()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var projectId = await SeedProjectAsync(db);
        var ct = TestContext.Current.CancellationToken;

        await using var ambient = await db.Database.BeginTransactionAsync(ct);
        Assert.Equal(0, await new ProjectReleaseNumberRepository(db).AllocateNextAsync(projectId, ct));
        Assert.NotNull(db.Database.CurrentTransaction);
        await ambient.RollbackAsync(ct);

        await using var verify = NewContext();
        Assert.Equal(0, await verify.Projects.Where(project => project.Id == projectId)
            .Select(project => project.ReleaseCounter).SingleAsync(ct));
    }

    [Fact]
    public async Task AProjectThatNoLongerExists_HasNoNumber()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var sut = new ProjectReleaseNumberRepository(db);

        Assert.Null(await sut.AllocateNextAsync(999_999, TestContext.Current.CancellationToken));
        Assert.Null(await sut.GetLatestAsync(999_999, TestContext.Current.CancellationToken));
    }

    private static async Task<int> SeedProjectAsync(AppDbContext db)
    {
        var organization = new Organization { Name = "Releases", Slug = $"releases-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var project = new Project { Name = $"release-counter-{Guid.NewGuid():N}", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return project.Id;
    }
}
