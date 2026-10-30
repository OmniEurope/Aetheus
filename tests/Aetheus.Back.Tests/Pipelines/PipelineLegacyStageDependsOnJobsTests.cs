// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>A legacy stage (with <c>steps</c>) that waits for a stage written with <c>jobs</c> waits for
/// every job of it: only the jobs carry step runs, so the parent name would never be satisfied.</summary>
public sealed class PipelineLegacyStageDependsOnJobsTests
{
    private const string Yaml = """
        name: ci
        stages:
          - name: Build
            agent: runner-1
            jobs:
              - name: compile
                steps:
                  - name: compile
                    shell: dotnet build
              - name: lint
                steps:
                  - name: lint
                    shell: dotnet format --verify-no-changes
          - name: Publish
            agent: runner-1
            depends_on:
              - Build
            steps:
              - name: publish
                shell: dotnet publish
        """;

    [Fact]
    public void ALegacyStage_DependingOnAJobsStage_WaitsForEveryJob()
    {
        var definition = YamlParsingHelper.ParseAndValidate(Yaml)!;

        var publish = YamlParsingHelper.FlattenJobs(definition).Single(stage => stage.Name == "Publish");

        Assert.Equal(["compile", "lint"], publish.DependsOn);
        Assert.Single(publish.Steps);
    }

    [Fact]
    public void ALegacyStage_DependingOnALegacyStage_KeepsItsDependencyAsWritten()
    {
        var definition = YamlParsingHelper.ParseAndValidate("""
            name: ci
            stages:
              - name: Build
                agent: runner-1
                steps:
                  - name: compile
                    shell: dotnet build
              - name: Publish
                agent: runner-1
                depends_on: [Build, Unrelated]
                steps:
                  - name: publish
                    shell: dotnet publish
            """)!;

        var publish = YamlParsingHelper.FlattenJobs(definition).Single(stage => stage.Name == "Publish");

        Assert.Equal(["Build", "Unrelated"], publish.DependsOn);
    }

    [Fact]
    public void ALegacyStage_DependingOnALegacyStageWithAnotherCase_IsNotCorrectedSilently()
    {
        var definition = YamlParsingHelper.ParseAndValidate("""
            name: ci
            stages:
              - name: Build
                agent: runner-1
                steps: [{ name: compile, shell: dotnet build }]
              - name: Publish
                agent: runner-1
                depends_on: [build]
                steps: [{ name: publish, shell: dotnet publish }]
            """)!;

        var publish = YamlParsingHelper.FlattenJobs(definition).Single(stage => stage.Name == "Publish");

        Assert.Equal(["build"], publish.DependsOn);
    }

    [Fact]
    public void TheValidator_AcceptsALegacyStageDependingOnAJobsStage()
    {
        var definition = YamlParsingHelper.ParseAndValidate(Yaml)!;
        var errors = new List<string>();

        PipelineDefinitionValidator.ValidateStages(definition, errors, []);

        Assert.Empty(errors);
    }

    [Fact]
    public void TheStageIsDispatched_OnlyOnceEveryJobHasCompleted()
    {
        var definition = YamlParsingHelper.ParseAndValidate(Yaml)!;
        var pending = new List<PipelineStepRun> { new() { StageName = "Publish" } };

        Assert.Empty(FindReadyStages(pending, definition, [PipelineRunHelpers.SystemPrepareStage]));
        Assert.Empty(FindReadyStages(pending, definition, [PipelineRunHelpers.SystemPrepareStage, "compile"]));
        Assert.Equal(
            ["Publish"],
            FindReadyStages(pending, definition, [PipelineRunHelpers.SystemPrepareStage, "compile", "lint"]));
    }

    private static List<string> FindReadyStages(
        List<PipelineStepRun> pendingSteps, PipelineYamlDefinition definition, List<string> completedStages)
    {
        var method = typeof(PipelineStageDispatchPlanner).GetMethod(
            "FindReadyStages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (List<string>)method.Invoke(null, [pendingSteps, definition, completedStages, completedStages])!;
    }
}
