// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>A stage whose <c>depends_on</c> names no stage, itself, or closes a cycle would stay pending
/// forever: the definition is refused. Names are compared as the dispatcher does (ordinal), on the flattened
/// stages.</summary>
public sealed class PipelineStageDependencyValidationTests
{
    [Fact]
    public void AValidChain_HasNoError()
    {
        var errors = Validate("""
            name: p
            stages:
              - name: Build
                agent: a
                steps: [{ name: s, shell: echo }]
              - name: Test
                agent: a
                depends_on: [Build]
                steps: [{ name: s, shell: echo }]
              - name: Ship
                agent: a
                depends_on: [Build, Test]
                steps: [{ name: s, shell: echo }]
            """);

        Assert.Empty(errors);
    }

    [Fact]
    public void AnUnknownDependency_IsAnError()
    {
        var errors = Validate("""
            name: p
            stages:
              - name: Build
                agent: a
                steps: [{ name: s, shell: echo }]
              - name: Test
                agent: a
                depends_on: [Biuld]
                steps: [{ name: s, shell: echo }]
            """);

        Assert.Equal(["Stage 'Test': depends_on 'Biuld' names no stage of this pipeline."], errors);
    }

    [Fact]
    public void ADependencyWithAnotherCase_IsAnError_AsTheDispatcherComparesOrdinal()
    {
        var errors = Validate("""
            name: p
            stages:
              - name: Build
                agent: a
                steps: [{ name: s, shell: echo }]
              - name: Test
                agent: a
                depends_on: [build]
                steps: [{ name: s, shell: echo }]
            """);

        Assert.Equal(["Stage 'Test': depends_on 'build' names no stage of this pipeline."], errors);
    }

    [Fact]
    public void AStageDependingOnItself_IsAnError()
    {
        var errors = Validate("""
            name: p
            stages:
              - name: Build
                agent: a
                depends_on: [Build]
                steps: [{ name: s, shell: echo }]
            """);

        Assert.Equal(["Stage 'Build': depends_on 'Build' names the stage itself."], errors);
    }

    [Fact]
    public void ADependencyCycle_IsAnError()
    {
        var errors = Validate("""
            name: p
            stages:
              - name: A
                agent: a
                depends_on: [C]
                steps: [{ name: s, shell: echo }]
              - name: B
                agent: a
                depends_on: [A]
                steps: [{ name: s, shell: echo }]
              - name: C
                agent: a
                depends_on: [B]
                steps: [{ name: s, shell: echo }]
            """);

        Assert.Equal(["Stages form a depends_on cycle: A -> C -> B -> A."], errors);
    }

    [Fact]
    public void JobsAreStagesOfTheirOwn_AndAJobsStageNamingAJobsStageWaitsForItsJobs()
    {
        var errors = Validate("""
            name: p
            stages:
              - name: Build
                agent: a
                jobs:
                  - name: compile
                    steps: [{ name: s, shell: echo }]
                  - name: lint
                    depends_on: [compile]
                    steps: [{ name: s, shell: echo }]
              - name: Ship
                agent: a
                depends_on: [Build]
                jobs:
                  - name: upload
                    steps: [{ name: s, shell: echo }]
            """);

        Assert.Empty(errors);
    }

    [Fact]
    public void AJobDependingOnAnUnknownSibling_IsAnError()
    {
        var errors = Validate("""
            name: p
            stages:
              - name: Build
                agent: a
                jobs:
                  - name: lint
                    depends_on: [compile]
                    steps: [{ name: s, shell: echo }]
            """);

        Assert.Equal(["Stage 'lint': depends_on 'compile' names no stage of this pipeline."], errors);
    }

    [Fact]
    public void ADefinitionThatExtendsATemplate_IsNotCheckedForNamesItCannotSee()
    {
        var errors = Validate("""
            name: p
            extends: base@1
            stages:
              - name: Mine
                agent: a
                depends_on: [FromTheBase]
                steps: [{ name: s, shell: echo }]
            """);

        Assert.Empty(errors);
    }

    private static List<string> Validate(string yaml)
    {
        var definition = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(yaml);
        var errors = new List<string>();
        PipelineDefinitionValidator.ValidateStages(definition, errors, []);
        return errors;
    }
}
