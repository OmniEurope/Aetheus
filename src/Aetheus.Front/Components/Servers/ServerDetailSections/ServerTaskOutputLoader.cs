// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// PLAN-005, recette R3-30/R3-31/R3-34: reads the full output of a completed server task through the
/// task-log API. The <c>TaskCompleted</c> SignalR notification never carries the output, so the server
/// sections fetch it once the task they queued has finished.
/// </summary>
public static class ServerTaskOutputLoader
{
    public static async Task<string> ReadAsync(ApiClient api, int taskId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        var logs = await api.Monitoring.GetTaskLogsAsync(taskId, ct);
        return string.Join('\n', logs.OrderBy(l => l.Id).Select(l => l.Message));
    }

    /// <summary>The full output, or an empty one when the log could not be read: the caller then reports the
    /// task as unfinished rather than failing on the read.</summary>
    public static async Task<string> ReadOrEmptyAsync(ApiClient api, int taskId, CancellationToken ct = default)
    {
        try { return await ReadAsync(api, taskId, ct); }
        catch (HttpRequestException) { return string.Empty; }
    }

    /// <summary>Recette R3-44: the last <paramref name="lines"/> non-blank lines of a task's output (a service
    /// that refused to start: the agent appends its status and its configuration check), or null when it
    /// printed nothing.</summary>
    public static async Task<string?> TailAsync(ApiClient api, int taskId, int lines, CancellationToken ct = default)
    {
        var output = await ReadAsync(api, taskId, ct);
        var tail = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0)
            .TakeLast(Math.Max(1, lines))
            .ToList();
        return tail.Count == 0 ? null : string.Join('\n', tail);
    }

    /// <summary>The last non-blank line of a task's output, or null when it printed nothing.</summary>
    public static async Task<string?> LastLineAsync(ApiClient api, int taskId, CancellationToken ct = default)
    {
        var output = await ReadAsync(api, taskId, ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
    }
}
