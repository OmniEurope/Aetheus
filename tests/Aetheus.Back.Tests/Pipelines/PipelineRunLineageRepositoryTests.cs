// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The lineage tile lists the runs launched after a run, not its own stages.</summary>
public sealed class PipelineRunLineageRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly DateTime _started = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    public PipelineRunLineageRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task R2_026_TheRunsOfItsOwnTriggerSteps_AreLeftOut_TheFollowUpsStay()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeline = new Pipeline { Id = 1, Name = "aetheus-candidate" };
        _db.Pipelines.Add(pipeline);
        string Marker(int id) => JsonSerializer.Serialize(new Dictionary<string, string> { ["UPSTREAM_RUN_ID"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 10, PipelineId = 1, Status = PipelineStatus.Success, StartedAt = _started, BuildNumber = 1 },
            // The CI stage of run 10 (a trigger step): already in its timeline.
            new PipelineRun { Id = 11, PipelineId = 1, Status = PipelineStatus.Success, StartedAt = _started.AddMinutes(1), BuildNumber = 2, AdditionalVariablesJson = Marker(10) },
            // The delivery launched once run 10 succeeded (on_success): a follow-up.
            new PipelineRun { Id = 12, PipelineId = 1, Status = PipelineStatus.Running, StartedAt = _started.AddMinutes(20), BuildNumber = 3, AdditionalVariablesJson = Marker(10) });
        _db.PipelineStepRuns.Add(new PipelineStepRun
        {
            Id = 1,
            PipelineRunId = 10,
            StageName = "CI",
            StepName = "trigger-ci",
            Status = TaskExecutionStatus.Success,
            TriggeredRunId = 11
        });
        await _db.SaveChangesAsync(ct);

        var downstream = await new PipelineRunLineageRepository(_db)
            .GetDownstreamRunsAsync(10, _started, _started.AddHours(1), ct);

        var link = Assert.Single(downstream);
        Assert.Equal((12, 3, PipelineStatus.Running), (link.RunId, link.BuildNumber, link.Status));
    }
}
