// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Each task retains at most <see cref="LiveWindow"/> streamed lines beyond explicitly
/// loaded history, deduplicated by persisted ID. <see cref="HasSnapshot"/> distinguishes loaded
/// history from a live tail, while <see cref="LoadCompleteAsync"/> retrieves the full log in
/// pages.</summary>
internal sealed class PipelineRunLogWindow
{
    /// <summary>The lines a task keeps in memory beyond what the reader explicitly loaded.</summary>
    internal const int LiveWindow = 5000;

    /// <summary>The page size of the complete-log endpoint (its maximum).</summary>
    internal const int PageSize = 200;

    private readonly HashSet<int> _snapshotRead = [];
    private readonly HashSet<int> _hasEarlier = [];
    private readonly Dictionary<int, int> _limits = [];

    public Dictionary<int, List<TaskLogDto>> Lines { get; } = [];

    /// <summary>True once the task's history was read (a snapshot or its complete log), not just streamed.</summary>
    public bool HasSnapshot(int taskId) => _snapshotRead.Contains(taskId);

    /// <summary>True when lines before the first one held are on the server but not in memory.</summary>
    public bool HasEarlierLines(int taskId) => _hasEarlier.Contains(taskId);

    public void Clear()
    {
        Lines.Clear();
        _snapshotRead.Clear();
        _hasEarlier.Clear();
        _limits.Clear();
    }

    /// <summary>Forgets a task, so its history is read again (a running step selected anew).</summary>
    public void Forget(int taskId)
    {
        Lines.Remove(taskId);
        _snapshotRead.Remove(taskId);
        _hasEarlier.Remove(taskId);
        _limits.Remove(taskId);
    }

    /// <summary>The most lines the unbounded log read returns (the newest ones; LogRepository).</summary>
    private const int ServerReadCap = 50_000;

    /// <summary>
    /// Merges a read of the task's last lines (<paramref name="requested"/> asked, or null for the
    /// unbounded read the result tabs and the failed-step cards make) with what is held, streamed lines
    /// included. Returns whether the held lines changed. An unbounded read is kept whole, like the
    /// complete log.
    /// </summary>
    public bool ApplySnapshot(int taskId, IReadOnlyCollection<TaskLogDto> snapshot, int? requested)
    {
        _snapshotRead.Add(taskId);
        if (snapshot.Count >= (requested ?? ServerReadCap)) _hasEarlier.Add(taskId);
        var held = Lines.GetValueOrDefault(taskId);
        var merged = Union(snapshot, held ?? []);
        if (requested is null) _limits[taskId] = Math.Max(_limits.GetValueOrDefault(taskId, LiveWindow), merged.Count + LiveWindow);
        Store(taskId, merged);
        return held is null || !held.SequenceEqual(Lines[taskId]);
    }

    /// <summary>Appends streamed lines, skipping those already held, and trims the oldest beyond the window.</summary>
    public bool AppendStreamed(int taskId, IEnumerable<TaskLogDto> logs)
    {
        if (!Lines.TryGetValue(taskId, out var list))
        {
            list = [];
            Lines[taskId] = list;
        }
        var known = list.Where(line => line.Id > 0).Select(line => line.Id).ToHashSet();
        var appended = false;
        foreach (var log in logs)
        {
            if (log.Id > 0 && !known.Add(log.Id)) continue;
            list.Add(log);
            appended = true;
        }
        Trim(taskId, list);
        return appended;
    }

    /// <summary>
    /// Reads the task's complete log from its first line, page by page, and keeps it with the lines
    /// streamed meanwhile. The task's window then grows by the complete log, so what was asked for stays.
    /// </summary>
    public async Task LoadCompleteAsync(int taskId, Func<int, Task<PaginatedResult<TaskLogDto>>> fetchPage)
    {
        var complete = new List<TaskLogDto>();
        for (var page = 1; ; page++)
        {
            var result = await fetchPage(page);
            complete.AddRange(result.Items);
            if (result.Items.Count == 0 || complete.Count >= result.TotalCount) break;
        }
        var merged = Union(complete, Lines.GetValueOrDefault(taskId) ?? []);
        _snapshotRead.Add(taskId);
        _hasEarlier.Remove(taskId);
        _limits[taskId] = merged.Count + LiveWindow;
        Store(taskId, merged);
    }

    /// <summary>
    /// Every line of both reads once (by id), in log order: timestamp, then id. Compared in UTC: a line read
    /// over HTTP arrives as local time (JsonOptions.Web), the same line streamed over the hub as UTC.
    /// </summary>
    private static List<TaskLogDto> Union(IEnumerable<TaskLogDto> read, IEnumerable<TaskLogDto> held)
    {
        var byId = new Dictionary<int, TaskLogDto>();
        var withoutId = new List<TaskLogDto>();
        foreach (var line in read.Concat(held))
        {
            if (line.Id > 0) byId[line.Id] = line;
            else withoutId.Add(line);
        }
        return [.. byId.Values.Concat(withoutId).OrderBy(line => line.Timestamp.ToUniversalTime()).ThenBy(line => line.Id)];
    }

    private void Store(int taskId, List<TaskLogDto> lines)
    {
        Lines[taskId] = lines;
        Trim(taskId, lines);
    }

    private void Trim(int taskId, List<TaskLogDto> lines)
    {
        var overflow = lines.Count - _limits.GetValueOrDefault(taskId, LiveWindow);
        if (overflow <= 0) return;
        lines.RemoveRange(0, overflow);
        _hasEarlier.Add(taskId);
    }
}
