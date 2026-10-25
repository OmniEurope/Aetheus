// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-14: release-fast starts and restores its colour by scripts, so its run finished Failed
/// where a typed deploy-prod finished RolledBack. A stage declares <c>deployment_role</c> and is judged
/// like the typed step it stands for.
/// </summary>
public sealed class PipelineDeploymentStagesTests
{
    private const string ScriptedYaml = """
        name: release-fast
        trigger: manual
        stages:
          - name: Deploy blue-green
            deployment_role: start
            steps:
              - name: Deploy production blue-green
                shell: sh deploy.sh
          - name: Notify
            condition: "failed()"
            steps:
              - name: mail
                shell: echo failed
          - name: Roll back on failure or missing confirmation
            condition: "failed()"
            deployment_role: rollback
            steps:
              - name: Restore previous colour
                shell: sh rollback.sh
        """;

    private static PipelineYamlDefinition Parse(string yaml) => YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(yaml);

    private static PipelineStageDefinition Stage(string? role, string? condition = null, string? stepType = null) => new()
    {
        Name = "s",
        DeploymentRole = role,
        Condition = condition,
        Steps = [new PipelineStepDefinition { Name = "x", Type = stepType ?? string.Empty, Shell = stepType is null ? "true" : string.Empty }]
    };

    [Fact]
    public void R_14_TheYamlKey_IsReadAsTheStagesDeploymentRole()
    {
        var definition = Parse(ScriptedYaml);

        Assert.Equal(PipelineDeploymentRoles.Start, definition.Stages[0].DeploymentRole);
        Assert.Null(definition.Stages[1].DeploymentRole);
        Assert.Equal(PipelineDeploymentRoles.Rollback, definition.Stages[2].DeploymentRole);
    }

    [Fact]
    public void R_14_ScriptStagesThatDeclareTheirRole_AreTheStartAndRollbackStages()
    {
        var definition = Parse(ScriptedYaml);

        Assert.Equal(["Deploy blue-green"], PipelineDeploymentStages.StartNames(definition));
        // The failure handler that only mails never makes a failed run a rolled-back one.
        Assert.Equal(["Roll back on failure or missing confirmation"], PipelineDeploymentStages.RollbackNames(definition));
    }

    [Fact]
    public void R_14_TheJobsOfAStage_InheritItsRole()
    {
        var definition = new PipelineYamlDefinition
        {
            Name = "jobs",
            Stages =
            [
                new PipelineStageDefinition
                {
                    Name = "Deploy",
                    DeploymentRole = PipelineDeploymentRoles.Start,
                    Jobs = [new PipelineJobDefinition { Name = "deploy-job", Steps = [new PipelineStepDefinition { Name = "x", Shell = "true" }] }]
                }
            ]
        };

        Assert.Equal(["deploy-job"], PipelineDeploymentStages.StartNames(definition));
    }

    [Theory]
    [InlineData(PipelineDeploymentRoles.Rollback, "failed()", null, true)]
    [InlineData("ROLLBACK", " failed() ", null, true)]
    [InlineData(null, "failed()", "bluegreen-rollback", true)]
    [InlineData(PipelineDeploymentRoles.Rollback, null, null, false)]
    [InlineData(PipelineDeploymentRoles.Rollback, "always()", null, false)]
    [InlineData(null, "failed()", null, false)]
    [InlineData(PipelineDeploymentRoles.Start, "failed()", null, false)]
    public void R_14_ARollbackStage_IsAFailureHandlerThatUndoesTheDeployment(string? role, string? condition, string? stepType, bool expected)
    {
        var stage = Stage(role, condition, stepType);

        Assert.Equal(expected, PipelineDeploymentStages.IsRollback(stage));
        // The cancellation keeps the same stages running.
        Assert.Equal(expected, PipelineRunControlService.IsDeploymentRollback(stage));
    }

    [Theory]
    [InlineData(PipelineDeploymentRoles.Start, null, true)]
    [InlineData(null, "bluegreen-up", true)]
    [InlineData(null, null, false)]
    [InlineData(PipelineDeploymentRoles.Rollback, null, false)]
    public void R_14_AStartStage_StartsTheDeployment(string? role, string? stepType, bool expected) =>
        Assert.Equal(expected, PipelineDeploymentStages.IsStart(Stage(role, stepType: stepType)));

    [Theory]
    [InlineData("start", null, null)]
    [InlineData("rollback", "failed()", null)]
    [InlineData("deploy", null, "deployment_role must be one of start, rollback.")]
    [InlineData("rollback", null, "deployment_role: rollback requires condition: failed().")]
    [InlineData("rollback", "always()", "deployment_role: rollback requires condition: failed().")]
    public void R_14_TheValidator_RefusesAnUnknownRoleAndARollbackThatIsNotAFailureHandler(string role, string? condition, string? error)
    {
        var definition = new PipelineYamlDefinition
        {
            Name = "p",
            Stages = [new PipelineStageDefinition { Name = "s", Agent = "linux-01", DeploymentRole = role, Condition = condition, Steps = [new PipelineStepDefinition { Name = "x", Shell = "true" }] }]
        };
        var errors = new List<string>();

        PipelineDefinitionValidator.ValidateStages(definition, errors, []);

        var roleErrors = errors.Where(message => message.Contains("deployment_role", StringComparison.Ordinal)).ToList();
        if (error is null) Assert.Empty(roleErrors);
        else Assert.Equal($"Stage 's': {error}", Assert.Single(roleErrors));
    }
}
