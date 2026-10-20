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

    /// <summary>The run opened on its Gate tab, where its findings are listed.</summary>
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

    /// <summary>Cancellation attribution distinguishes a named operator from system cancellation;
    /// unknown authors remain unspecified.</summary>
    public static string? CancelledByText(PipelineRunDto run, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(localizer);
        if (string.IsNullOrWhiteSpace(run.CancelledBy)) return null;
        return string.Equals(run.CancelledBy, "system", StringComparison.Ordinal)
            ? localizer["RunCancelledBySystem"]
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, localizer["RunCancelledBy"], run.CancelledBy);
    }

    /// <summary>The frozen ID column accounts for the longest ID, an available row action and cell
    /// padding. Explicit content-based sizing avoids equal-share allocation under fixed table
    /// layout.</summary>
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
        // The total is every declared step, including those a terminal run never reached.
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
