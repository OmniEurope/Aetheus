// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

internal static class PipelineRunPresentation
{
    public static string EditBackHref(int? projectId, int? serverId) => serverId is { } server
        ? $"/servers/{server}/pipelines"
        : projectId is { } project ? $"/projects/{project}/pipelines" : "/pipelines";

    public static string Href(int runId, int? projectId, int? serverId) => serverId is { } server
        ? $"/pipelines/runs/{runId}?serverId={server}"
        : projectId is { } project ? $"/pipelines/runs/{runId}?projectId={project}" : $"/pipelines/runs/{runId}";

    /// <summary>Recette R2-128: the run opened on its Gate tab, where its findings are listed.</summary>
    public static string GateHref(int runId, int? projectId, int? serverId)
    {
        var href = Href(runId, projectId, serverId);
        return href + (href.Contains('?', StringComparison.Ordinal) ? "&tab=gate" : "?tab=gate");
    }

    public static string? CurrentStep(PipelineRunDto run) => PipelineHelper.GetCurrentStepLabel(run);
    public static bool IsFailed(PipelineRunDto run) => run.Status == PipelineStatus.Failed;

    /// <summary>A run can be cancelled while it is still going anywhere. Once the cancellation has been
    /// durably accepted the run keeps its mandatory teardown, so asking again would change nothing.</summary>
    public static bool CanCancel(PipelineRunDto run) =>
        !run.CancellationRequested
        && run.Status is PipelineStatus.Pending or PipelineStatus.Running or PipelineStatus.WaitingForApproval;

    /// <summary>Recette R2-103: "Cancelled by alice", or "Cancelled by Aetheus" for a cancellation the
    /// system made (latest-wins replacement, recovery); null when the author is unknown (a run cancelled
    /// before it was recorded).</summary>
    public static string? CancelledByText(PipelineRunDto run, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(localizer);
        if (string.IsNullOrWhiteSpace(run.CancelledBy)) return null;
        return string.Equals(run.CancelledBy, "system", StringComparison.Ordinal)
            ? localizer["RunCancelledBySystem"]
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, localizer["RunCancelledBy"], run.CancelledBy);
    }

    /// <summary>
    /// Recette R-329: the width of the frozen ID column of a pipeline's runs, sized to what it holds:
    /// "#" and the digits of the longest id, plus one row action (cancel a live run, rerun a failed one;
    /// never both) when a listed run offers one, plus the cell padding. A fixed width (150px, then
    /// 112px) wasted room beside the scrolled columns, and "auto" is no better: under the grid's fixed
    /// table layout an auto column takes an equal share of what the others leave, not its content.
    /// Digits count 1ch each (the grid's figures are tabular); two more cover the "#" and the link's
    /// medium weight, never below the link's own 2.5rem floor (<c>.pipeline-name-link</c>), so the id
    /// never wraps (it may break anywhere on a phone).
    /// </summary>
    public static string IdColumnWidth(IEnumerable<PipelineRunDto> runs)
    {
        var digits = 1;
        var hasAction = false;
        foreach (var run in runs)
        {
            digits = Math.Max(digits, run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
            hasAction |= CanCancel(run) || IsFailed(run);
        }

        var text = FormattableString.Invariant($"max({digits + 2}ch, 2.5rem)");
        return hasAction
            ? $"calc({text} + var(--omni-badge-height) + var(--omni-space-xs) + 2 * var(--omni-space-md))"
            : $"calc({text} + 2 * var(--omni-space-md))";
    }

    public static bool IsConfigFailure(PipelineRunDto run) =>
        run.Status == PipelineStatus.Failed &&
        (run.Steps.Count == 0 || run.Steps.Any(step =>
            step.Status == TaskExecutionStatus.Failed && step.TaskId is null));
    public static bool IsExecutionFailure(PipelineRunDto run) =>
        run.Status == PipelineStatus.Failed && !IsConfigFailure(run);
    public static bool IsWindows(string? os) => os?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true;
    public static OmniTone Badge(PipelineStatus status) => PipelineHelper.GetRunBadge(status);

    public static string? GitBranch(PipelineRunDto? run) =>
        run?.ResolvedVariables.GetValueOrDefault("BUILD_SOURCEBRANCH")
        ?? run?.ResolvedVariables.GetValueOrDefault("DEFAULT_BRANCH");

    public static IEnumerable<PipelineStepRunDto> AllSteps(
        IEnumerable<StageViewModel> stages,
        IReadOnlyDictionary<int, PipelineRunDto> children) =>
        stages.SelectMany(stage => stage.Steps)
            .Concat(children.Values.SelectMany(child => child.Steps));

    public static IReadOnlyCollection<int> LiveChildRunIds(
        IReadOnlyDictionary<int, PipelineRunDto> children) =>
        children.Values
            .Where(child => !PipelineRunFormatting.IsTerminal(child.Status))
            .Select(child => child.Id)
            .ToList();

    public static IReadOnlyList<PipelineStepRunDto> LintSteps(
        PipelineRunDto? run,
        IReadOnlyDictionary<int, PipelineRunDto> children) =>
        (run?.Steps ?? Enumerable.Empty<PipelineStepRunDto>())
            .Concat(children.Values.SelectMany(child => child.Steps))
            .Where(step => !step.IsSystem
                && (step.StepName.Contains("lint", StringComparison.OrdinalIgnoreCase)
                    || step.StageName.Contains("lint", StringComparison.OrdinalIgnoreCase)))
            .ToList();

    public static List<string> TabSlugs(PipelineRunDto? run, bool hasGateResult, bool hasYamlTab, bool hasLintTab)
    {
        var slugs = new List<string> { "overview" };
        slugs.Add("logs");
        if (hasGateResult) slugs.Add("gate");
        if (hasYamlTab) slugs.Add("yaml");
        if (run?.Artifacts.Count > 0) slugs.Add("artifacts");
        if (PipelineRunMetrics.HasCoverage(run)) slugs.Add("coverage");
        if (run?.TestResultSummary is not null) slugs.Add("tests");
        if (hasLintTab) slugs.Add("lint");
        if (PipelineRunMetrics.HasComplexity(run)) slugs.Add("quality");
        return slugs;
    }

    public static (int Completed, int Total) StepProgress(PipelineRunDto run)
    {
        var steps = run.Steps.Where(step => !step.IsSystem).ToList();
        var completed = steps.Count(step => step.Status is TaskExecutionStatus.Success
            or TaskExecutionStatus.Failed or TaskExecutionStatus.Timeout or TaskExecutionStatus.Cancelled);
        // Recette R-336: the total is every declared step, including those a terminal run never reached.
        return (completed, steps.Count);
    }

    public static string NextConnectorStatus(IReadOnlyList<StageViewModel> stages, int stageIndex)
    {
        if (stageIndex + 1 >= stages.Count) return "pending";
        var current = stages[stageIndex];
        var next = stages[stageIndex + 1];
        if (current.Status == TaskExecutionStatus.Success && next.Status == TaskExecutionStatus.Failed)
            return "success-failed";
        if (current.Status == TaskExecutionStatus.Success && next.Status != TaskExecutionStatus.Pending)
            return next.Status.ToString().ToLowerInvariant();
        return current.Status.ToString().ToLowerInvariant();
    }

    public static string FormatDuration(DateTime started, DateTime? completed)
    {
        if (completed is null) return "\u2014";
        var duration = completed.Value - started;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        return duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
            : $"{duration.Seconds}s";
    }
}
