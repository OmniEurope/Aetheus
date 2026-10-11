// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

internal static class PipelineHelper
{
    internal static OmniTone GetRunBadge(PipelineStatus status) => status switch
    {
        PipelineStatus.Success => OmniTone.Success,
        PipelineStatus.Failed => OmniTone.Danger,
        PipelineStatus.Running => OmniTone.Accent,
        // D13: amber now means "finished with a swallowed failure", so Cancelled is not amber. Recette
        // R-347: the neutral grey barely stood out from the table in the dark theme; Cancelled takes the
        // info blue, the one variant no other run status uses.
        PipelineStatus.Partial => OmniTone.Warning,
        // R-14: the deployment did not happen and the previous version serves again: a warning, not
        // the red of a run that left something broken. The label, not the colour, tells it from Partial.
        PipelineStatus.RolledBack => OmniTone.Warning,
        // Recette R2-146 (user's choice, as a trial): a refused result is a warning, not the red of a
        // technical failure.
        PipelineStatus.Rejected => OmniTone.Warning,
        PipelineStatus.Cancelled => OmniTone.Info,
        _ => OmniTone.Neutral
    };

    /// <summary>Colour for an analysis gate letter: A and B read as healthy, C as a warning, D and
    /// below as a problem. Same scale the gate overview uses, so the compact badge in a run table and
    /// the detailed view never tell two different stories.</summary>
    internal static OmniTone GetGradeBadge(AnalysisGrade grade) => grade switch
    {
        AnalysisGrade.A or AnalysisGrade.B => OmniTone.Success,
        AnalysisGrade.C => OmniTone.Warning,
        _ => OmniTone.Danger
    };

    /// <summary>M: the step a non-terminal run is currently on, as "Stage · Step" (the running step,
    /// else the next pending non-system step). Null for terminal runs (the status badge conveys the
    /// outcome) or when no step data is loaded. The "System:" stage prefix is stripped for display.</summary>
    internal static string? GetCurrentStepLabel(PipelineRunDto run)
    {
        if (run.Status is not (PipelineStatus.Running or PipelineStatus.Pending or PipelineStatus.WaitingForApproval)) return null;

        var step = run.Steps.FirstOrDefault(s => s.Status == TaskExecutionStatus.Running)
                   ?? run.Steps.FirstOrDefault(s => s.Status == TaskExecutionStatus.Assigned)
                   ?? run.Steps.FirstOrDefault(s => s.Status == TaskExecutionStatus.Pending && !s.IsSystem);
        if (step is null) return null;

        var stage = step.StageName.StartsWith("System:", StringComparison.Ordinal)
            ? step.StageName["System:".Length..]
            : step.StageName;
        return $"{stage} · {step.StepName}";
    }

    internal static string GetTriggerIcon(PipelineTriggerType type) => type switch
    {
        PipelineTriggerType.Webhook => "webhook",
        PipelineTriggerType.Schedule => "schedule",
        _ => "touch_app"
    };
}
