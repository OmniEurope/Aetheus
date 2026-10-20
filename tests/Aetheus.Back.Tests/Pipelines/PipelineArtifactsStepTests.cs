// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Step artifact names share one namespace with stage bundles.</summary>
public sealed class PipelineArtifactsStepTests
{
    private static List<string> Errors(string yaml)
    {
        var definition = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(yaml);
        var errors = new List<string>();
        PipelineDefinitionValidator.ValidateStages(definition, errors, []);
        return errors;
    }

    [Fact]
    public void AStageOfArtifactsSteps_IsValid()
    {
        Assert.Empty(Errors("""
            name: ci
            stages:
              - name: Publish artifacts
                agent: linux-01
                steps:
                  - name: Payload
                    type: artifacts
                    artifact_name: BuildPayloads-artifacts
                    target_files: ["out/payload/**"]
                  - name: Runtime
                    type: artifacts
                    artifact_name: QaRuntime-artifacts
                    target_files: ["out/qa/**"]
            """));
    }

    public static TheoryData<PipelineStepDefinition, string> Incomplete => new()
    {
        { new PipelineStepDefinition { Name = "s", Type = "artifacts", TargetFiles = ["x/**"] }, "requires artifact_name" },
        { new PipelineStepDefinition { Name = "s", Type = "artifacts", ArtifactName = "A" }, "requires target_files" },
        { new PipelineStepDefinition { Name = "s", Shell = "echo", ArtifactName = "A" }, "only valid on an artifacts step" }
    };

    [Theory]
    [MemberData(nameof(Incomplete))]
    public void AnIncompleteOrMisplacedDeclaration_IsRefused(PipelineStepDefinition step, string expected)
    {
        var definition = new PipelineYamlDefinition
        {
            Name = "ci",
            Stages = [new PipelineStageDefinition { Name = "S", Agent = "linux-01", Steps = [step] }]
        };
        var errors = new List<string>();
        PipelineDefinitionValidator.ValidateStages(definition, errors, []);

        Assert.Contains(errors, error => error.Contains(expected, StringComparison.Ordinal));
    }

    /// <summary>The collector reads the agent's workspace, not the files inside the step containers.</summary>
    [Fact]
    public void AContainerIsolatedStage_CannotCollectPerStep()
    {
        var errors = Errors("""
            name: ci
            stages:
              - name: S
                agent: linux-01
                isolation:
                  mode: container
                  image: mcr.microsoft.com/dotnet/sdk:10.0
                steps:
                  - name: step
                    type: artifacts
                    artifact_name: A
                    target_files: ["x/**"]
            """);

        Assert.Contains(errors, error => error.Contains("container-isolated", StringComparison.Ordinal));
    }

    [Fact]
    public void AMatrixStage_CannotCollectPerStep()
    {
        var errors = Errors("""
            name: ci
            stages:
              - name: S
                agent: linux-01
                matrix:
                  os: [linux, windows]
                steps:
                  - name: step
                    type: artifacts
                    artifact_name: A
                    target_files: ["x/**"]
            """);

        Assert.Contains(errors, error => error.Contains("matrix", StringComparison.Ordinal));
    }

    [Fact]
    public void AStepAndAStage_CannotPublishTheSameName()
    {
        var errors = Errors("""
            name: ci
            stages:
              - name: Build
                agent: linux-01
                artifacts: ["out/**"]
                steps:
                  - name: build
                    shell: make
              - name: Publish
                agent: linux-01
                steps:
                  - name: again
                    type: artifacts
                    artifact_name: Build-artifacts
                    target_files: ["out/**"]
            """);

        Assert.Contains(errors, error => error.Contains("both publish artifact 'Build-artifacts'", StringComparison.Ordinal));
    }
}
