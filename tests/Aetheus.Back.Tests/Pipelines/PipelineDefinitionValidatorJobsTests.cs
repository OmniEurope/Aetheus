// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>A stage written with jobs is validated job by job: a name, at least one step, a known os,
/// and a warning when neither the job nor its stage says where it runs.</summary>
public sealed class PipelineDefinitionValidatorJobsTests
{
    private static readonly PipelineStepDefinition Shell = new() { Name = "build", Shell = "dotnet build" };

    [Fact]
    public void ACompleteJob_IsValidWithoutWarning()
    {
        var (errors, warnings) = Validate(new PipelineStageDefinition
        {
            Name = "Build",
            Jobs = [new PipelineJobDefinition { Name = "compile", Agent = "runner-1", Os = "linux", Steps = [Shell] }],
        });

        Assert.Empty(errors);
        Assert.Empty(warnings);
    }

    [Fact]
    public void AJobWithoutNameOrStep_IsInvalid()
    {
        var (errors, _) = Validate(new PipelineStageDefinition
        {
            Name = "Build",
            Agent = "runner-1",
            Jobs = [new PipelineJobDefinition { Name = " " }],
        });

        Assert.Contains("Job name cannot be empty in stage 'Build'.", errors);
        Assert.Contains("Job ' ' in stage 'Build' must have at least one step.", errors);
    }

    [Fact]
    public void AJobWithNoPlacement_WarnsItUsesRunAffinity()
    {
        var (_, warnings) = Validate(new PipelineStageDefinition
        {
            Name = "Build",
            Jobs = [new PipelineJobDefinition { Name = "compile", Steps = [Shell] }],
        });

        Assert.Contains("Job 'compile' in stage 'Build' has no agent/pool/environment - will use run affinity.", warnings);
    }

    [Theory]
    [InlineData("runner-1", null, null, null, null, null)]
    [InlineData(null, "pool-a", null, null, null, null)]
    [InlineData(null, null, "staging", null, null, null)]
    [InlineData(null, null, null, "runner-1", null, null)]
    [InlineData(null, null, null, null, "pool-a", null)]
    [InlineData(null, null, null, null, null, "staging")]
    public void APlacementOnTheJobOrItsStage_NeedsNoAffinityWarning(
        string? jobAgent, string? jobPool, string? jobEnvironment, string? stageAgent, string? stagePool, string? stageEnvironment)
    {
        var (_, warnings) = Validate(new PipelineStageDefinition
        {
            Name = "Build",
            Agent = stageAgent ?? string.Empty,
            Pool = stagePool,
            Environment = stageEnvironment,
            Jobs = [new PipelineJobDefinition { Name = "compile", Agent = jobAgent ?? string.Empty, Pool = jobPool, Environment = jobEnvironment, Steps = [Shell] }],
        });

        Assert.DoesNotContain(warnings, warning => warning.Contains("run affinity", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnrecognizedJobOs_IsIgnoredWithAWarning()
    {
        var (_, warnings) = Validate(new PipelineStageDefinition
        {
            Name = "Build",
            Jobs = [new PipelineJobDefinition { Name = "compile", Agent = "runner-1", Os = "beos", Steps = [Shell] }],
        });

        Assert.Contains(warnings, warning => warning.StartsWith("Job 'compile' in stage 'Build' has an unrecognized os 'beos'", StringComparison.Ordinal));
    }

    [Fact]
    public void TheStepsOfAJob_AreValidatedWithTheJobInTheirContext()
    {
        var (errors, _) = Validate(new PipelineStageDefinition
        {
            Name = "Build",
            Jobs = [new PipelineJobDefinition { Name = "compile", Agent = "runner-1", Steps = [new PipelineStepDefinition { Name = "empty" }] }],
        });

        Assert.Contains("Step 'empty' in stage 'Build' / job 'compile' must have a shell command or checkout enabled.", errors);
    }

    private static (List<string> Errors, List<string> Warnings) Validate(PipelineStageDefinition stage)
    {
        List<string> errors = [];
        List<string> warnings = [];
        PipelineDefinitionValidator.ValidateStages(new PipelineYamlDefinition { Name = "ci", Stages = [stage] }, errors, warnings);
        return (errors, warnings);
    }
}
