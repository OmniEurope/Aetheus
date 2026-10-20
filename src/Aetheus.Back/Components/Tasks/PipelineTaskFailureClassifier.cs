// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Tasks;

internal static class PipelineTaskFailureClassifier
{
    private static readonly Regex TestName =
        new(@"(?:^|[^a-z0-9])(?:test|tests|e2e|exercise)(?:$|[^a-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BuildName =
        new(@"(?:^|[^a-z0-9])(?:build|compile|package)(?:$|[^a-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static void ApplyReported(
        ServerTask task,
        TaskResultDto result,
        DateTime completedAt)
    {
        if (string.IsNullOrWhiteSpace(result.FailureCode))
            return;

        task.FailureCode = result.FailureCode;
        task.FailureReason = result.FailureReason;
        AddIncidentLog(task, completedAt);
    }

    internal static void ApplyIfMissing(
        ServerTask task,
        PipelineStepRun? stepRun,
        TaskResultDto result,
        DateTime completedAt)
    {
        if (!string.IsNullOrWhiteSpace(task.FailureCode)
            || result.Status is not (TaskExecutionStatus.Failed or TaskExecutionStatus.Timeout))
            return;
        // Create pipeline incidents only for failed pipeline steps. Manual server actions expose
        // their failure through task status, exit code and logs.
        if (stepRun is null && task.PipelineRunId is null)
            return;

        task.FailureCode = Infer(task.Name, stepRun?.GroupName);
        task.FailureReason =
            $"Pipeline step '{stepRun?.StageName ?? "unknown"}/{stepRun?.StepName ?? task.Name}' " +
            $"finished with status {result.Status} and exit code {result.ExitCode}.";
        AddIncidentLog(task, completedAt);
    }

    internal static void AddIncidentLog(ServerTask task, DateTime completedAt)
    {
        task.Logs.Add(new TaskLog
        {
            TaskId = task.Id,
            Task = task,
            Level = TaskLogLevel.Error,
            Message = $"[incident:{task.FailureCode}] {task.FailureReason}",
            Timestamp = completedAt
        });
    }

    internal static string Infer(string taskName, string? groupName)
    {
        if (TestName.IsMatch(taskName))
            return TaskFailureCodes.TestsFailed;
        if (BuildName.IsMatch(taskName))
            return TaskFailureCodes.BuildFailed;
        if (string.Equals(groupName, "Test", StringComparison.OrdinalIgnoreCase))
            return TaskFailureCodes.TestsFailed;
        if (string.Equals(groupName, "Build", StringComparison.OrdinalIgnoreCase))
            return TaskFailureCodes.BuildFailed;
        return TaskFailureCodes.ToolError;
    }
}
