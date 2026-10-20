// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The template port (AUD-07, suite) flushes the edits its consumer makes to a tracked
/// template: the template service saves an update through it, not through IPipelineRepository.</summary>
public sealed class PipelineTemplateRepositoryPortTests : IDisposable
{
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly AppDbContext _db;

    public PipelineTemplateRepositoryPortTests() => _db = NewContext();

    private AppDbContext NewContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SaveChangesAsync_PersistsAnEditMadeOnATemplateFoundThroughThePort()
    {
        IPipelineTemplateRepository port = new PipelineTemplateRepository(_db);
        await port.AddTemplateAsync(new PipelineTemplate { Name = "build", Description = "before" }, Ct);
        var id = (await port.FindTemplateByNameAsync("BUILD", Ct))!.Id;

        var tracked = await port.FindTemplateAsync(id, Ct);
        tracked!.Description = "after";
        await port.SaveChangesAsync(Ct);

        await using var reader = NewContext();
        Assert.Equal("after", (await new PipelineTemplateRepository(reader).GetTemplateAsync(id, Ct))!.Description);
    }

    public void Dispose() => _db.Dispose();
}
