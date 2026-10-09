// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Logs;

public class LogRepository(AppDbContext db) : ILogRepository
{
    // Hard upper bound for the "no maxLines" read so a very verbose task (a build/deploy with tens of
    // thousands of lines) can never materialise the entire TaskLogs slice into memory. Keeps the most
    // recent lines, mirroring the bounded path's semantics.
    private const int MaxUnboundedTaskLogLines = 50_000;

    public async Task<List<TaskLog>> GetTaskLogsAsync(int taskId, int? maxLines = null, CancellationToken ct = default)
    {
        var query = db.TaskLogs
            .AsNoTracking()
            .Where(l => l.TaskId == taskId);

        var cap = maxLines is > 0 ? maxLines.Value : MaxUnboundedTaskLogLines;
        return await query
            .OrderByDescending(l => l.Timestamp)
            .ThenByDescending(l => l.Id)
            .Take(cap)
            .OrderBy(l => l.Timestamp)
            .ThenBy(l => l.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<(List<TaskLog> Items, int Total)> GetTaskLogPageAsync(
        int taskId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.TaskLogs.AsNoTracking().Where(l => l.TaskId == taskId);
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        // Timestamp, then Id: lines written in one batch share a timestamp, and the id keeps their order
        // stable from one page to the next.
        var items = await query
            .OrderBy(l => l.Timestamp)
            .ThenBy(l => l.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return (items, total);
    }

    public async Task<List<string>> GetTaskOutputVariableLinesAsync(int taskId, CancellationToken ct = default)
    {
        // SQL-side filter (LIKE '%##aetheus[%'): CompleteTaskAsync only needs the setvariable
        // marker lines, not the up-to-50k-line log slice of a verbose build. Uses Contains, not
        // StartsWith: OutputVariablePattern has no leading '^' anchor, so it matches a marker
        // appearing mid-line (e.g. `echo "done: ##aetheus[setvariable name=X]v"`) - a
        // StartsWith/column-0 filter would silently drop those user-authored markers.
        return await db.TaskLogs
            .AsNoTracking()
            .Where(l => l.TaskId == taskId && l.Message.Contains("##aetheus["))
            .OrderBy(l => l.Timestamp)
            .Select(l => l.Message)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<int?> GetPipelineRunIdForTaskAsync(int taskId, CancellationToken ct = default)
    {
        return await db.Tasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.PipelineRunId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<TaskLog> AddLogAsync(TaskLog log, CancellationToken ct = default)
    {
        db.TaskLogs.Add(log);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return log;
    }

    public async Task AddLogsAsync(List<TaskLog> logs, CancellationToken ct = default)
    {
        db.TaskLogs.AddRange(logs);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteLogsOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        // Relational (prod): set-based delete so we never materialise every expired
        // TaskLog - on a fleet with 6+ months of logs, load + RemoveRange is OOM-latent.
        // InMemory (unit tests) does not support ExecuteDelete, so fall back there.
        if (db.Database.IsRelational())
            return await db.TaskLogs.Where(l => l.Timestamp < cutoff).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var old = await db.TaskLogs.Where(l => l.Timestamp < cutoff).ToListAsync(ct).ConfigureAwait(false);
        db.TaskLogs.RemoveRange(old);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return old.Count;
    }
}
