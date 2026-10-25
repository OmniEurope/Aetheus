// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Why a run ended Failed or Rejected, in the words the engine recorded (recette R1-9): the result mail
/// and the notification payload carry it so the reader does not have to open the run to learn the cause.
/// A cause the engine did not record stays null: nothing is invented.
/// </summary>
/// <param name="Cause">The recorded reason, a failure code, an exit code, or the refusing outputs.</param>
/// <param name="CauseStep">The step(s) it concerns, as <c>Stage / Step</c>.</param>
internal sealed record PipelineRunOutcomeCause(string? Cause, string? CauseStep)
{
    public const int MaxCauseLength = 400;
    private const int MaxSteps = 3;

    public static PipelineRunOutcomeCause None { get; } = new(null, null);

    /// <summary>A Failed run: the first blocking failed step's reason, and the failed steps (at most three).</summary>
    public static PipelineRunOutcomeCause ForFailure(IReadOnlyList<StepFailureProjection> failedSteps)
    {
        ArgumentNullException.ThrowIfNull(failedSteps);
        if (failedSteps.Count == 0) return None;
        var cause = failedSteps.Select(Describe).FirstOrDefault(text => text is not null);
        return new PipelineRunOutcomeCause(
            cause is null ? null : Clip(cause),
            string.Join(", ", failedSteps.Take(MaxSteps).Select(StepName)));
    }

    /// <summary>
    /// A Rejected run: the outputs that refused the result (<see cref="PipelineControlVerdict.Refusals"/>).
    /// Read from every successful step's outputs, without the pipeline definition: a refusing output published by a
    /// step the definition marks advisory would be listed too. The gate variables are the seal's own.
    /// </summary>
    public static PipelineRunOutcomeCause ForRejection(IEnumerable<StepOutputProjection> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var refusing = outputs
            .Select(output => (output, refusals: PipelineControlVerdict
                .Refusals(PipelineRunHelpers.DeserializeResolvedVariables(output.OutputVariablesJson)).ToList()))
            .Where(item => item.refusals.Count > 0)
            .ToList();
        if (refusing.Count == 0) return None;
        var cause = string.Join(", ", refusing.SelectMany(item => item.refusals).Select(pair => $"{pair.Key}={pair.Value}").Distinct());
        var steps = string.Join(", ", refusing.Take(MaxSteps).Select(item => $"{item.output.StageName} / {item.output.StepName}"));
        return new PipelineRunOutcomeCause(Clip(cause), steps);
    }

    private static string StepName(StepFailureProjection step) =>
        $"{step.StageName} / {step.StepName}" + (string.IsNullOrEmpty(step.MatrixLeg) ? string.Empty : $" ({step.MatrixLeg})");

    // The task's own reason first, with its code; a code alone; an exit code alone; else nothing was recorded.
    private static string? Describe(StepFailureProjection step)
    {
        var reason = step.FailureReason?.Trim();
        var code = step.FailureCode?.Trim();
        if (!string.IsNullOrEmpty(reason)) return string.IsNullOrEmpty(code) ? reason : $"{reason} ({code})";
        if (!string.IsNullOrEmpty(code)) return code;
        return step.ExitCode is { } exit ? string.Create(CultureInfo.InvariantCulture, $"exit code {exit}") : null;
    }

    private static string Clip(string text) => text.Length <= MaxCauseLength ? text : text[..MaxCauseLength];
}
