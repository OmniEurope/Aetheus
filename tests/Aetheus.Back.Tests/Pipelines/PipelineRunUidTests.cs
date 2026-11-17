// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>ADR-060: the identifier is written where every EF save goes, so a creation path added later
/// gets it without knowing about it. The PostgreSQL side was a migration test removed once the migration shipped (docs/audit/tests-integration-e2e/migrations-livrees.md).</summary>
public sealed class PipelineRunUidTests
{
    private static readonly DateTime Started = new(2026, 11, 1, 8, 30, 15, 250, DateTimeKind.Utc);

    private static AppDbContext NewContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static long UnixMilliseconds(Guid uuidV7) =>
        Convert.ToInt64(uuidV7.ToString("N")[..12], 16);

    [Fact]
    public async Task ARunSavedAsynchronously_GetsAVersion7Uid_FromItsStart()
    {
        await using var db = NewContext();
        var run = new PipelineRun { PipelineId = 1, StartedAt = Started };

        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, run.Uid!.Value.Version);
        Assert.Equal(new DateTimeOffset(Started).ToUnixTimeMilliseconds(), UnixMilliseconds(run.Uid.Value));
    }

    [Fact]
    public void ARunSavedSynchronously_GetsItsUidToo()
    {
        using var db = NewContext();
        var run = new PipelineRun { PipelineId = 1, StartedAt = Started };

        db.PipelineRuns.Add(run);
        db.SaveChanges();

        Assert.Equal(7, run.Uid!.Value.Version);
    }

    [Fact]
    public async Task ARunReachedThroughItsPipeline_GetsItsUidToo()
    {
        await using var db = NewContext();
        var run = new PipelineRun { StartedAt = Started };
        var pipeline = new Pipeline { Name = "graph", YamlDefinition = "name: graph", Runs = [run] };

        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, run.Uid!.Value.Version);
    }

    [Fact]
    public async Task AUidAlreadyThere_IsKept()
    {
        await using var db = NewContext();
        var uid = Guid.CreateVersion7();
        var run = new PipelineRun { PipelineId = 1, StartedAt = Started, Uid = uid };

        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(uid, run.Uid);
    }

    [Fact]
    public async Task ASavedRun_KeepsItsUidWhenItChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = NewContext();
        var run = new PipelineRun { PipelineId = 1, StartedAt = Started };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var uid = run.Uid;

        run.Status = PipelineStatus.Success;
        await db.SaveChangesAsync(ct);

        Assert.Equal(uid, run.Uid);
    }

    [Fact]
    public void AStartBefore1970_TakesTheFirstInstantAVersion7CanHold()
    {
        var uid = PipelineRunUid.From(default);

        Assert.Equal(7, uid.Version);
        Assert.Equal(0, UnixMilliseconds(uid));
    }

    [Fact]
    public void TheRunDtos_CarryTheUid()
    {
        var uid = Guid.CreateVersion7();
        var run = new PipelineRun { Id = 3, PipelineId = 1, StartedAt = Started, Uid = uid };

        Assert.Equal(uid, PipelineRunDtoMapper.MapRunToDto(run).Uid);
        Assert.Equal(uid, PipelineRunDtoMapper.RunListProjection.Compile()(run).Uid);
    }
}
