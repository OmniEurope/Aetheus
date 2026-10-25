// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Which stages start a deployment and which undo it. A typed step says it (<c>bluegreen-up</c>,
/// <c>bluegreen-rollback</c>); a stage that does the same by a script says it with
/// <c>deployment_role</c> (recette R-14). The run status, the cancellation and the dispatch all read
/// these two answers, so a script deployment is judged like a typed one.
/// </summary>
internal static class PipelineDeploymentStages
{
    /// <summary>The stage starts the new deployment.</summary>
    public static bool IsStart(PipelineStageDefinition stage) =>
        RunsStep(stage, "bluegreen-up") || HasRole(stage, PipelineDeploymentRoles.Start);

    /// <summary>A failure handler (<c>condition: failed()</c>) that undoes the deployment; a failure
    /// handler that only notifies never counts.</summary>
    public static bool IsRollback(PipelineStageDefinition stage) => IsFailureHandler(stage) && UndoesDeployment(stage);

    /// <summary>The stage undoes the deployment, whatever its condition.</summary>
    public static bool UndoesDeployment(PipelineStageDefinition stage) =>
        RunsStep(stage, "bluegreen-rollback") || HasRole(stage, PipelineDeploymentRoles.Rollback);

    /// <summary>The names of the stages that start the new colour (a <c>bluegreen-up</c> step, or
    /// <c>deployment_role: start</c>).</summary>
    public static List<string> StartNames(PipelineYamlDefinition definition) =>
        [.. YamlParsingHelper.FlattenJobs(definition).Where(IsStart).Select(stage => stage.Name)];

    /// <summary>The names of the failure handlers that undo the deployment (<see cref="IsRollback"/>): recognised by
    /// what they do rather than by a name, so a failure handler that only notifies never turns a failed run into a
    /// rolled-back one.</summary>
    public static List<string> RollbackNames(PipelineYamlDefinition definition) =>
        [.. YamlParsingHelper.FlattenJobs(definition).Where(IsRollback).Select(stage => stage.Name)];

    private static bool IsFailureHandler(PipelineStageDefinition stage) =>
        string.Equals(stage.Condition?.Trim(), "failed()", StringComparison.OrdinalIgnoreCase);

    private static bool RunsStep(PipelineStageDefinition stage, string stepType) =>
        stage.Steps.Any(step => string.Equals(step.Type, stepType, StringComparison.OrdinalIgnoreCase));

    private static bool HasRole(PipelineStageDefinition stage, string role) =>
        string.Equals(stage.DeploymentRole?.Trim(), role, StringComparison.OrdinalIgnoreCase);
}
