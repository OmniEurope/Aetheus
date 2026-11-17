// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R-31: why a step that did not succeed stopped. A "Délai dépassé" step used to show nothing at
/// all; the back stores a failure code and a raw reason for the steps it can explain, and this turns them
/// into a sentence in the user's language. A code the front knows is said in plain words; the raw detail is
/// never the headline, it is kept folded under it for whoever wants the exact text.
/// </summary>
internal static class PipelineStepStopReason
{
    /// <param name="Severity">The alert colour: red for a failure, orange for a timeout, blue for a cancellation.</param>
    /// <param name="Headline">The sentence that says why, localized.</param>
    /// <param name="Detail">The raw reason the back recorded, shown folded; null when there is none.</param>
    internal sealed record View(OmniSeverity Severity, string Headline, string? Detail);

    /// <summary>A step stopped for a reason worth saying when it failed, timed out or was cancelled.</summary>
    public static bool Applies(PipelineStepRunDto step) =>
        step.Status is TaskExecutionStatus.Failed or TaskExecutionStatus.Timeout or TaskExecutionStatus.Cancelled;

    /// <summary>
    /// R-21: the real error of a step, which the classified headline does not carry ("compilation failed" says
    /// nothing of <c>NETSDK1045</c>): the last line logged at Error level, else the last line that says "error".
    /// Null when the log holds neither, or is not loaded.
    /// </summary>
    public static string? LastErrorLine(PipelineStepRunDto step, IReadOnlyDictionary<int, List<TaskLogDto>> logs)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(logs);
        if (!Applies(step) || step.TaskId is not { } taskId || !logs.TryGetValue(taskId, out var lines)) return null;
        var line = lines.LastOrDefault(l => l.Level == TaskLogLevel.Error && !string.IsNullOrWhiteSpace(l.Message))
                   ?? lines.LastOrDefault(l => l.Message.Contains("error", StringComparison.OrdinalIgnoreCase));
        return line is null ? null : OmniAnsi.Strip(line.Message).Trim();
    }

    public static View? For(PipelineStepRunDto step, IStringLocalizer<AppStrings> l)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(l);
        if (!Applies(step)) return null;

        var detail = string.IsNullOrWhiteSpace(step.FailureReason) ? null : step.FailureReason.Trim();
        var severity = step.Status switch
        {
            TaskExecutionStatus.Failed => OmniSeverity.Danger,
            TaskExecutionStatus.Timeout => OmniSeverity.Warning,
            _ => OmniSeverity.Info
        };
        // A skipped step has its condition and values shown with its logs: it needs no raw detail.
        if (step.Status == TaskExecutionStatus.Cancelled && !string.IsNullOrWhiteSpace(step.SkippedCondition))
            return new View(severity, l["StepStopConditionNotMet"].Value, null);

        return new View(severity, Headline(step, l), detail);
    }

    private static string Headline(PipelineStepRunDto step, IStringLocalizer<AppStrings> l) =>
        step.FailureCode switch
        {
            TaskFailureCodes.AgentSessionReplaced => l["StepStopAgentSessionReplaced"].Value,
            TaskFailureCodes.InfrastructureMismatch => l["StepStopInfrastructureMismatch"].Value,
            TaskFailureCodes.ConfigurationIncomplete => l["StepStopConfigurationIncomplete"].Value,
            TaskFailureCodes.ToolError => l["StepStopToolError"].Value,
            TaskFailureCodes.BuildFailed => l["StepStopBuildFailed"].Value,
            TaskFailureCodes.TestsFailed => l["StepStopTestsFailed"].Value,
            TaskFailureCodes.AgentIncompatible => l["StepStopAgentIncompatible"].Value,
            _ => step.Status switch
            {
                TaskExecutionStatus.Timeout => l["StepStopTimeout"].Value,
                TaskExecutionStatus.Cancelled => l["StepStopCancelled"].Value,
                _ => step.ExitCode is { } exitCode
                    ? string.Format(l["StepStopFailedExit"].Value, exitCode)
                    : l["StepStopFailed"].Value
            }
        };
}
