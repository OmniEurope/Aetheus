// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The run-lookup port PipelineHub authorizes through (AUD-07): it returns the owning
/// pipeline of the requested run only, and null for a run that does not exist.</summary>
public sealed class PipelineRunLookupRepositoryTests : IDisposable
{
    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetPipelineIdForRunAsync_ReturnsTheOwningPipelineOfThatRun()
    {
        _db.Pipelines.AddRange(new Pipeline { Id = 3, Name = "build" }, new Pipeline { Id = 4, Name = "deploy" });
        _db.PipelineRuns.AddRange(new PipelineRun { Id = 9, PipelineId = 3 }, new PipelineRun { Id = 10, PipelineId = 4 });
        await _db.SaveChangesAsync(Ct);
        IPipelineRunLookupReader reader = new PipelineRunLookupRepository(_db);

        Assert.Equal(4, await reader.GetPipelineIdForRunAsync(10, Ct));
        Assert.Equal(3, await reader.GetPipelineIdForRunAsync(9, Ct));
    }

    [Fact]
    public async Task GetPipelineIdForRunAsync_UnknownRun_ReturnsNull()
    {
        _db.Pipelines.Add(new Pipeline { Id = 3, Name = "build" });
        _db.PipelineRuns.Add(new PipelineRun { Id = 9, PipelineId = 3 });
        await _db.SaveChangesAsync(Ct);

        Assert.Null(await new PipelineRunLookupRepository(_db).GetPipelineIdForRunAsync(404, Ct));
    }

    public void Dispose() => _db.Dispose();
}
