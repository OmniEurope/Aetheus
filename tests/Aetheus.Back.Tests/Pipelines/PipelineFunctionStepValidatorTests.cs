// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Save-time validation of <c>type: aetheus</c> steps against the function catalog.</summary>
public sealed class PipelineFunctionStepValidatorTests
{
    private static PipelineYamlDefinition? Parse(string step) =>
        YamlParsingHelper.ParseAndValidate($"""
            name: p
            stages:
              - name: Build
                steps:
                  - name: S
            {step}
            """);

    private static string Step(string body) =>
        string.Join('\n', body.Split('\n').Select(line => "        " + line));

    private static List<string> Errors(PipelineStepDefinition step)
    {
        var errors = new List<string>();
        PipelineFunctionStepValidator.Validate(errors, step, "stage 'x'");
        return errors;
    }

    [Fact]
    public void ValidFunctionStep_ParsesFromYaml_WithFunctionAndWith()
    {
        var definition = Parse(Step("type: aetheus\nfunction: detect-project-languages\nwith:\n  root: src"));

        var step = Assert.Single(Assert.Single(definition!.Stages).Steps);
        Assert.Equal("detect-project-languages", step.Function);
        Assert.Equal("src", step.With["root"]);
    }

    [Fact]
    public void ValidFunctionStep_WithoutOptionalParameter_HasNoError() =>
        Assert.Empty(Errors(new PipelineStepDefinition
        {
            Name = "S",
            Type = "aetheus",
            Function = "detect-project-languages"
        }));

    [Fact]
    public void MissingFunction_IsRefused() =>
        Assert.Contains("must name a function", Assert.Single(Errors(new PipelineStepDefinition
        {
            Name = "S",
            Type = "aetheus"
        })));

    [Fact]
    public void UnknownFunction_IsRefused_NamingStepAndFunction()
    {
        var error = Assert.Single(Errors(new PipelineStepDefinition
        {
            Name = "S",
            Type = "aetheus",
            Function = "nope"
        }));

        Assert.Contains("'S'", error);
        Assert.Contains("'nope'", error);
        Assert.Contains("stage 'x'", error);
    }

    [Fact]
    public void FunctionName_IsCaseSensitive() =>
        Assert.Single(Errors(new PipelineStepDefinition
        {
            Name = "S",
            Type = "aetheus",
            Function = "Detect-Project-Languages"
        }));

    [Fact]
    public void UnknownWithParameter_IsRefused()
    {
        var error = Assert.Single(Errors(new PipelineStepDefinition
        {
            Name = "S",
            Type = "aetheus",
            Function = "detect-project-languages",
            With = new() { ["rooot"] = "." }
        }));

        Assert.Contains("'rooot'", error);
    }

    [Fact]
    public void EnsureDotnetSdk_AcceptsItsManifestParameter() =>
        Assert.Empty(Errors(new PipelineStepDefinition
        {
            Name = "S",
            Type = "aetheus",
            Function = "ensure-dotnet-sdk",
            With = new() { ["global_json"] = "src/global.json" }
        }));

    [Fact]
    public void RestoreJavaScriptAnalysisDependencies_RefusesAnyWithParameter() =>
        Assert.Contains("'root'", Assert.Single(Errors(new PipelineStepDefinition
        {
            Name = "S",
            Type = "aetheus",
            Function = "restore-javascript-analysis-dependencies",
            With = new() { ["root"] = "." }
        })));

    [Fact]
    public void RunAffectedDotnetFormat_RequiresTheSolution() =>
        Assert.Contains("requires the with parameter 'solution'", Assert.Single(Errors(new PipelineStepDefinition
        {
            Name = "S",
            Type = "aetheus",
            Function = "run-affected-dotnet-format",
            With = new() { ["mode"] = "full" }
        })));

    [Fact]
    public void RunAffectedDotnetFormat_AcceptsItsParameters() =>
        Assert.Empty(Errors(new PipelineStepDefinition
        {
            Name = "S",
            Type = "aetheus",
            Function = "run-affected-dotnet-format",
            With = new()
            {
                ["solution"] = "App.slnx",
                ["mode"] = "affected",
                ["baseline"] = "$(DELIVERY_BASELINE_SOURCE_SHA)",
                ["head"] = "$(BUILD_SOURCEVERSION)",
                ["dotnet"] = "$(DOTNET)"
            }
        }));

    [Fact]
    public void PrepareImageInputs_RequiresTheImages() =>
        Assert.Contains("requires the with parameter 'images'", Assert.Single(Errors(new PipelineStepDefinition
        {
            Name = "S",
            Type = "aetheus",
            Function = "prepare-image-inputs",
            With = new() { ["source"] = "$(BUILD_SOURCEVERSION)" }
        })));

