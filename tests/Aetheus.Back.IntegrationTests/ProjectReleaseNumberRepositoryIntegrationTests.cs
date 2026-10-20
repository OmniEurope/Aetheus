// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary> The per-project, per-series release counter behind <c>BUILD_RELEASENUMBER</c>. As for the
/// build number, the contracts that matter exist only against PostgreSQL: two releases of one project
/// started together never take the same number, a series counts from 0 on its own, an administrator's
/// choice of the next number is honoured and never gives a version twice, and the reservation joins an
/// ambient transaction.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProjectReleaseNumberRepositoryIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProjectReleaseNumberRepository Repository(AppDbContext db) => new(db, TimeProvider.System);

    [Fact]
    public async Task TheFirstReleaseOfASeriesIsZero_AndTheLatestFollowsTheAllocations()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var projectId = await SeedProjectAsync(db);
        var sut = Repository(db);

        Assert.Equal(0, await sut.GetLatestAsync(projectId, "1.4", Ct));
        Assert.Equal(0, await sut.AllocateNextAsync(projectId, "1.4", Ct));
        Assert.Equal(1, await sut.AllocateNextAsync(projectId, "1.4", Ct));
        Assert.Equal(1, await sut.GetLatestAsync(projectId, "1.4", Ct));
    }

    [Fact]
    public async Task ASeriesWithReleasesContinues_AndANewSeriesStartsAtZero()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var projectId = await SeedProjectAsync(db);
        // 1.3 released 0..16 (production is at 1.3.16): the counter and the numbers it handed out.
        db.ProjectReleaseSeries.Add(new ProjectReleaseSeries { ProjectId = projectId, Series = "1.3", NextNumber = 17 });
        db.ProjectReleaseNumberAllocations.AddRange(Enumerable.Range(0, 17).Select(number => new ProjectReleaseNumberAllocation
        {
            ProjectId = projectId,
            Series = "1.3",
            Number = number,
            AllocatedAt = DateTime.UtcNow
        }));
        await db.SaveChangesAsync(Ct);
        var sut = Repository(db);

        Assert.Equal(17, await sut.AllocateNextAsync(projectId, "1.3", Ct));
        Assert.Equal(0, await sut.AllocateNextAsync(projectId, "1.4", Ct));
        Assert.Equal(1, await sut.AllocateNextAsync(projectId, "1.4", Ct));
        Assert.Equal(18, await sut.AllocateNextAsync(projectId, "1.3", Ct));
        Assert.Equal(18, await sut.GetLatestAsync(projectId, "1.3", Ct));
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
            return await Repository(db).AllocateNextAsync(projectId, "1.4", Ct);
        }));

        Assert.Equal(Enumerable.Range(0, 8).Select(number => (int?)number), numbers.Order());
    }

    [Fact]
    public async Task ConcurrentReleasesOfTwoSeries_EachTakeDistinctNumbersFromZero()
    {
        await ResetAndMigrateAsync();
        await using var seed = NewContext();
        var projectId = await SeedProjectAsync(seed);

        var taken = await Task.WhenAll(Enumerable.Range(0, 10).Select(async index =>
        {
            await using var db = NewContext();
            var series = index % 2 == 0 ? "1.3" : "1.4";
            return (Series: series, Number: await Repository(db).AllocateNextAsync(projectId, series, Ct));
        }));

        foreach (var series in new[] { "1.3", "1.4" })
            Assert.Equal(Enumerable.Range(0, 5).Select(number => (int?)number),
                taken.Where(item => item.Series == series).Select(item => item.Number).Order());
    }

    [Fact]
    public async Task SettingTheNextVersion_IsHonouredWhateverTheOtherSeriesReached()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var projectId = await SeedProjectAsync(db);
        var sut = Repository(db);
        for (var index = 0; index < 17; index++) await sut.AllocateNextAsync(projectId, "1.3", Ct);

        Assert.Equal(SetNextReleaseNumberOutcome.Set, await sut.SetNextAsync(projectId, "1.4", 0, Ct));
        Assert.Equal(0, await sut.AllocateNextAsync(projectId, "1.4", Ct));
        Assert.Equal(1, await sut.AllocateNextAsync(projectId, "1.4", Ct));

        Assert.Equal(SetNextReleaseNumberOutcome.Set, await sut.SetNextAsync(projectId, "1.4", 7, Ct));
        Assert.Equal(7, await sut.AllocateNextAsync(projectId, "1.4", Ct));
        Assert.Equal(8, await sut.AllocateNextAsync(projectId, "1.4", Ct));
        Assert.Equal(["1.4", "1.3"], (await sut.GetSeriesAsync(projectId, Ct))!.Select(item => item.Series));
    }

    [Fact]
    public async Task ANumberAlreadyTaken_IsRefusedAndTheCounterStays()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var projectId = await SeedProjectAsync(db);
        var sut = Repository(db);
        await sut.AllocateNextAsync(projectId, "1.4", Ct);
        await sut.AllocateNextAsync(projectId, "1.4", Ct);

        Assert.Equal(SetNextReleaseNumberOutcome.NumberAlreadyUsed, await sut.SetNextAsync(projectId, "1.4", 0, Ct));
        Assert.Equal(SetNextReleaseNumberOutcome.NumberAlreadyUsed, await sut.SetNextAsync(projectId, "1.4", 1, Ct));
        Assert.Equal(2, await sut.AllocateNextAsync(projectId, "1.4", Ct));
    }

    [Fact]
    public async Task ARaceBetweenAnAdministratorAndARelease_NeverGivesAVersionTwice()
    {
        await ResetAndMigrateAsync();
        await using var seed = NewContext();
        var projectId = await SeedProjectAsync(seed);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async index =>
        {
            await using var db = NewContext();
            var sut = Repository(db);
            if (index % 2 == 0) return (Taken: await sut.AllocateNextAsync(projectId, "1.4", Ct), Set: (SetNextReleaseNumberOutcome?)null);
            return (Taken: (int?)null, Set: await sut.SetNextAsync(projectId, "1.4", 0, Ct));
        }));

        await using var verify = NewContext();
        var recorded = await verify.ProjectReleaseNumberAllocations
            .Where(allocation => allocation.ProjectId == projectId && allocation.Series == "1.4")
            .Select(allocation => allocation.Number).ToListAsync(Ct);
        Assert.Equal(recorded.Count, recorded.Distinct().Count());
        Assert.Equal(recorded.Order(), outcomes.Where(item => item.Taken.HasValue).Select(item => item.Taken!.Value).Order());
    }

    [Fact]
    public async Task AllocateNext_InsideAnAmbientTransaction_JoinsIt()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var projectId = await SeedProjectAsync(db);

        await using var ambient = await db.Database.BeginTransactionAsync(Ct);
        Assert.Equal(0, await Repository(db).AllocateNextAsync(projectId, "1.4", Ct));
        Assert.NotNull(db.Database.CurrentTransaction);
        await ambient.RollbackAsync(Ct);

        await using var verify = NewContext();
        Assert.Equal(0, await verify.Projects.Where(project => project.Id == projectId)
            .Select(project => project.ReleaseCounter).SingleAsync(Ct));
        Assert.False(await verify.ProjectReleaseNumberAllocations.AnyAsync(allocation => allocation.ProjectId == projectId, Ct));
        Assert.False(await verify.ProjectReleaseSeries.AnyAsync(row => row.ProjectId == projectId, Ct));
    }

    [Fact]
    public async Task AProjectThatNoLongerExists_HasNoNumber()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var sut = Repository(db);

        Assert.Null(await sut.AllocateNextAsync(999_999, "1.4", Ct));
        Assert.Null(await sut.GetLatestAsync(999_999, "1.4", Ct));
        Assert.Null(await sut.GetSeriesAsync(999_999, Ct));
        Assert.Equal(SetNextReleaseNumberOutcome.ProjectNotFound, await sut.SetNextAsync(999_999, "1.4", 0, Ct));
    }

    private static async Task<int> SeedProjectAsync(AppDbContext db)
    {
        var organization = new Organization { Name = "Releases", Slug = $"releases-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        var project = new Project { Name = $"release-counter-{Guid.NewGuid():N}", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);
        return project.Id;
    }
}
