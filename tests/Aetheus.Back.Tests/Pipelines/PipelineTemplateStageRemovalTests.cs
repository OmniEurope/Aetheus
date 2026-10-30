// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-105: a pipeline that extends a template can leave a stage out at compile time (<c>remove: true</c>)
/// instead of having it skipped at run time. A stage nobody can wait for must not stall the ones after it: the
/// stages that depended on a removed stage depend on what it depended on. Templates here are inline strings; the
/// resolver is the real one.
/// </summary>
public sealed class PipelineTemplateStageRemovalTests
{
    private const int OrganizationId = 3;

    private const string Template = """
        name: chain
        stages:
          - name: Checkout
            steps:
              - name: Get the source
                checkout: true
          - name: Coverage
            depends_on: [Checkout]
            steps:
              - name: Publish coverage
                shell: echo coverage
          - name: Lint
            depends_on: [Coverage]
            steps:
              - name: Publish lint
                shell: echo lint
          - name: Complexity
            depends_on: [Lint]
            steps:
              - name: Measure
                shell: echo complexity
          - name: Languages
            group: Quality
            os: linux
            depends_on: [Complexity]
            steps:
              - name: Python
                shell: echo python
              - name: Java
                shell: echo java
          - name: Grade
            depends_on: [Languages, Coverage]
            steps:
              - name: Grade it
                shell: echo grade
        """;

    private static async Task<PipelineYamlDefinition> ResolveAsync(string childStages)
    {
        var template = new PipelineTemplate
        {
            Id = 41,
            Name = "chain",
            OrganizationId = OrganizationId,
            LatestVersion = 1,
            Versions = [new PipelineTemplateVersion { Version = 1, YamlContent = Template }]
        };
        var repository = Substitute.For<IPipelineTemplateRepository>();
        repository.FindTemplateByNameAsync("chain", OrganizationId, Arg.Any<CancellationToken>()).Returns(template);
        var resolution = await new PipelineTemplateResolver(repository).ResolveAsync(
            $"name: child\nextends: chain@1\n{childStages}", OrganizationId, ct: TestContext.Current.CancellationToken);
        return resolution.Definition;
    }

    private static string[] DependenciesOf(PipelineYamlDefinition definition, string stage) =>
        [.. definition.Stages.Single(item => item.Name == stage).DependsOn];

    [Fact]
    public async Task ARemovedStage_IsNotInTheResolvedPipeline_AndItsDependentsDependOnWhatItDependedOn()
    {
        var definition = await ResolveAsync("stages:\n  - name: Lint\n    remove: true\n");

        Assert.DoesNotContain(definition.Stages, stage => stage.Name == "Lint");
        Assert.Equal(["Coverage"], DependenciesOf(definition, "Complexity"));
    }

    [Fact]
    public async Task SeveralRemovedStagesInARow_AreSplicedThrough()
    {
        var definition = await ResolveAsync(
            "stages:\n  - name: Lint\n    remove: true\n  - name: Complexity\n    remove: true\n  - name: Languages\n    remove: true\n");

        Assert.Equal(["Checkout", "Coverage", "Grade"], definition.Stages.Select(stage => stage.Name));
        // Languages -> Complexity -> Lint -> Coverage; Grade already waited for Coverage: it is not listed twice.
        Assert.Equal(["Coverage"], DependenciesOf(definition, "Grade"));
    }

    [Fact]
    public async Task ARemovedStageNobodyWaitsFor_ChangesNoDependency()
    {
        var definition = await ResolveAsync("stages:\n  - name: Grade\n    remove: true\n");

        Assert.Equal(["Checkout", "Coverage", "Lint", "Complexity", "Languages"], definition.Stages.Select(stage => stage.Name));
        Assert.Equal(["Checkout"], DependenciesOf(definition, "Coverage"));
        Assert.Equal(["Complexity"], DependenciesOf(definition, "Languages"));
    }

    [Fact]
    public async Task ARemovedStageIsLookedUpWithoutRegardToCase()
    {
        var definition = await ResolveAsync("stages:\n  - name: lint\n    remove: true\n");

        Assert.DoesNotContain(definition.Stages, stage => stage.Name == "Lint");
        Assert.Equal(["Coverage"], DependenciesOf(definition, "Complexity"));
    }

    [Fact]
    public async Task ARemovedStepOfARestatedStage_LeavesTheOtherStepsAndTheSplicedDependencies()
    {
        var definition = await ResolveAsync("""
            stages:
              - name: Lint
                remove: true
              - name: Complexity
                remove: true
              - name: Languages
                group: Quality
                os: linux
                depends_on: [Complexity]
                steps:
                  - name: Java
                    remove: true
            """);

        var languages = definition.Stages.Single(stage => stage.Name == "Languages");
        Assert.Equal(["Python"], languages.Steps.Select(step => step.Name));
        Assert.Equal(["Coverage"], languages.DependsOn);
    }

    [Fact]
    public async Task WithoutARemoval_TheDependenciesAreExactlyTheTemplates()
    {
        var definition = await ResolveAsync("stages: []\n");

        Assert.Equal(["Checkout", "Coverage", "Lint", "Complexity", "Languages", "Grade"], definition.Stages.Select(stage => stage.Name));
        Assert.Equal(["Languages", "Coverage"], DependenciesOf(definition, "Grade"));
        Assert.Equal(["Lint"], DependenciesOf(definition, "Complexity"));
    }

    [Fact]
    public async Task ARestatedStageWithAnEmptyDependsOn_StillCutsItsDependencies_AsBefore()
    {
        var definition = await ResolveAsync("stages:\n  - name: Complexity\n    steps:\n      - name: Measure\n        shell: echo mine\n");

        Assert.Empty(DependenciesOf(definition, "Complexity"));
    }
}