    [Theory]
    [InlineData("ensure-tls-certificates", "hosts")]
    [InlineData("retain-docker-images", "repositories")]
    [InlineData("prune-stale-qa-compose", "project_prefix")]
    public void DeploymentEnvironmentFunctions_RequireWhatTheScriptTookAsArguments(string function, string parameter) =>
        Assert.Contains($"requires the with parameter '{parameter}'", Assert.Single(Errors(new PipelineStepDefinition
        {
            Name = "S",
            Type = "aetheus",
            Function = function
        })));

    [Theory]
    [InlineData("ensure-tls-certificates", "hosts", "$(APP_HOST) $(API_HOST),$(API_HOST_ALIAS)")]
    [InlineData("grant-acme-challenge-traversal", "webroot", "$(ACME_WEBROOT)")]
    [InlineData("bluegreen-host-reset", "remove_images", "$(COMPOSE_PROJECT)-browser-smoke")]
    [InlineData("retain-docker-images", "repositories", "$(BACK_IMAGE_REPO) $(FRONT_IMAGE_REPO)")]
    [InlineData("prune-qa-buildx-cache", "max_cache_gib", "32")]
    [InlineData("prune-stale-qa-compose", "project_prefix", "app-qa")]
    public void DeploymentEnvironmentFunctions_AcceptTheirParameters(string function, string parameter, string value) =>
        Assert.Empty(Errors(new PipelineStepDefinition
        {
            Name = "S",
            Type = "aetheus",
            Function = function,
            With = new() { [parameter] = value }
        }));

    [Theory]
    [InlineData("app-version", "run_number", "$(BUILD_RELEASENUMBER)")]
    [InlineData("deploy-identity", "project_name", "$(BUILD_PROJECTNAME)")]
    [InlineData("verify-immutable-source", "mode", "full")]
    [InlineData("prepare-image-inputs", "images", "app-back.tar.gz app-front.tar.gz")]
    public void VersionSourceAndPackageFunctions_AcceptTheirParameters(string function, string parameter, string value) =>
        Assert.Empty(Errors(new PipelineStepDefinition
        {
            Name = "S",
            Type = "aetheus",
            Function = function,
            With = new() { [parameter] = value }
        }));

    [Fact]
    public void RecordStorageEvidence_AcceptsItsParametersAsRunVariables() =>
        Assert.Empty(Errors(new PipelineStepDefinition
        {
            Name = "S",
            Type = "aetheus",
            Function = "record-storage-evidence",
            With = new()
            {
                ["scope"] = "qa",
                ["run_id"] = "$(BUILD_BUILDID)",
                ["builder"] = "$(AETHEUS_BUILDX_BUILDER)",
                ["project_name"] = "$(BUILD_PROJECTNAME)",
                ["max_cache_gib"] = "80",
                ["plateau_growth_gib"] = "5"
            }
        }));

    [Fact]
    public void FunctionOnAStepOfAnotherType_IsRefused() =>
        Assert.Contains("only valid on a type: aetheus step", Assert.Single(Errors(new PipelineStepDefinition
        {
            Name = "S",
            Type = "lint",
            Function = "detect-project-languages"
        })));

    [Fact]
    public void WithOnAShellStep_IsRefused() =>
        Assert.Single(Errors(new PipelineStepDefinition
        {
            Name = "S",
            Shell = "echo",
            With = new() { ["root"] = "." }
        }));

    private static List<string> DefinitionErrors(string yaml)
    {
        var errors = new List<string>();
        PipelineDefinitionValidator.ValidateStages(
            YamlParsingHelper.ParseAndValidate(yaml)!, errors, []);
        return errors;
    }

    [Fact]
    public void DefinitionValidation_RefusesAnUnknownFunction() =>
        Assert.Contains(DefinitionErrors(Yaml("function: nope")), error => error.Contains("'nope'"));

    [Fact]
    public void DefinitionValidation_AcceptsAKnownFunction() =>
        Assert.DoesNotContain(DefinitionErrors(Yaml("function: detect-project-languages")),
            error => error.Contains("Aetheus step"));

    [Fact]
    public void DefinitionValidation_AllowsAFunctionStepInAContainerIsolatedStage() =>
        Assert.Empty(DefinitionErrors(Yaml("function: detect-project-languages", "isolation:\n      mode: container\n      image: alpine:3")));

    private static string Yaml(string function, string stageExtra = "") =>
        "name: p\nstages:\n  - name: Build\n    " + stageExtra + "\n    steps:\n      - name: S\n        type: aetheus\n        " + function + "\n";
}
