// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The per-pipeline build counter behind <c>BUILD_PIPELINE_RUNNUMBER</c>.
///
/// None of this is observable on InMemory: it has no row locks, no <c>ExecuteUpdateAsync</c> and no
/// real transactions, so the unit suite exercises a separate branch entirely. The two contracts that
/// matter only exist against a relational provider - the reservation joins an ambient transaction
/// instead of opening a nested one (which throws on the same connection), and two contenders never
/// receive the same number.
/// </summary>
public sealed class PipelineBuildNumberRepositoryIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    /// <summary>
    /// The webhook path already runs inside <c>IDbTransactionScope.ExecuteInTransactionAsync</c>. A
    /// second <c>BeginTransactionAsync</c> on the same connection throws, so a reservation that did
    /// not detect the ambient transaction failed the launch outright.
    /// </summary>
    [Fact]
    public async Task ReserveNext_InsideAnAmbientTransaction_JoinsItInsteadOfOpeningANestedOne()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var pipelineId = await SeedPipelineAsync(db);

        await using var ambient = await db.Database
            .BeginTransactionAsync(TestContext.Current.CancellationToken);
        var sut = new PipelineBuildNumberRepository(db);

        var first = await sut.ReserveNextAsync(pipelineId, TestContext.Current.CancellationToken);
        var second = await sut.ReserveNextAsync(pipelineId, TestContext.Current.CancellationToken);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        // The caller still owns the transaction: nothing below committed it on their behalf.
        Assert.NotNull(db.Database.CurrentTransaction);

        await ambient.RollbackAsync(TestContext.Current.CancellationToken);
        await using var verify = NewContext();
        var persisted = await verify.Pipelines
            .Where(pipeline => pipeline.Id == pipelineId)
            .Select(pipeline => pipeline.BuildCounter)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, persisted);
    }

    // Two launches of the same pipeline must never carry the same build number: the UPDATE takes the
    // row lock, so the second contender blocks on it rather than reading the same value.
    [Fact]
    public async Task ConcurrentReservations_NeverHandOutTheSameNumber()
    {
        await ResetAndMigrateAsync();
        await using var seed = NewContext();
        var pipelineId = await SeedPipelineAsync(seed);

        var reservations = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = NewContext();
            return await new PipelineBuildNumberRepository(db)
                .ReserveNextAsync(pipelineId, TestContext.Current.CancellationToken);
        }));

        Assert.Equal(Enumerable.Range(1, 8), reservations.Order());
    }

    // R-8, the whole path of a launch: reserve then insert, in parallel, on the real schema. Each run keeps
    // the number it reserved and the numbers count from 1 within each pipeline, whatever the global key.
    [Fact]
    public async Task ConcurrentLaunches_OfTwoPipelines_EachRunTakesItsOwnNumberWithinItsPipeline()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        await using var seed = NewContext();
        var first = await SeedPipelineAsync(seed);
        var second = await SeedPipelineAsync(seed);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(async index =>
        {
            await using var db = NewContext();
            var pipelineId = index % 2 == 0 ? first : second;
            var number = await new PipelineBuildNumberRepository(db).ReserveNextAsync(pipelineId, ct);
            db.PipelineRuns.Add(new PipelineRun { PipelineId = pipelineId, BuildNumber = number, StartedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
        }));

        await using var verify = NewContext();
        foreach (var pipelineId in new[] { first, second })
        {
            var numbers = await verify.PipelineRuns.AsNoTracking()
                .Where(run => run.PipelineId == pipelineId)
                .Select(run => run.BuildNumber)
                .ToListAsync(ct);
            Assert.Equal(Enumerable.Range(1, 8), numbers.Order());
        }
    }

    // R-8: the counter hands a number out once, and the database refuses a second holder whatever wrote it.
    [Fact]
    public async Task TwoRunsOfOnePipeline_CannotHoldTheSameNumber_ButTwoPipelinesCan()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        await using var db = NewContext();
        var first = await SeedPipelineAsync(db);
        var second = await SeedPipelineAsync(db);
        db.PipelineRuns.Add(new PipelineRun { PipelineId = first, BuildNumber = 12, StartedAt = DateTime.UtcNow });
        db.PipelineRuns.Add(new PipelineRun { PipelineId = second, BuildNumber = 12, StartedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);

        await using var duplicate = NewContext();
        duplicate.PipelineRuns.Add(new PipelineRun { PipelineId = first, BuildNumber = 12, StartedAt = DateTime.UtcNow });

        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync(ct));
        Assert.Contains("IX_PipelineRuns_PipelineId_BuildNumber", refused.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReserveNext_ForAPipelineThatNoLongerExists_ReturnsZero()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();

        Assert.Equal(0, await new PipelineBuildNumberRepository(db)
            .ReserveNextAsync(999_999, TestContext.Current.CancellationToken));
    }

    private static async Task<int> SeedPipelineAsync(AppDbContext db)
    {
        var pipeline = new Pipeline { Name = $"counter-{Guid.NewGuid():N}" };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return pipeline.Id;
    }
}
