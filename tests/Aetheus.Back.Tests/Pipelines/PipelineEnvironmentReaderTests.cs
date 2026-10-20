// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The environment port (AUD-07, suite) answers what the environment lock gate and the
/// environment-check guard read, and the IPipelineRepository facade, which delegates to it, answers the
/// same.</summary>
public sealed class PipelineEnvironmentReaderTests : IDisposable
{
    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IPipelineRepository Facade() => new PipelineRepository(
        _db, TimeProvider.System, NullLogger<PipelineRepository>.Instance,
        new PipelineTaskLifecycleRepository(_db, TimeProvider.System), new PipelineRunLineageRepository(_db));

    private async Task SeedAsync()
    {
        _db.Projects.AddRange(new Project { Id = 1, Name = "app" }, new Project { Id = 2, Name = "other" });
        _db.Environments.AddRange(
            new Data.Entities.Environment { Id = 10, Name = "prod", ProjectId = 1 },
            new Data.Entities.Environment { Id = 11, Name = "prod", ProjectId = 2 },
            new Data.Entities.Environment { Id = 12, Name = "prod" },
            new Data.Entities.Environment { Id = 13, Name = "qa" });
        _db.EnvironmentChecks.AddRange(
            new EnvironmentCheck { Id = 100, EnvironmentId = 10, Name = "smoke" },
            new EnvironmentCheck { Id = 101, EnvironmentId = 10, Name = "health" },
            new EnvironmentCheck { Id = 102, EnvironmentId = 11, Name = "other" });
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task TheEnvironmentQueries_ReadTheProjectsOwnEnvironmentFirstAndItsChecksByName()
    {
        await SeedAsync();
        IPipelineEnvironmentReader port = new PipelineEnvironmentRepository(_db);

        var own = await port.FindEnvironmentByNameAsync("prod", 1, Ct);
        Assert.Equal(10, own?.Id);
        Assert.Equal(["health", "smoke"], own!.Checks.Select(check => check.Name).Order());
        Assert.Equal(12, (await port.FindEnvironmentByNameAsync("prod", 3, Ct))?.Id);
        Assert.Equal(12, (await port.FindEnvironmentByNameAsync("prod", null, Ct))?.Id);
        Assert.Equal(13, (await port.FindEnvironmentByNameAsync("qa", 1, Ct))?.Id);
        Assert.Null(await port.FindEnvironmentByNameAsync("staging", 1, Ct));

        Assert.Equal(["health", "smoke"], (await port.GetEnvironmentChecksAsync(10, Ct)).Select(check => check.Name));
        Assert.Empty(await port.GetEnvironmentChecksAsync(13, Ct));
    }

    [Fact]
    public async Task TheFacade_AnswersExactlyWhatThePortAnswers()
    {
        await SeedAsync();
        IPipelineEnvironmentReader port = new PipelineEnvironmentRepository(_db);
        IPipelineEnvironmentReader facade = Facade();

        foreach (var (name, projectId) in new (string, int?)[] { ("prod", 1), ("prod", 2), ("prod", null), ("qa", 1), ("staging", 1) })
        {
            Assert.Equal(
                (await port.FindEnvironmentByNameAsync(name, projectId, Ct))?.Id,
                (await facade.FindEnvironmentByNameAsync(name, projectId, Ct))?.Id);
        }
        Assert.Equal(
            (await port.GetEnvironmentChecksAsync(10, Ct)).Select(check => check.Id),
            (await facade.GetEnvironmentChecksAsync(10, Ct)).Select(check => check.Id));
    }

    public void Dispose() => _db.Dispose();
}
