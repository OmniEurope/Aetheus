// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The release-number port (AUD-07, suite) answers what <c>PipelineReleaseNumbers</c> reads and
/// takes, per version series, and the IPipelineRepository facade, which delegates to it, moves the same
/// counters.</summary>
public sealed class PipelineReleaseNumberRepositoryPortTests : IDisposable
{
    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProjectReleaseNumberRepository Repository() => new(_db, TimeProvider.System);

    private IPipelineRepository Facade() => new PipelineRepository(
        _db, TimeProvider.System, NullLogger<PipelineRepository>.Instance,
        new PipelineTaskLifecycleRepository(_db, TimeProvider.System), new PipelineRunLineageRepository(_db));

    private async Task SeedAsync()
    {
        _db.Projects.Add(new Project { Id = 1, Name = "app" });
        _db.Pipelines.Add(new Pipeline { Id = 1, Name = "release", ProjectId = 1 });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 10, PipelineId = 1, ReleaseNumber = 4, ReleaseSeries = "1.3" },
            new PipelineRun { Id = 11, PipelineId = 1 });
        await _db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task ThePort_TakesTheNextNumberReadsTheLatestAndARunsOwn()
    {
        await SeedAsync();
        IPipelineReleaseNumberRepository port = Repository();

        Assert.Equal(0, await port.GetLatestReleaseNumberAsync(1, "1.4", Ct));
        Assert.Equal(0, await port.AllocateNextReleaseNumberAsync(1, "1.4", Ct));
        Assert.Equal(1, await port.AllocateNextReleaseNumberAsync(1, "1.4", Ct));
        Assert.Equal(1, await port.GetLatestReleaseNumberAsync(1, "1.4", Ct));
        Assert.Null(await port.AllocateNextReleaseNumberAsync(404, "1.4", Ct));
        Assert.Null(await port.GetLatestReleaseNumberAsync(404, "1.4", Ct));
        Assert.Equal(new ReleaseNumberAssignment("1.3", 4), await port.GetRunReleaseAsync(10, Ct));
        Assert.Null(await port.GetRunReleaseAsync(11, Ct));
        Assert.Null(await port.GetRunReleaseAsync(404, Ct));
    }

    [Fact]
    public async Task EachSeriesCountsFromZeroOnItsOwn()
    {
        await SeedAsync();
        var repository = Repository();

        Assert.Equal(0, await repository.AllocateNextAsync(1, "1.3", Ct));
        Assert.Equal(1, await repository.AllocateNextAsync(1, "1.3", Ct));
        Assert.Equal(2, await repository.AllocateNextAsync(1, "1.3", Ct));
        Assert.Equal(0, await repository.AllocateNextAsync(1, "1.4", Ct));
        Assert.Equal(3, await repository.AllocateNextAsync(1, "1.3", Ct));
        Assert.Equal(1, await repository.AllocateNextAsync(1, "1.4", Ct));
        Assert.Equal(3, await repository.GetLatestAsync(1, "1.3", Ct));
        Assert.Equal(1, await repository.GetLatestAsync(1, "1.4", Ct));
        Assert.Equal(0, await repository.GetLatestAsync(1, "2.0", Ct));
    }

    [Fact]
    public async Task ASeriesWithACounterContinuesFromIt()
    {
        await SeedAsync();
        _db.ProjectReleaseSeries.Add(new ProjectReleaseSeries { ProjectId = 1, Series = "1.3", NextNumber = 17 });
        await _db.SaveChangesAsync(Ct);

        Assert.Equal(17, await Repository().AllocateNextAsync(1, "1.3", Ct));
        Assert.Equal(0, await Repository().AllocateNextAsync(1, "1.4", Ct));
    }

    [Fact]
    public async Task SettingTheNextNumber_MakesItTheNextVersionAndTheOnesAfterFollow()
    {
        await SeedAsync();
        var repository = Repository();
        await repository.AllocateNextAsync(1, "1.3", Ct);

        Assert.Equal(SetNextReleaseNumberOutcome.Set, await repository.SetNextAsync(1, "1.4", 0, Ct));
        Assert.Equal(0, await repository.AllocateNextAsync(1, "1.4", Ct));
        Assert.Equal(1, await repository.AllocateNextAsync(1, "1.4", Ct));

        Assert.Equal(SetNextReleaseNumberOutcome.Set, await repository.SetNextAsync(1, "1.4", 10, Ct));
        Assert.Equal(10, await repository.AllocateNextAsync(1, "1.4", Ct));
        Assert.Equal(11, await repository.AllocateNextAsync(1, "1.4", Ct));
    }

    [Fact]
    public async Task SettingANumberASeriesAlreadyHandedOut_IsRefusedAndChangesNothing()
    {
        await SeedAsync();
        var repository = Repository();
        await repository.AllocateNextAsync(1, "1.4", Ct);
        await repository.AllocateNextAsync(1, "1.4", Ct);

        Assert.Equal(SetNextReleaseNumberOutcome.NumberAlreadyUsed, await repository.SetNextAsync(1, "1.4", 1, Ct));
        Assert.Equal(2, await repository.AllocateNextAsync(1, "1.4", Ct));
        // The same number in another series is free.
        Assert.Equal(SetNextReleaseNumberOutcome.Set, await repository.SetNextAsync(1, "1.5", 1, Ct));
    }

    [Fact]
    public async Task ACounterSetBelowNumbersAlreadyHandedOut_SkipsThemInsteadOfGivingThemTwice()
    {
        await SeedAsync();
        var repository = Repository();
        for (var index = 0; index < 4; index++) await repository.AllocateNextAsync(1, "1.4", Ct); // 0..3

        Assert.Equal(SetNextReleaseNumberOutcome.Set, await repository.SetNextAsync(1, "1.4", 9, Ct));
        Assert.Equal(9, await repository.AllocateNextAsync(1, "1.4", Ct));
        Assert.Equal(SetNextReleaseNumberOutcome.Set, await repository.SetNextAsync(1, "1.4", 6, Ct));
        Assert.Equal(6, await repository.AllocateNextAsync(1, "1.4", Ct));
        Assert.Equal(7, await repository.AllocateNextAsync(1, "1.4", Ct));
        Assert.Equal(8, await repository.AllocateNextAsync(1, "1.4", Ct));
        // 9 was handed out above: the counter jumps over it.
        Assert.Equal(10, await repository.AllocateNextAsync(1, "1.4", Ct));
    }

    [Fact]
    public async Task TheSeriesOfAProject_AreListedNewestFirstAndNumerically()
    {
        await SeedAsync();
        var repository = Repository();
        await repository.AllocateNextAsync(1, "1.9", Ct);
        await repository.AllocateNextAsync(1, "1.10", Ct);
        await repository.AllocateNextAsync(1, "1.10", Ct);
        await repository.AllocateNextAsync(1, "2.0", Ct);

        var series = await repository.GetSeriesAsync(1, Ct);

        Assert.Equal(["2.0", "1.10", "1.9"], series!.Select(item => item.Series));
        Assert.Equal([1, 2, 1], series!.Select(item => item.NextNumber));
        Assert.Null(await repository.GetSeriesAsync(404, Ct));
        Assert.Equal(SetNextReleaseNumberOutcome.ProjectNotFound, await repository.SetNextAsync(404, "1.4", 0, Ct));
    }

    [Fact]
    public async Task TheFacade_MovesTheSameCounterAsThePort()
    {
        await SeedAsync();
        IPipelineReleaseNumberRepository port = Repository();
        IPipelineReleaseNumberRepository facade = Facade();

        Assert.Equal(0, await port.AllocateNextReleaseNumberAsync(1, "1.4", Ct));
        Assert.Equal(1, await facade.AllocateNextReleaseNumberAsync(1, "1.4", Ct));
        Assert.Equal(await port.GetLatestReleaseNumberAsync(1, "1.4", Ct), await facade.GetLatestReleaseNumberAsync(1, "1.4", Ct));
        Assert.Equal(await port.GetRunReleaseAsync(10, Ct), await facade.GetRunReleaseAsync(10, Ct));
        Assert.Equal(await port.AllocateNextReleaseNumberAsync(404, "1.4", Ct), await facade.AllocateNextReleaseNumberAsync(404, "1.4", Ct));
    }

    public void Dispose() => _db.Dispose();
}
