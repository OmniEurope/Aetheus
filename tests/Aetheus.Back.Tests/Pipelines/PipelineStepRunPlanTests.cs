// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The step-run rows a launched run starts with: system prepare/cleanup around the workspace steps,
/// one row per matrix leg, contiguous order, and the "run to end" forcing kept off declared steps.
/// </summary>
public sealed class PipelineStepRunPlanTests
{
    private static PipelineRunPreparation Prepare(string? repositoryUrl, params PipelineStageDefinition[] stages)
        => new()
        {
            PipelineId = 1,
            YamlSnapshot = "name: ci",
            TargetServerIds = [],
            RepositoryUrl = repositoryUrl,
            Definition = new PipelineYamlDefinition { Name = "ci", Stages = [.. stages] },
            EffectiveStages = [.. stages]
        };

    [Fact]
    public void Build_WorkspacePipelineInRunToEnd_WrapsMatrixLegsBetweenCloneAndCleanup_AndForcesOnlyUndeclaredSteps()
    {
        var stage = new PipelineStageDefinition
        {
            Name = "Build",
            Group = "ci",
            Matrix = new Dictionary<string, List<string>> { ["os"] = ["linux", "windows"] },
            Steps =
            [
                new PipelineStepDefinition { Name = "compile", RetryCount = 2 },
                new PipelineStepDefinition { Name = "lint", ContinueOnError = true }
            ]
        };
        var run = new PipelineRun { Id = 7, RunToEnd = true };

        var rows = PipelineStepRunPlan.Build(run, Prepare("https://git.example/repo.git", stage));

        Assert.Equal(
            ["Clone Repository", "compile [linux]", "lint [linux]", "compile [windows]", "lint [windows]", "Cleanup"],
            rows.Select(r => r.StepName));
        Assert.Equal(Enumerable.Range(0, rows.Count), rows.Select(r => r.Order));
        Assert.All(rows, r => Assert.Equal(7, r.PipelineRunId));
        Assert.Equal(PipelineRunService.SystemPrepareStage, rows[0].StageName);
        Assert.True(rows[0].IsSystem);
        Assert.Equal(PipelineRunService.SystemCleanupStage, rows[^1].StageName);
        Assert.True(rows[^1].IsSystem);
        Assert.True(rows[^1].ContinueOnError);

        var compile = rows[1];
        Assert.Equal("Build", compile.StageName);
        Assert.Equal("linux", compile.MatrixLeg);
        Assert.Equal("ci", compile.GroupName);
        Assert.Equal(2, compile.RetryCount);
        Assert.True(compile.ContinueOnError);
        Assert.True(compile.ContinueOnErrorForced);

        var lint = rows[2];
        Assert.True(lint.ContinueOnError);
        Assert.False(lint.ContinueOnErrorForced);
    }

    [Fact]
    public void Build_WithoutRepository_PreparesTheWorkspace_AndLeavesStepsUnforcedOutsideRunToEnd()
    {
        var stage = new PipelineStageDefinition { Name = "Build", Steps = [new PipelineStepDefinition { Name = "compile" }] };

        var rows = PipelineStepRunPlan.Build(new PipelineRun { Id = 3 }, Prepare(null, stage));

        Assert.Equal(["Prepare Workspace", "compile", "Cleanup"], rows.Select(r => r.StepName));
        Assert.Null(rows[1].MatrixLeg);
        Assert.False(rows[1].ContinueOnError);
        Assert.False(rows[1].ContinueOnErrorForced);
    }

    [Fact]
    public void Build_TriggerOnlyPipeline_HasNoSystemSteps()
    {
        var stage = new PipelineStageDefinition
        {
            Name = "Orchestrate",
            Steps = [new PipelineStepDefinition { Name = "child", Type = "trigger" }]
        };

        var rows = PipelineStepRunPlan.Build(new PipelineRun { Id = 4 }, Prepare("https://git.example/repo.git", stage));

        var only = Assert.Single(rows);
        Assert.Equal("child", only.StepName);
        Assert.Equal(0, only.Order);
        Assert.False(only.IsSystem);
    }
}
