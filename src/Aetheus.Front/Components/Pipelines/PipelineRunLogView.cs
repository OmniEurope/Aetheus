// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Log-pane presentation helpers for the run view: variable highlighting, search filtering, error-line
/// extraction, failed-step log preloading, and the "why is this pane empty / pending" reason strings.
/// Extracted from <c>PipelineRun.razor.cs</c> as a real collaborator so that file stays under the size
/// budget; the markup imports it via <c>@using static</c> so call sites stay unqualified.
/// </summary>
internal static class PipelineRunLogView
{
    // S-DES-11: regex over the run's known variable names so they can be accented inside log lines.
    // Built once per load; names shorter than 3 chars are skipped to avoid noisy false matches.
    public static Regex? BuildLogVarRegex(PipelineRunDto? run)
    {
        var names = run?.ResolvedVariables.Keys.Where(k => k.Length >= 3).ToList() ?? [];
        return names.Count == 0
            ? null
            : new Regex($@"\b(?:{string.Join("|", names.Select(Regex.Escape))})\b", RegexOptions.Compiled);
    }

    // HTML-encodes the raw log line, then wraps occurrences of known variable names in an accent span.
    // Encoding first guarantees the injected markup is the only HTML in the output (no log-content XSS).
    public static MarkupString HighlightLogVariables(string message, Regex? logVarRegex)
    {
        var encoded = System.Net.WebUtility.HtmlEncode(message);
        if (logVarRegex is null) return (MarkupString)encoded;
        return (MarkupString)logVarRegex.Replace(encoded, m => $"<span class=\"log-var\">{m.Value}</span>");
    }

    // S-UX-27: filter the selected step's log lines by the search term, preserving original line numbers.
    public static List<(TaskLogDto Log, int Num)> FilterLogs(List<TaskLogDto> logs, string search)
    {
        var result = new List<(TaskLogDto, int)>(logs.Count);
        for (var i = 0; i < logs.Count; i++)
        {
            if (string.IsNullOrEmpty(search) ||
                logs[i].Message.Contains(search, StringComparison.OrdinalIgnoreCase))
                result.Add((logs[i], i + 1));
        }
        return result;
    }

    // The error lines for a failed step (error-level lines, else the tail of the log) for the inline preview.
    private const int OverviewErrorLineLimit = 12;

    public static IReadOnlyList<TaskLogDto> ErrorLinesFor(
        PipelineStepRunDto step, IReadOnlyDictionary<int, List<TaskLogDto>> stepLogsCache)
    {
        if (step.TaskId is not { } taskId || !stepLogsCache.TryGetValue(taskId, out var logs) || logs.Count == 0)
            return [];
        var errors = logs.Where(l => l.Level == TaskLogLevel.Error).ToList();
        var source = errors.Count > 0 ? errors : logs;
        return source.Count > OverviewErrorLineLimit ? source.GetRange(source.Count - OverviewErrorLineLimit, OverviewErrorLineLimit) : source;
    }

    // Preload the logs for every failed step so the Overview error card can render them without a click.
    public static async Task LoadFailedStepLogsAsync(
        IReadOnlyList<PipelineStepRunDto> failedSteps, ApiClient api, PipelineRunLogWindow logs)
    {
        foreach (var step in failedSteps)
        {
            // Recette R2-104: lines streamed while the step ran are not its history; it is read all the same.
            if (step.TaskId is not { } taskId || logs.HasSnapshot(taskId)) continue;
            try { logs.ApplySnapshot(taskId, await api.Monitoring.GetTaskLogsAsync(taskId) ?? [], requested: null); }
            catch (HttpRequestException) { logs.Lines[taskId] = []; }
        }
    }

    // N8FJ: an empty log panel has three distinct causes - spell out which one instead of a flat "No logs".
    public static string GetEmptyLogsReason(PipelineStepRunDto step, IStringLocalizer<AppStrings> l)
    {
        if (!string.IsNullOrWhiteSpace(step.FailureReason))
            return l["LogsFailureDiagnostic",
                string.IsNullOrWhiteSpace(step.FailureCode) ? "Unclassified" : step.FailureCode,
                step.FailureReason];

        if (step.TaskId.HasValue)
        {
            var exitCode = step.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?";
            return step.Status switch
            {
                TaskExecutionStatus.Success => l["LogsSuccessfulNoOutput", exitCode],
                TaskExecutionStatus.Failed => l["LogsFailedNoOutput", exitCode],
                TaskExecutionStatus.Timeout => l["LogsTimeoutNoOutput"],
                TaskExecutionStatus.Cancelled => l["LogsCancelledNoOutput"],
                _ => l["LogsAwaitingOutput"]
            };
        }

        if (step.Status == TaskExecutionStatus.Cancelled &&
            !string.IsNullOrWhiteSpace(step.SkippedCondition))
        {
            var values = step.SkippedConditionVariables.Count == 0
                ? l["ConditionHadNoVariables"].Value
                : string.Join(", ", step.SkippedConditionVariables
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"{item.Key} = '{item.Value}'"));
            return l["LogsConditionNotMet", step.SkippedCondition, values];
        }

        return step.Status switch
        {
            // Reached a run state but no task/logs were ever recorded - they were not retained.
            TaskExecutionStatus.Success or TaskExecutionStatus.Failed or TaskExecutionStatus.Timeout
                => l["LogsNotRetained"],
            // Never executed (cancelled / never assigned) - there is nothing to show.
            _ => l["LogsNeverProduced"],
        };
    }

    public static string GetQueueReason(
        PipelineStepRunDto step,
        IStringLocalizer<AppStrings> l)
    {
        var runner = string.IsNullOrWhiteSpace(step.ServerName)
            ? l["Server"].Value
            : step.ServerName;
        return step.QueuePosition is { } position && step.QueueDepth is { } depth
            ? l["TaskQueuePositionLive", runner, position, depth]
            : l["TaskQueuePositionPending", runner];
    }

    public static string GetPendingReason(
        PipelineStepRunDto step, List<StageViewModel> stages, PipelineRunDto? run,
        IStringLocalizer<AppStrings> l)
    {
        if (step.IsSystem)
            return step.StageName.Contains("Cleanup") ? l["WaitingForPreviousStage"] : l["WaitingForAgent"];

        var stageIndex = stages.FindIndex(s => s.Name == step.StageName);
        if (stageIndex > 0)
        {
            var prevStage = stages[stageIndex - 1];
            if (prevStage.Status is TaskExecutionStatus.Pending or TaskExecutionStatus.Running)
                return l["WaitingForPreviousStage"];
        }

        if (step.ServerName is null)
            return l["WaitingForAgent"];

        var pendingApproval = run?.Approvals.Any(approval =>
            approval.Status == ApprovalStatus.Pending &&
            string.Equals(approval.StageName, step.StageName, StringComparison.Ordinal)) == true;
        var legacyApprovalState = run is { Status: PipelineStatus.WaitingForApproval, Approvals.Count: 0 };
        return pendingApproval || legacyApprovalState
            ? l["WaitingForApproval"]
            : l["WaitingForExecution"];
    }
}
