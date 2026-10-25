// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The reusable host cutover template. It is what the deployment pipelines will extend instead of
/// each carrying their own shell copy of the same sequence, so a mistake here would propagate to
/// every project that adopts it rather than to one.
///
/// These tests exist because the template is inert until something extends it: nothing else would
/// catch a misspelled field or an unknown step type before the day it is first used in anger.
/// </summary>
public sealed class HostBlueGreenTemplateTests
{
    /// <summary>Contracts target the latest template version published by the seeder.</summary>
    private const string Resource = "host-bluegreen-deploy-v2.yaml";

    [Fact]
    public void Resource_IsTheLatestPublishedVersion()
    {
        var (_, _, resources) = DeliveryTemplateCatalog.Definitions["host-bluegreen-deploy"];

        Assert.Equal(Resource, resources[^1]);
    }

    /// <summary>
    /// One Compose list, no per-step subset: the control plane decides per step what it can forward,
    /// so a consumer maintains one list. A subset left behind would be a variable nothing reads, and a
    /// consumer still setting it would believe it did something.
    /// </summary>
    [Fact]
    public void Template_DeclaresOneComposeListAndNoPerStepSubset()
    {
        var definition = Parse();

        Assert.DoesNotContain(definition.Variables.Keys, key => key.StartsWith("BG_COMPOSE_ENV_", StringComparison.Ordinal));
        var yaml = DeliveryTemplateCatalog.ReadResource(Resource);
        Assert.DoesNotContain("$(BG_COMPOSE_ENV_CUTOVER)", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("$(BG_COMPOSE_ENV_ROLLBACK)", yaml, StringComparison.Ordinal);
    }

    private static PipelineYamlDefinition Parse() =>
        YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(
            DeliveryTemplateCatalog.ReadResource(Resource));

    [Fact]
    public void Template_ParsesAndValidatesCleanly()
    {
        var definition = Parse();
        var errors = new List<string>();
        var warnings = new List<string>();

        PipelineDefinitionValidator.ValidateDefinitionBasics(definition, errors, warnings);
        PipelineDefinitionValidator.ValidateStages(definition, errors, warnings);

        Assert.Empty(errors);
    }

    // A typed step carries no shell, so an unknown or misspelled type would be rejected by the
    // validator as a step with nothing to run. Asserting the exact set also pins that the template
    // stayed free of shell.
    [Fact]
    public void EveryStep_UsesANativeTypeAndNoShell()
    {
        var steps = Parse().Stages.SelectMany(stage => stage.Steps).ToList();

        Assert.All(steps, step =>
        {
            Assert.False(string.IsNullOrWhiteSpace(step.Type), $"Step '{step.Name}' has no type.");
            Assert.True(string.IsNullOrWhiteSpace(step.Shell), $"Step '{step.Name}' still carries shell.");
        });
        Assert.Equal(
            ["bluegreen-commit", "bluegreen-migrate", "bluegreen-rollback", "bluegreen-switch", "bluegreen-up", "smoke"],
            steps.Select(step => step.Type!).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>Evidence precedes commit so the cutover remains compensatable until validation
    /// completes.</summary>
    [Fact]
    public void Commit_DependsOnTheEvidenceStage()
    {
        var stages = Parse().Stages;
        var commit = Assert.Single(stages, stage => stage.Name == "Commit");
        var evidence = Assert.Single(stages, stage => stage.Name == "Evidence");

        Assert.Contains("Evidence", commit.DependsOn);
        Assert.Contains("Switch", evidence.DependsOn);
    }

    /// <summary>Rollback depends on the last guarded stage, keeping it eligible through evidence
    /// and commit failures.</summary>
    [Fact]
    public void Rollback_IsAFailureHandlerOnTheLastStageItGuards()
    {
        var stages = Parse().Stages;
        var rollback = Assert.Single(stages, stage => stage.Name == "Rollback");

        Assert.Equal("failed()", rollback.Condition);
        Assert.Equal(["Commit"], rollback.DependsOn);
        // Nothing may run after the stage the rollback guards, or that stage would fail outside its
        // window and the rollback would be answered before it.
        Assert.DoesNotContain(
            stages.Where(stage => stage.Name != "Rollback"),
            stage => stage.DependsOn.Contains("Commit"));
    }

    // Rendering the upstream configuration IS the switch; without it the reload would re-apply the
    // configuration already live and report a successful cutover that never happened.
    [Fact]
    public void Switch_RendersTheUpstreamConfiguration()
    {
        var step = Assert.Single(
            Parse().Stages.SelectMany(stage => stage.Steps),
            candidate => candidate.Type == "bluegreen-switch");

        Assert.False(string.IsNullOrWhiteSpace(step.UpstreamTemplate));
        Assert.False(string.IsNullOrWhiteSpace(step.UpstreamConf));
        Assert.False(string.IsNullOrWhiteSpace(step.ReloadHelper));
        Assert.False(string.IsNullOrWhiteSpace(step.Revision));
    }

    /// <summary>The switch forwards the consumer's confirmation window; zero leaves the host window
    /// unarmed.</summary>
    [Fact]
    public void V6_Switch_ForwardsTheConfirmationWindow_WhichDefaultsToNone()
    {
        var definition = Parse();
        var step = Assert.Single(definition.Stages.SelectMany(stage => stage.Steps), candidate => candidate.Type == "bluegreen-switch");

        Assert.Equal("$(BG_CONFIRM_MINUTES)", step.ConfirmMinutes);
        Assert.Equal("0", definition.Variables["BG_CONFIRM_MINUTES"]);
    }

    /// <summary>The quick-return template names only native steps and records the release it returns to.</summary>
    [Fact]
    public void RevertTemplate_RevertsThenRecordsTheReleaseItReturnedTo()
    {
        var definition = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(
            DeliveryTemplateCatalog.ReadResource("host-bluegreen-revert-v1.yaml"));
        var errors = new List<string>();
        var warnings = new List<string>();
        PipelineDefinitionValidator.ValidateDefinitionBasics(definition, errors, warnings);
        PipelineDefinitionValidator.ValidateStages(definition, errors, warnings);
        Assert.Empty(errors);

        var revert = Assert.Single(definition.Stages.Single(stage => stage.Name == "Revert").Steps);
        Assert.Equal("bluegreen-revert", revert.Type);
        Assert.Equal(["BLUEGREEN_REVERTED_REVISION"], revert.Outputs);
        var record = definition.Stages.Single(stage => stage.Name == "Record release");
        Assert.Equal(["Revert"], record.DependsOn);
        var release = Assert.Single(record.Steps);
        Assert.Equal("$(BLUEGREEN_REVERTED_REVISION)", release.Version);
        Assert.True(release.Deployed);
    }

    /// <summary>
    /// The evidence stage has to be able to fail, or the rollback below it is decorative: a failed()
    /// stage only fires on a failed stage, and the smoke executor exits 0 in advisory mode however bad
    /// the findings are.
    /// </summary>
    [Fact]
    public void Evidence_IsGradedAsBlockingSoTheRollbackHasATrigger()
    {
        var definition = Parse();
        var smoke = Assert.Single(
            definition.Stages.SelectMany(stage => stage.Steps),
            candidate => candidate.Type == "smoke");

        Assert.Equal("$(BG_SMOKE_GATE)", smoke.EvidenceGate);
        Assert.Equal("blocking", definition.Variables["BG_SMOKE_GATE"]);
    }

    // Without the migration sources the expand/contract gate silently has nothing to inspect, which
    // is how the previous rewrite lost the check while still claiming to enforce it.
    [Fact]
    public void Migrate_PointsAtTheMigrationSources()
    {
        var step = Assert.Single(
            Parse().Stages.SelectMany(stage => stage.Steps),
            candidate => candidate.Type == "bluegreen-migrate");

        Assert.False(string.IsNullOrWhiteSpace(step.MigrationsDir));
    }

    /// <summary>
    /// Nothing in the template may name a single application: it is meant to be extended by any
    /// project, and a hard-coded host or project would silently bind it to one.
    /// </summary>
    [Fact]
    public void Template_CarriesNoApplicationSpecificIdentity()
    {
        var yaml = DeliveryTemplateCatalog.ReadResource(Resource);

        foreach (var token in new[] { "aetheus-prod", "aetheus-demo", "sonytumen", "10025", "10029" })
            Assert.DoesNotContain(token, yaml, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every Compose invocation in the cutover must receive the same per-run values. A step left
    /// without them would run the same project against the Compose file's defaults - a placeholder
    /// image started under the banner of a successful cutover, which is exactly the drift the typed
    /// steps replaced.
    /// </summary>
    [Fact]
    public void EveryBlueGreenStep_CarriesTheRunsComposeInputs()
    {
        var definition = Parse();
        var blueGreenSteps = definition.Stages
            .SelectMany(stage => stage.Steps)
            .Where(step => step.Type?.StartsWith("bluegreen-", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.NotEmpty(blueGreenSteps);
        Assert.All(blueGreenSteps, step => Assert.Equal("$(BG_COMPOSE_ENV)", step.ComposeEnv));
        Assert.Equal(string.Empty, definition.Variables["BG_COMPOSE_ENV"]);
    }
}
