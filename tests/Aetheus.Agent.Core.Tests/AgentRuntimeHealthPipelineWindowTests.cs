// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Agent.Core.Tests;

/// <summary>R-33: the agent's own record of when its pipeline tasks ran, the evidence that attributes a
/// Testcontainers reaper to a run and bounds that attribution to the run's lifetime.</summary>
public sealed class AgentRuntimeHealthPipelineWindowTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 20, 20, 0, TimeSpan.Zero));

    [Fact]
    public void NoPipelineTask_AttributesNothing()
    {
        var health = new AgentRuntimeHealth(_time);

        Assert.False(health.CreatedDuringLivePipelineTask(_time.GetUtcNow()));
    }

    [Fact]
    public void ContainerCreatedWhileATaskRuns_IsAttributed_ButNotOneCreatedBeforeTheTask()
    {
        var health = new AgentRuntimeHealth(_time);
        var beforeTask = _time.GetUtcNow().AddSeconds(-1);
        using var task = health.TrackPipelineTask();
        _time.Advance(TimeSpan.FromMinutes(6));

        Assert.True(health.CreatedDuringLivePipelineTask(_time.GetUtcNow().AddMinutes(-1)));
        Assert.False(health.CreatedDuringLivePipelineTask(beforeTask));
    }

    [Fact]
    public void AttributionEndsShortlyAfterTheTask_AndNeverCoversAContainerCreatedAfterIt()
    {
        var health = new AgentRuntimeHealth(_time);
        var task = health.TrackPipelineTask();
        _time.Advance(TimeSpan.FromMinutes(5));
        var createdDuringTask = _time.GetUtcNow().AddMinutes(-1);
        task.Dispose();
        var createdAfterTask = _time.GetUtcNow().AddSeconds(5);

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(health.CreatedDuringLivePipelineTask(createdDuringTask));
        Assert.False(health.CreatedDuringLivePipelineTask(createdAfterTask));

        _time.Advance(AgentRuntimeDefaults.PipelineContainerGrace);
        Assert.False(health.CreatedDuringLivePipelineTask(createdDuringTask));
    }
}
