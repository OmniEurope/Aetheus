// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Analysis;

/// <summary>What became of a task that was to publish a report the run does not hold.</summary>
internal sealed record ProducerTaskState(
    int TaskId,
    TaskExecutionStatus Status,
    int? ExitCode,
    string? FailureCode,
    string? FailureReason,
    IReadOnlyList<string> LastErrors);

/// <summary>Recette R-21: a gate in Error because a producer published nothing said only "the task published no report".
/// The task's own end (status, exit code, failure code and reason) and its last error lines (the coverage that is empty
/// or malformed, the SARIF files not found, the commit that does not match the workspace) are what name the reason.</summary>
internal static class AnalysisGateProducerStates
{
    private const int MaxErrorLines = 3;
    private const int MaxLineLength = 300;
    private const string IncidentPrefix = "[incident:";

    /// <summary>One description per task id; a task that no longer exists has none.</summary>
    public static async Task<IReadOnlyDictionary<int, string>> DescribeAsync(
        AppDbContext db, IEnumerable<int> taskIds, CancellationToken ct)
    {
        var ids = taskIds.Distinct().ToList();
        var details = new Dictionary<int, string>();
        if (ids.Count == 0) return details;
        var tasks = await db.Tasks.AsNoTracking()
            .Where(task => ids.Contains(task.Id))
            .Select(task => new { task.Id, task.Status, task.ExitCode, task.FailureCode, task.FailureReason })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var task in tasks)
        {
            // The terminal incident line only repeats the failure reason: the lines that name the cause come before it.
            var errors = await db.TaskLogs.AsNoTracking()
                .Where(log => log.TaskId == task.Id && log.Level == TaskLogLevel.Error
                    && !log.Message.StartsWith(IncidentPrefix))
                .OrderByDescending(log => log.Id)
                .Select(log => log.Message)
                .Take(MaxErrorLines)
                .ToListAsync(ct).ConfigureAwait(false);
            errors.Reverse();
            details[task.Id] = Describe(new ProducerTaskState(
                task.Id, task.Status, task.ExitCode, task.FailureCode, task.FailureReason, errors));
        }
        return details;
    }

    public static string Describe(ProducerTaskState state)
    {
        var text = $"tâche #{state.TaskId.ToString(CultureInfo.InvariantCulture)} {StatusText(state.Status)}";
        if (state.ExitCode is { } exit) text += $", code de sortie {exit.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(state.FailureCode) || !string.IsNullOrWhiteSpace(state.FailureReason))
            text += $", échec {Cut(string.Join(" : ", new[] { state.FailureCode, state.FailureReason }.Where(part => !string.IsNullOrWhiteSpace(part))))}";
        var errors = state.LastErrors.Where(line => !string.IsNullOrWhiteSpace(line)).Select(Cut).ToList();
        return errors.Count == 0
            ? text
            : $"{text.TrimEnd('.')}. Dernières erreurs de son journal : {string.Join(" | ", errors)}";
    }

    private static string StatusText(TaskExecutionStatus status) => status switch
    {
        TaskExecutionStatus.Pending => "jamais démarrée (en attente)",
        TaskExecutionStatus.Assigned => "assignée à un agent, jamais démarrée",
        TaskExecutionStatus.Running => "encore en cours",
        TaskExecutionStatus.Success => "terminée avec succès, mais sans publier de rapport",
        TaskExecutionStatus.Failed => "en échec",
        TaskExecutionStatus.Timeout => "arrêtée par son délai",
        TaskExecutionStatus.Cancelled => "annulée",
        _ => status.ToString()
    };

    private static string Cut(string? value)
    {
        var line = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= MaxLineLength ? line : line[..MaxLineLength] + "…";
    }
}
