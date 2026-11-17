// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Pipelines;
using Aetheus.Shared.Components.Tasks;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The step of a child run that stopped it (Failed, Timeout or Cancelled), with the task's own failure fields first.</summary>
public sealed record StepStopProjection(
    string StageName, string StepName, string? MatrixLeg, TaskExecutionStatus Status, int? ExitCode, string? FailureCode, string? FailureReason);

/// <summary>
/// Recette R-21: what a parent trigger step says when its child run did not complete. The message names the child
/// run (id, pipeline), the first step that stopped it and that step's own failure code and reason, so the reader
/// does not have to open the child to learn why. Cut under the step failure-reason column (2048, PipelineStepRun).
/// </summary>
internal static class PipelineTriggerFailureMessage
{
    /// <summary>The <c>PipelineStepRun.FailureReason</c> column length (PipelineConfigurations).</summary>
    public const int MaxLength = 2048;

    /// <summary>Reads the child run's pipeline and stopped step, then builds the reason. A read failing here never
    /// changes the outcome: the parent still fails, with the bare child status.</summary>
    public static async Task<string> DescribeAsync(
        IPipelineRepository repo, ILogger logger, int? childRunId, PipelineStatus childStatus, CancellationToken ct)
    {
        string? pipelineName = null;
        StepStopProjection? stopped = null;
        if (childRunId is { } id)
        {
            try
            {
                pipelineName = (await repo.GetPipelineRunWithPipelineAsync(id, ct).ConfigureAwait(false))?.Pipeline?.Name;
                stopped = await repo.GetFirstStoppedStepAsync(id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not read the stopped step of child run {ChildRun} for its trigger step", id);
            }
        }

        return Build(childRunId, pipelineName, childStatus, stopped);
    }

    public static string Build(int? childRunId, string? childPipelineName, PipelineStatus childStatus, StepStopProjection? stoppedStep)
    {
        var name = string.IsNullOrWhiteSpace(childPipelineName) ? string.Empty : $" (pipeline {childPipelineName.Trim()})";
        var text = $"Triggered run {childRunId}{name} completed with status {childStatus}.";
        if (stoppedStep is not null)
        {
            var step = $"{stoppedStep.StageName} / {stoppedStep.StepName}"
                + (string.IsNullOrEmpty(stoppedStep.MatrixLeg) ? string.Empty : $" ({stoppedStep.MatrixLeg})");
            var verb = stoppedStep.Status switch
            {
                TaskExecutionStatus.Timeout => "timed out",
                TaskExecutionStatus.Cancelled => "was cancelled",
                _ => "failed"
            };
            text += $" First stopped step: {step} {verb}";
            var code = stoppedStep.FailureCode?.Trim();
            var reason = stoppedStep.FailureReason?.Trim();
            if (!string.IsNullOrEmpty(code)) text += $" [{code}]";
            if (!string.IsNullOrEmpty(reason)) text += $": {reason}";
            else if (stoppedStep.ExitCode is { } exit) text += $": exit code {exit}";
            if (!text.EndsWith('.')) text += ".";
        }

        return text.Length <= MaxLength ? text : text[..(MaxLength - 3)] + "...";
    }
}
