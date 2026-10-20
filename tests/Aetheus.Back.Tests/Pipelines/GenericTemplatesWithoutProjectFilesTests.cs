// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// PLAN-002 lot 1.10, recette R3-62 to R3-64 and ADR-050: a project runs the generic templates without
/// adding a single file to its repository. Each shipped generic template is extended exactly as the setup
/// wizard writes a project's pipeline (<c>extends: name@N</c>, variables, no stage of its own), resolved
/// by the real <see cref="PipelineTemplateResolver"/> and validated; the resolved definition must then
/// name no script of the repository and no scanner configuration file the repository would have to carry.
/// </summary>
public sealed class GenericTemplatesWithoutProjectFilesTests
{
    private const int OrganizationId = 3;

    public static TheoryData<string, string> GenericTemplates()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, definition) in DeliveryPipelineTemplateSeeder.Definitions)
            foreach (var resource in definition.Resources.Where(item => item.StartsWith("generic/", StringComparison.Ordinal)))
                data.Add(name, resource);
        return data;
    }

    [Theory]
    [MemberData(nameof(GenericTemplates))]
    public async Task ProjectPipelineExtendingTheTemplate_ResolvesAndValidates_WithNoRepositoryFile(
        string templateName, string resource)
    {
        var templateYaml = DeliveryPipelineTemplateSeeder.ReadResource(resource);
        var resolver = new PipelineTemplateResolver(Repository(templateName, templateYaml));

        var resolution = await resolver.ResolveAsync(
            ProjectPipeline(templateName), OrganizationId, ct: TestContext.Current.CancellationToken);

        var definition = resolution.Definition;
        var errors = new List<string>();
        var warnings = new List<string>();
        PipelineDefinitionValidator.ValidateDefinitionBasics(definition, errors, warnings);
        PipelineDefinitionValidator.ValidateStages(definition, errors, warnings);
        Assert.Empty(errors);
        Assert.Equal(templateName, resolution.TemplateName);
        Assert.Equal(1, resolution.ResolvedVersion);

        // The project adds no stage: the run is the template's, stage for stage.
        var template = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(templateYaml)!;
        Assert.Equal(template.Stages.Select(stage => stage.Name), definition.Stages.Select(stage => stage.Name));

        var stages = YamlParsingHelper.FlattenJobs(definition);
        var shells = stages.SelectMany(stage => stage.Steps).Select(step => step.Shell ?? string.Empty).ToList();
        Assert.All(shells, shell => Assert.DoesNotContain(".pipeline/scripts/", shell, StringComparison.Ordinal));
        Assert.All(shells, shell => Assert.DoesNotContain("deploy/scripts/", shell, StringComparison.Ordinal));
        Assert.All(shells, shell => Assert.DoesNotContain("test -f", shell, StringComparison.Ordinal));
        Assert.Empty(PipelineSetupReadinessService.RequiredScannerFiles(stages));
    }

    /// <summary>The project's own commands are declared in its pipeline, never in a file: every
    /// template step that runs project work reads one <c>APPLICATION_*_COMMANDS</c> variable the
    /// template declares empty, and the project's value reaches the resolved definition.</summary>
    [Theory]
    [InlineData("application-ci", "generic/application-ci.yaml", "APPLICATION_CI_COMMANDS")]
    [InlineData("application-quality", "generic/application-quality.yaml", "APPLICATION_QUALITY_COMMANDS")]
    [InlineData("application-qa", "generic/application-qa.yaml", "APPLICATION_QA_COMMANDS")]
    [InlineData("application-qa-extended", "generic/application-qa-extended.yaml", "APPLICATION_QA_EXTENDED_COMMANDS")]
    [InlineData("application-deploy-prod", "generic/application-deploy-prod.yaml", "APPLICATION_DEPLOY_PROD_COMMANDS")]
    [InlineData("application-release-fast", "generic/application-release-fast.yaml", "APPLICATION_RELEASE_FAST_COMMANDS")]
    [InlineData("package-telemetry", "generic/package-telemetry.yaml", "APPLICATION_PACKAGE_TELEMETRY_COMMANDS")]
    [InlineData("package-web-analytics-dotnet", "generic/package-web-analytics-dotnet.yaml", "APPLICATION_PACKAGE_DOTNET_ANALYTICS_COMMANDS")]
    [InlineData("package-web-analytics-browser", "generic/package-web-analytics-browser.yaml", "APPLICATION_PACKAGE_BROWSER_ANALYTICS_COMMANDS")]
    [InlineData("publish-observability-packages", "generic/publish-observability-packages.yaml", "APPLICATION_PUBLISH_PACKAGES_COMMANDS")]
    public async Task ProjectCommands_AreAPipelineVariable_TheTemplateRuns(
        string templateName, string resource, string variable)
    {
        var templateYaml = DeliveryPipelineTemplateSeeder.ReadResource(resource);
        var template = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(templateYaml)!;
        var resolver = new PipelineTemplateResolver(Repository(templateName, templateYaml));
        var project = ProjectPipeline(templateName) + $"\n  {variable}: \"make release\"";

        var resolved = (await resolver.ResolveAsync(
            project, OrganizationId, ct: TestContext.Current.CancellationToken)).Definition;

        Assert.Equal(string.Empty, template.Variables[variable]);
        Assert.Equal("make release", resolved.Variables[variable]);
        var runner = Assert.Single(
            YamlParsingHelper.FlattenJobs(resolved).SelectMany(stage => stage.Steps),
            step => (step.Shell ?? string.Empty).Contains($"sh -eu -c \"${variable}\"", StringComparison.Ordinal));
        Assert.Contains($"${{{variable}:-}}", runner.Shell, StringComparison.Ordinal);
    }

    /// <summary>Without commands, the steps whose work only the project knows refuse to run rather than
    /// report a QA, a deployment or a package that never happened (AGENTS.md, no fake success).</summary>
    [Theory]
    [InlineData("generic/application-qa.yaml", "APPLICATION_QA_COMMANDS")]
    [InlineData("generic/application-qa-extended.yaml", "APPLICATION_QA_EXTENDED_COMMANDS")]
    [InlineData("generic/application-deploy-prod.yaml", "APPLICATION_DEPLOY_PROD_COMMANDS")]
    [InlineData("generic/application-release-fast.yaml", "APPLICATION_RELEASE_FAST_COMMANDS")]
    [InlineData("generic/package-telemetry.yaml", "APPLICATION_PACKAGE_TELEMETRY_COMMANDS")]
    [InlineData("generic/package-web-analytics-dotnet.yaml", "APPLICATION_PACKAGE_DOTNET_ANALYTICS_COMMANDS")]
    [InlineData("generic/package-web-analytics-browser.yaml", "APPLICATION_PACKAGE_BROWSER_ANALYTICS_COMMANDS")]
    [InlineData("generic/publish-observability-packages.yaml", "APPLICATION_PUBLISH_PACKAGES_COMMANDS")]
    public void WithoutCommands_ProjectWorkFailsAndSaysWhy(string resource, string variable)
    {
        var shell = CommandStep(resource, variable).Shell!;

        var guard = shell.IndexOf($"if test -z \"${{{variable}:-}}\"; then", StringComparison.Ordinal);
        var refusal = shell.IndexOf("exit 1", StringComparison.Ordinal);
        var run = shell.IndexOf($"sh -eu -c \"${variable}\"", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < refusal && refusal < run, $"{resource} must refuse before it runs.");
        Assert.Contains($"set {variable} in this pipeline.", shell, StringComparison.Ordinal);
    }

    private static PipelineStepDefinition CommandStep(string resource, string variable) =>
        Assert.Single(
            YamlParsingHelper.FlattenJobs(YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(
                DeliveryPipelineTemplateSeeder.ReadResource(resource))!).SelectMany(stage => stage.Steps),
            step => (step.Shell ?? string.Empty).Contains($"\"${variable}\"", StringComparison.Ordinal));

    /// <summary>The pipeline the setup wizard writes for a project: the template, its prefix, no stage.</summary>
    private static string ProjectPipeline(string templateName) => $"""
        name: demo-{templateName}
        extends: {templateName}@1
        trigger: manual
        stages: []
        variables:
          APPLICATION_PREFIX: "demo"
        """;

    private static IPipelineTemplateRepository Repository(string name, string yaml)
    {
        var repository = Substitute.For<IPipelineTemplateRepository>();
        var template = new PipelineTemplate
        {
            Id = 41,
            Name = name,
            OrganizationId = OrganizationId,
            LatestVersion = 1,
            Versions = [new PipelineTemplateVersion { Version = 1, YamlContent = yaml }]
        };
        repository.FindTemplateByNameAsync(name, OrganizationId, Arg.Any<CancellationToken>()).Returns(template);
        return repository;
    }
}
