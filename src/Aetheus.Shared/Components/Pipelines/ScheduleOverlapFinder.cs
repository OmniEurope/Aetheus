// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// Pure overlap detection between the occurrences of different scheduled tasks. Intervals are half-open
/// (<c>[StartUtc, EndUtc)</c>): an end equal to another start is edge to edge, not an overlap. Works on
/// absolute UTC instants, so a run crossing midnight is compared like any other.
/// </summary>
public static class ScheduleOverlapFinder
{
    /// <summary>Returns the tasks with each occurrence's <see cref="ScheduleOccurrenceDto.OverlapMinutes"/>
    /// filled: the minutes of that occurrence during which more tasks run than <paramref name="capacity"/>
    /// allows (recette R-39: runs side by side within the execution capacity are not an overlap). With a
    /// capacity of one, the default, that is the minutes covered by at least one occurrence of another task.
    /// Covered time is counted once, rounded up to whole minutes. Occurrences of the same task are
    /// never compared with each other. The input is not modified.</summary>
    /// <param name="tasks">The tasks of the plan, with their occurrences.</param>
    /// <param name="capacity">How many tasks may run at once (at least one).</param>
    public static List<ScheduledTaskDto> Fill(IReadOnlyList<ScheduledTaskDto> tasks, int capacity = 1)
    {
        var filled = FillOccurrences([.. tasks.Select(task => task.Occurrences)], capacity);
        return [.. tasks.Select((task, index) => task with { Occurrences = filled[index] })];
    }

    /// <summary>The same as <see cref="Fill(IReadOnlyList{ScheduledTaskDto}, int)"/> with the server actions of the plan
    /// counted as tasks of their own (recette R-8): a pipeline and a server action that run together overlap, whatever
    /// the server. Returns the pipelines, then the actions, each with its occurrences' overlap minutes filled.</summary>
    public static (List<ScheduledTaskDto> Tasks, List<ScheduledServerActionDto> Actions) Fill(
        IReadOnlyList<ScheduledTaskDto> tasks, IReadOnlyList<ScheduledServerActionDto> actions, int capacity)
    {
        var filled = FillOccurrences([.. tasks.Select(task => task.Occurrences), .. actions.Select(action => action.Occurrences)], capacity);
        return (
            [.. tasks.Select((task, index) => task with { Occurrences = filled[index] })],
            [.. actions.Select((action, index) => action with { Occurrences = filled[tasks.Count + index] })]);
    }

    private static List<List<ScheduleOccurrenceDto>> FillOccurrences(IReadOnlyList<List<ScheduleOccurrenceDto>> occurrencesByTask, int capacity)
    {
        var allowed = Math.Max(1, capacity);
        var others = new List<(int Task, DateTime Start, DateTime End)>();
        for (var t = 0; t < occurrencesByTask.Count; t++)
        {
            foreach (var occurrence in occurrencesByTask[t])
            {
                if (occurrence.EndUtc > occurrence.StartUtc)
                {
                    others.Add((t, occurrence.StartUtc, occurrence.EndUtc));
                }
            }
        }

        others.Sort((a, b) => a.Start.CompareTo(b.Start));
        var longest = others.Count == 0 ? TimeSpan.Zero : others.Max(other => other.End - other.Start);
        var result = new List<List<ScheduleOccurrenceDto>>(occurrencesByTask.Count);
        for (var t = 0; t < occurrencesByTask.Count; t++)
        {
            var task = t;
            result.Add([.. occurrencesByTask[t]
                .Select(occurrence => occurrence with { OverlapMinutes = OverlapOf(task, occurrence, others, longest, allowed) })]);
        }

        return result;
    }

    /// <summary>The most tasks running at once at any instant of <c>[start, end)</c>, from the occurrences the
    /// tasks carry. Used to keep, among the pairs of runs that share time, those that really exceed the capacity.</summary>
    public static int PeakConcurrency(IReadOnlyList<ScheduledTaskDto> tasks, DateTime start, DateTime end)
    {
        var edges = new List<(DateTime At, int Task, int Delta)>();
        for (var t = 0; t < tasks.Count; t++)
        {
            foreach (var occurrence in tasks[t].Occurrences)
            {
                var from = Max(occurrence.StartUtc, start);
                var to = Min(occurrence.EndUtc, end);
                if (from < to)
                {
                    edges.Add((from, t, 1));
                    edges.Add((to, t, -1));
                }
            }
        }

        edges.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Delta.CompareTo(b.Delta));
        var running = new Dictionary<int, int>();
        var peak = 0;
        foreach (var (_, task, delta) in edges)
        {
            var count = running.GetValueOrDefault(task) + delta;
            if (count == 0)
            {
                running.Remove(task);
            }
            else
            {
                running[task] = count;
            }

            peak = Math.Max(peak, running.Count);
        }

        return peak;
    }

    private static int OverlapOf(
        int task, ScheduleOccurrenceDto occurrence, List<(int Task, DateTime Start, DateTime End)> others, TimeSpan longest, int capacity)
    {
        if (occurrence.EndUtc <= occurrence.StartUtc)
        {
            return 0;
        }

        var shared = new List<(int Task, DateTime Start, DateTime End)>();
        // Nothing starting more than the longest run before this occurrence can still reach it.
        for (var i = FirstStartingAtOrAfter(others, occurrence.StartUtc - longest); i < others.Count; i++)
        {
            var other = others[i];
            if (other.Start >= occurrence.EndUtc)
            {
                break;
            }

            if (other.Task == task || other.End <= occurrence.StartUtc)
            {
                continue;
            }

            shared.Add((other.Task, Max(other.Start, occurrence.StartUtc), Min(other.End, occurrence.EndUtc)));
        }

        var length = capacity <= 1
            ? UnionLength([.. shared.Select(item => (item.Start, item.End))])
            : LengthOverCapacity(shared, capacity);
        return (int)Math.Ceiling(length.TotalMinutes);
    }

    // The time during which at least `capacity` other tasks run: with the occurrence itself, more than the capacity.
    private static TimeSpan LengthOverCapacity(List<(int Task, DateTime Start, DateTime End)> shared, int capacity)
    {
        var edges = new List<(DateTime At, int Task, int Delta)>(shared.Count * 2);
        foreach (var (task, start, end) in shared)
        {
            edges.Add((start, task, 1));
            edges.Add((end, task, -1));
        }

        // At one instant, the ends come first: a run ending where another starts is not running with it.
        edges.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Delta.CompareTo(b.Delta));
        var running = new Dictionary<int, int>();
        var total = TimeSpan.Zero;
        var previous = DateTime.MinValue;
        foreach (var (at, task, delta) in edges)
        {
            if (running.Count >= capacity)
            {
                total += at - previous;
            }

            previous = at;
            var count = running.GetValueOrDefault(task) + delta;
            if (count == 0)
            {
                running.Remove(task);
            }
            else
            {
                running[task] = count;
            }
        }

        return total;
    }

    private static int FirstStartingAtOrAfter(List<(int Task, DateTime Start, DateTime End)> sorted, DateTime instant)
    {
        var low = 0;
        var high = sorted.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (sorted[middle].Start < instant)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static TimeSpan UnionLength(List<(DateTime Start, DateTime End)> intervals)
    {
        var total = TimeSpan.Zero;
        var coveredUntil = DateTime.MinValue;
        foreach (var (start, end) in intervals.OrderBy(i => i.Start))
        {
            var from = Max(start, coveredUntil);
            if (end > from)
            {
                total += end - from;
                coveredUntil = end;
            }
        }

        return total;
    }

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;
}
