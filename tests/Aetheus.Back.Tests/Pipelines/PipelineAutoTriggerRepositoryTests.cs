// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The automatic-trigger port (AUD-07, suite) answers what the webhook, Git push and schedule
/// triggers read, and the IPipelineRepository facade, which delegates to it, answers the same.</summary>
public sealed class PipelineAutoTriggerRepositoryTests : IDisposable
{
    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IPipelineRepository Facade() => new PipelineRepository(
        _db, TimeProvider.System, NullLogger<PipelineRepository>.Instance,
        new PipelineTaskLifecycleRepository(_db, TimeProvider.System), new PipelineRunLineageRepository(_db));

    private async Task SeedAsync()
    {
        _db.Projects.Add(new Project { Id = 1, Name = "app" });
        _db.Pipelines.AddRange(
            new Pipeline { Id = 1, Name = "hook", ProjectId = 1, TriggerType = PipelineTriggerType.Webhook },
            new Pipeline { Id = 2, Name = "hook-orphan", TriggerType = PipelineTriggerType.Webhook },
            new Pipeline { Id = 3, Name = "nightly", TriggerType = PipelineTriggerType.Schedule },
            new Pipeline { Id = 4, Name = "manual", ProjectId = 1, TriggerType = PipelineTriggerType.Manual });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 10, PipelineId = 1, Status = PipelineStatus.Running },
            new PipelineRun { Id = 11, PipelineId = 1, Status = PipelineStatus.WaitingForApproval },
            new PipelineRun { Id = 12, PipelineId = 1, Status = PipelineStatus.Success },
            new PipelineRun { Id = 13, PipelineId = 3, Status = PipelineStatus.Pending },
            new PipelineRun { Id = 14, PipelineId = 4, Status = PipelineStatus.Failed });
        await _db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task TheTriggerQueries_ReturnTheTriggeredPipelinesAndTheirActiveRuns()
    {
        await SeedAsync();
        IPipelineAutoTriggerRepository port = new PipelineAutoTriggerRepository(_db);

        Assert.Equal([1, 2], (await port.GetWebhookTriggeredPipelinesWithProjectAsync(Ct)).Select(p => p.Id).Order());
        Assert.Equal([1], (await port.GetWebhookTriggeredPipelinesForProjectAsync(1, Ct)).Select(p => p.Id));
        Assert.Equal([3], (await port.GetScheduledPipelinesAsync(Ct)).Select(p => p.Id));
        Assert.True(await port.HasActiveRunAsync(1, Ct));
        Assert.False(await port.HasActiveRunAsync(4, Ct));
        Assert.True(await port.LockPipelineForWebhookAsync(1, Ct));
        Assert.False(await port.LockPipelineForWebhookAsync(404, Ct));
        Assert.Equal([10, 11], await port.GetActiveRunIdsAsync(1, Ct));
        Assert.Equal(new HashSet<int> { 1, 3 }, await port.GetPipelineIdsWithActiveRunsAsync(Ct));
    }

    [Fact]
    public async Task TheFacade_AnswersExactlyWhatThePortAnswers()
    {
        await SeedAsync();
        IPipelineAutoTriggerRepository port = new PipelineAutoTriggerRepository(_db);
        IPipelineAutoTriggerRepository facade = Facade();

        Assert.Equal(
            (await port.GetWebhookTriggeredPipelinesWithProjectAsync(Ct)).Select(p => p.Id).Order(),
            (await facade.GetWebhookTriggeredPipelinesWithProjectAsync(Ct)).Select(p => p.Id).Order());
        Assert.Equal(
            (await port.GetWebhookTriggeredPipelinesForProjectAsync(1, Ct)).Select(p => p.Id),
            (await facade.GetWebhookTriggeredPipelinesForProjectAsync(1, Ct)).Select(p => p.Id));
        Assert.Equal(
            (await port.GetScheduledPipelinesAsync(Ct)).Select(p => p.Id),
            (await facade.GetScheduledPipelinesAsync(Ct)).Select(p => p.Id));
        Assert.Equal(await port.HasActiveRunAsync(1, Ct), await facade.HasActiveRunAsync(1, Ct));
        Assert.Equal(await port.LockPipelineForWebhookAsync(404, Ct), await facade.LockPipelineForWebhookAsync(404, Ct));
        Assert.Equal(await port.GetActiveRunIdsAsync(1, Ct), await facade.GetActiveRunIdsAsync(1, Ct));
        Assert.Equal(await port.GetPipelineIdsWithActiveRunsAsync(Ct), await facade.GetPipelineIdsWithActiveRunsAsync(Ct));
    }

    public void Dispose() => _db.Dispose();
}
