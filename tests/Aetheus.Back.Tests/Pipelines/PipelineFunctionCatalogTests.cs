// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The shared contract of <c>type: aetheus</c> steps: the catalog both the control plane and the agent
/// resolve function names against, the task kinds and capabilities a function travels under, and the
/// YAML and environment encodings of its parameters.
/// </summary>
public sealed partial class PipelineFunctionCatalogTests
{
    [GeneratedRegex("^[A-Z][A-Z0-9]*(_[A-Z0-9]+)*$")]
    private static partial Regex UpperSnake();

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$")]
    private static partial Regex KebabName();

    [GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$")]
    private static partial Regex SnakeKey();

    [Fact]
    public void FunctionNames_AreUniqueKebabCase()
    {
        var names = PipelineFunctionCatalog.All.Select(function => function.Name).ToList();

        Assert.NotEmpty(names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, name => Assert.Matches(KebabName(), name));
    }

    [Fact]
    public void Outputs_AreUniqueUpperSnakeNames()
    {
        foreach (var function in PipelineFunctionCatalog.All)
        {
            Assert.Equal(function.Outputs.Count, function.Outputs.Distinct(StringComparer.Ordinal).Count());
            Assert.All(function.Outputs, output => Assert.Matches(UpperSnake(), output));
        }
    }

    [Fact]
    public void NamedOutputParameter_IsARequiredParameterOfItsFunction()
    {
        var named = PipelineFunctionCatalog.All.Where(function => function.NamedOutputParameter is not null).ToList();

        // publish-failed-tests publishes <GATE>_FAILED_TESTS, a name only the step knows.
        Assert.Equal(["publish-failed-tests"], named.Select(function => function.Name));
        Assert.All(named, function => Assert.True(function.FindParameter(function.NamedOutputParameter!)?.Required));
    }

    [Fact]
    public void Parameters_AreUniqueSnakeCaseKeysWithADescription()
    {
        foreach (var function in PipelineFunctionCatalog.All)
        {
            var names = function.Parameters.Select(parameter => parameter.Name).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
            Assert.All(function.Parameters, parameter =>
            {
                Assert.Matches(SnakeKey(), parameter.Name);
                Assert.False(string.IsNullOrWhiteSpace(parameter.Description));
            });
        }
    }

    [Fact]
    public void DetectProjectLanguages_KeepsTheOutputsOfTheScriptItReplaces()
    {
        var function = PipelineFunctionCatalog.Find("detect-project-languages");

        Assert.NotNull(function);
        Assert.Equal(PipelineFunctionAccess.Build, function.Access);
        Assert.Equal(OperationKind.PipelineFunction, function.Operation);
        Assert.Equal(["HAS_DOTNET", "HAS_JAVASCRIPT", "HAS_PYTHON", "HAS_JAVA"], function.Outputs);
        var root = Assert.Single(function.Parameters);
        Assert.Equal("root", root.Name);
        Assert.False(root.Required);
        Assert.Same(root, function.FindParameter("root"));
        Assert.Null(function.FindParameter("Root"));
    }

    [Fact]
    public void EnsureDotnetSdk_PublishesTheExecutablePathTheScriptPrinted()
    {
        var function = PipelineFunctionCatalog.Find("ensure-dotnet-sdk");

        Assert.NotNull(function);
        Assert.Same(PipelineFunctionCatalog.EnsureDotnetSdk, function);
        Assert.Equal(PipelineFunctionAccess.Build, function.Access);
        Assert.Equal(["DOTNET"], function.Outputs);
        var manifest = Assert.Single(function.Parameters);
        Assert.Equal("global_json", manifest.Name);
        Assert.False(manifest.Required);
    }

    [Fact]
    public void RestoreJavaScriptAnalysisDependencies_TakesNoParameterAndPublishesNothing()
    {
        var function = PipelineFunctionCatalog.Find("restore-javascript-analysis-dependencies");

        Assert.NotNull(function);
        Assert.Same(PipelineFunctionCatalog.RestoreJavaScriptAnalysisDependencies, function);
        Assert.Equal(PipelineFunctionAccess.Build, function.Access);
        Assert.Empty(function.Parameters);
        Assert.Empty(function.Outputs);
    }

    [Fact]
    public void RunAffectedDotnetFormat_RequiresOnlyTheSolutionAndPublishesModeAndStatus()
    {
        var function = PipelineFunctionCatalog.Find("run-affected-dotnet-format");

        Assert.NotNull(function);
        Assert.Same(PipelineFunctionCatalog.RunAffectedDotnetFormat, function);
        Assert.Equal(PipelineFunctionAccess.Build, function.Access);
        Assert.Equal(["DOTNET_FORMAT_MODE", "DOTNET_FORMAT_GATE_STATUS"], function.Outputs);
        Assert.Equal(["solution", "mode", "baseline", "head", "dotnet", "restore"], function.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(["solution"], function.Parameters.Where(parameter => parameter.Required).Select(parameter => parameter.Name));
    }

    [Theory]
    [InlineData("ensure-tls-certificates", PipelineFunctionAccess.Deployment,
        new[] { "hosts", "min_days", "live_dir", "email", "helpers_dir" }, new[] { "hosts" })]
    [InlineData("grant-acme-challenge-traversal", PipelineFunctionAccess.Deployment, new[] { "webroot" }, new string[0])]
    [InlineData("bluegreen-host-reset", PipelineFunctionAccess.Deployment,
        new[] { "state_dir", "env_file", "secrets_file", "compose_project", "remove_images" }, new string[0])]
    [InlineData("retain-docker-images", PipelineFunctionAccess.Deployment,
        new[] { "repositories", "keep", "dry_run" }, new[] { "repositories" })]
    [InlineData("prune-qa-buildx-cache", PipelineFunctionAccess.Build,
        new[] { "builder", "max_cache_gib", "reserved_cache_gib", "min_free_gib" }, new string[0])]
    [InlineData("prune-stale-qa-compose", PipelineFunctionAccess.Build, new[] { "project_prefix", "run_id" }, new[] { "project_prefix" })]
    public void DeploymentEnvironmentFunctions_KeepTheContractOfTheScriptsTheyReplace(
        string name, PipelineFunctionAccess access, string[] parameters, string[] required)
    {
        var function = PipelineFunctionCatalog.Find(name);

        Assert.NotNull(function);
        Assert.Equal(access, function.Access);
        Assert.Equal(parameters, function.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(required, function.Parameters.Where(parameter => parameter.Required).Select(parameter => parameter.Name));
        Assert.Empty(function.Outputs);
    }

    [Theory]
    [InlineData("app-version", new[] { "run_number", "suffix" }, new string[0], new[] { "APP_VERSION" })]
    [InlineData("deploy-identity", new[] { "project_name", "helpers_dir" }, new string[0],
        new[] { "PROJECT_SLUG", "BACK_IMAGE_REPO", "FRONT_IMAGE_REPO", "AGENT_HELPERS_DIR" })]
    [InlineData("verify-immutable-source", new[] { "head", "mode" }, new string[0], new string[0])]
    [InlineData("prepare-image-inputs", new[] { "images", "input", "source" }, new[] { "images" }, new string[0])]
    [InlineData("authorize-baseline-bootstrap", new[] { "bootstrap", "allow_bootstrap" }, new string[0], new string[0])]
    [InlineData("export-baseline-identity", new[] { "contract", "migrations_dir" }, new string[0],
        new[] { "DELIVERY_BASELINE_BOOTSTRAP", "DELIVERY_BASELINE_SOURCE_SHA", "DELIVERY_BASELINE_SCHEMA_HASH", "DELIVERY_BASELINE_SCHEMA_MIGRATIONS_B64" })]
    [InlineData("generate-assurance-contract",
        new[]
        {
            "output", "evidence_directory", "source_sha", "quality", "security", "qa", "provenance", "security_history", "extended_qa",
            "profile", "performance_enabled", "allow_bootstrap", "inapplicable_tests"
        },
        new[] { "output", "evidence_directory", "source_sha", "quality", "security", "qa", "provenance" },
        new[] { "CANDIDATE_ASSURANCE_GRADE" })]
    [InlineData("verify-assurance-contract", new[] { "contract", "source_commit", "minimum_grade", "requirement" },
        new[] { "contract", "source_commit", "minimum_grade" }, new string[0])]
    [InlineData("verify-delivery-promotion", new[] { "candidate_contract", "production_schema", "artifact_directory", "live_contract" },
        new[] { "candidate_contract", "production_schema", "artifact_directory" }, new string[0])]
    [InlineData("generate-package-sbom",
        new[] { "ecosystem", "input", "name", "version", "source_commit", "output", "source_date_epoch" },
        new[] { "ecosystem", "input", "name", "version", "source_commit", "output" }, new string[0])]
    public void VersionSourceAndPackageFunctions_KeepTheContractOfTheScriptsTheyReplace(
        string name, string[] parameters, string[] required, string[] outputs)
    {
        var function = PipelineFunctionCatalog.Find(name);

        Assert.NotNull(function);
        Assert.Equal(PipelineFunctionAccess.Build, function.Access);
        Assert.Equal(parameters, function.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(required, function.Parameters.Where(parameter => parameter.Required).Select(parameter => parameter.Name));
        Assert.Equal(outputs, function.Outputs);
    }

    [Theory]
    [InlineData("verify-idle-frontend", new[] { "url" }, new[] { "url" })]
    [InlineData("settle-fast-release-transaction", new[] { "env_file" }, new string[0])]
    [InlineData("verify-production-state", new[] { "env_file", "state_dir", "legacy_env_file", "compose_project" }, new string[0])]
    [InlineData("verify-production-secrets", new[] { "keys", "env_file" }, new[] { "keys" })]
    [InlineData("backfill-env-secret", new[] { "key", "env_file" }, new[] { "key" })]
    [InlineData("reconcile-env-urls", new[] { "urls", "env_file" }, new[] { "urls" })]
    [InlineData("render-env-sample", new[] { "sample", "env_file", "public_api_url", "public_app_url", "app_version" }, new[] { "sample" })]
    [InlineData("render-apache-upstream",
        new[] { "template", "output", "define_only", "front_port", "back_port", "active_color", "define", "upstream_conf" },
        new[] { "template", "output" })]
    [InlineData("publish-vitrine", new[] { "domain", "web_root", "placeholder", "source_dir" }, new[] { "domain", "web_root", "placeholder" })]
    public void ProductionDeployChecks_AreDeploymentFunctionsThatPublishNothing(string name, string[] parameters, string[] required)
    {
        var function = PipelineFunctionCatalog.Find(name);

        Assert.NotNull(function);
        Assert.Equal(PipelineFunctionAccess.Deployment, function.Access);
        Assert.Equal(OperationKind.PipelineDeploymentFunction, function.Operation);
        Assert.Equal(parameters, function.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(required, function.Parameters.Where(parameter => parameter.Required).Select(parameter => parameter.Name));
        Assert.Empty(function.Outputs);
    }

    [Theory]
    [InlineData("verify-release-ancestry", new[] { "source_commit_file", "deploy_ref" }, new[] { "source_commit_file" })]
    [InlineData("payload-evidence", new[] { "mode", "source_sha", "evidence", "artifacts", "kind" },
        new[] { "mode", "source_sha", "evidence", "artifacts" })]
    [InlineData("record-nightly-qualification",
        new[] { "output", "source_sha", "security", "quality", "security_history", "extended_dynamic_security", "assurance_grade" },
        new[] { "output", "source_sha", "security" })]
    public void ReleaseAndNightlyEvidenceFunctions_KeepTheArgumentsOfTheScriptsTheyReplaceAndPublishNothing(
        string name, string[] parameters, string[] required)
    {
        var function = PipelineFunctionCatalog.Find(name);

        Assert.NotNull(function);
        Assert.Contains(function, PipelineFunctionCatalog.All);
        Assert.Equal(PipelineFunctionAccess.Build, function.Access);
        Assert.Equal(OperationKind.PipelineFunction, function.Operation);
        Assert.Equal(parameters, function.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(required, function.Parameters.Where(parameter => parameter.Required).Select(parameter => parameter.Name));
        Assert.Empty(function.Outputs);
    }

    [Fact]
    public void VerifyPublicDistribution_TakesTheCheckoutBranchAndDepthOfTheVerifyModeAndPublishesItsVerdict()
    {
        var function = PipelineFunctionCatalog.Find("verify-public-distribution");

        Assert.NotNull(function);
        Assert.Same(PipelineFunctionCatalog.VerifyPublicDistribution, function);
        Assert.Contains(function, PipelineFunctionCatalog.All);
        Assert.Equal(PipelineFunctionAccess.Build, function.Access);
        Assert.Equal(OperationKind.PipelineFunction, function.Operation);
        Assert.Equal(["public_checkout", "branch", "depth"], function.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(["public_checkout"], function.Parameters.Where(parameter => parameter.Required).Select(parameter => parameter.Name));
        Assert.Equal(["PUBLIC_VERIFIED", "PUBLIC_MATCHED_COMMIT"], function.Outputs);
    }

    [Fact]
    public void RecordStorageEvidence_KeepsTheInputsOfTheScriptItReplacesAndPublishesNothing()
    {
        var function = PipelineFunctionCatalog.Find("record-storage-evidence");

        Assert.NotNull(function);
        Assert.Same(PipelineFunctionCatalog.RecordStorageEvidence, function);
        Assert.Equal(PipelineFunctionAccess.Build, function.Access);
        Assert.Equal(
            ["scope", "run_id", "builder", "project_name", "max_cache_gib", "plateau_growth_gib"],
            function.Parameters.Select(parameter => parameter.Name));
        Assert.DoesNotContain(function.Parameters, parameter => parameter.Required);
        Assert.Empty(function.Outputs);
    }

    [Fact]
    public void PersistCompatibilityMode_PublishesTheAttestationTheRunPageAndTheSealRead()
    {
        var function = PipelineFunctionCatalog.Find("persist-compatibility-mode");

        Assert.NotNull(function);
        Assert.Same(PipelineFunctionCatalog.PersistCompatibilityMode, function);
        Assert.Equal(PipelineFunctionAccess.Build, function.Access);
        Assert.Empty(function.Parameters);
        Assert.Equal(
            [
                "QA_CURRENT_INTEGRATION_GATE_STATUS", "QA_CURRENT_E2E_GATE_STATUS", "QA_PREVIOUS_SMOKE_GATE_STATUS",
                "QA_PREVIOUS_INTEGRATION_GATE_STATUS", "QA_PREVIOUS_E2E_GATE_STATUS",
                "COMPATIBILITY_MODE", "PREVIOUS_VERSION_TESTED", "COMPATIBILITY_VERDICT"
            ],
            function.Outputs);
        // The five statuses are the seal's own N/N-1 gates: a name spelled differently here would
        // publish a status nothing grades.
        Assert.All(function.Outputs.Take(5), output => Assert.Contains(output, PipelineControlVerdict.TestGateStatusVariables.Values));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Detect-Project-Languages")]
    [InlineData("detect-project-languages ")]
    [InlineData("unknown-function")]
    public void Find_RefusesAnythingButTheExactName(string? name) =>
        Assert.Null(PipelineFunctionCatalog.Find(name));

    [Fact]
    public void TargetValidator_AcceptsABuildFunctionOnlyAsABuildTask()
    {
        Assert.True(OperationTargetValidator.IsValid(OperationKind.PipelineFunction, "detect-project-languages"));
        Assert.False(OperationTargetValidator.IsValid(OperationKind.PipelineDeploymentFunction, "detect-project-languages"));
    }

    [Theory]
    [InlineData(OperationKind.PipelineFunction, null)]
    [InlineData(OperationKind.PipelineFunction, "")]
    [InlineData(OperationKind.PipelineFunction, "unknown-function")]
    [InlineData(OperationKind.PipelineFunction, "detect-project-languages; rm -rf /")]
    [InlineData(OperationKind.PipelineDeploymentFunction, "unknown-function")]
    public void TargetValidator_RefusesNamesOutsideTheCatalog(OperationKind kind, string? target) =>
        Assert.False(OperationTargetValidator.IsValid(kind, target));

    [Fact]
    public void TargetValidator_AcceptsEveryCatalogFunctionUnderItsOwnKindOnly()
    {
        foreach (var function in PipelineFunctionCatalog.All)
        {
            var other = function.Operation == OperationKind.PipelineFunction
                ? OperationKind.PipelineDeploymentFunction
                : OperationKind.PipelineFunction;
            Assert.True(OperationTargetValidator.IsValid(function.Operation, function.Name));
            Assert.False(OperationTargetValidator.IsValid(other, function.Name));
        }
    }

    [Fact]
    public void Operation_MapsEachAccessToItsOwnKind()
    {
        var descriptor = PipelineFunctionCatalog.All[0];

        Assert.Equal(OperationKind.PipelineFunction, (descriptor with { Access = PipelineFunctionAccess.Build }).Operation);
        Assert.Equal(
            OperationKind.PipelineDeploymentFunction,
            (descriptor with { Access = PipelineFunctionAccess.Deployment }).Operation);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (descriptor with { Access = (PipelineFunctionAccess)99 }).Operation);
    }

    [Fact]
    public void RequiredFor_SeparatesTheBuildAndDeploymentCapabilities()
    {
        Assert.Equal("pipeline.functions", AgentCapabilities.PipelineFunctions);
        Assert.Equal("deployment.functions", AgentCapabilities.DeploymentFunctions);
        Assert.Equal(AgentCapabilities.PipelineFunctions, AgentCapabilities.RequiredFor(OperationKind.PipelineFunction));
        Assert.Equal(
            AgentCapabilities.DeploymentFunctions,
            AgentCapabilities.RequiredFor(OperationKind.PipelineDeploymentFunction));
    }

    [Fact]
    public void OperationKinds_KeepTheirWireValues()
    {
        Assert.Equal(626, (int)OperationKind.PipelineFunction);
        Assert.Equal(627, (int)OperationKind.PipelineDeploymentFunction);
    }

    [Fact]
    public void StepYaml_ReadsFunctionAndWith()
    {
        const string yaml = """
            name: Languages
            type: aetheus
            function: detect-project-languages
            with:
              root: src
            """;

        var step = YamlParsingHelper.Deserializer.Deserialize<PipelineStepDefinition>(yaml);

        Assert.Equal(PipelineFunctionCatalog.StepType, step.Type);
        Assert.Equal("detect-project-languages", step.Function);
        Assert.Equal("src", Assert.Single(step.With, pair => pair.Key == "root").Value);
    }

    [Fact]
    public void Parameters_RoundTripThroughTheEnvironmentEncoding()
    {
        var parameters = new Dictionary<string, string> { ["root"] = "src", ["min_days"] = "30" };

        var json = PipelineFunctionVariables.SerializeParameters(parameters);

        Assert.True(PipelineFunctionVariables.TryParseParameters(json, out var parsed));
        Assert.Equal(parameters.OrderBy(pair => pair.Key), parsed.OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parameters_AbsentDecodeAsEmpty(string? json)
    {
        Assert.True(PipelineFunctionVariables.TryParseParameters(json, out var parsed));
        Assert.Empty(parsed);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"root\":1}")]
    [InlineData("{\"root\":null}")]
    [InlineData("{not json")]
    public void Parameters_MalformedAreRefusedNotReadAsEmpty(string json) =>
        Assert.False(PipelineFunctionVariables.TryParseParameters(json, out _));
}
