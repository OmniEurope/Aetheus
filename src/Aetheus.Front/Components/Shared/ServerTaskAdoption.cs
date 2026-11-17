// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Recette R-4 (ex R-12): the one matcher every page uses to find, in the live task feed
/// (<see cref="TaskTrackerService.Tasks"/>, seeded from <c>/api/tasks/active</c>), the task it must show again when
/// the user comes back to it. A page that launches a server task never keeps that task only in its own memory: on
/// arrival, and on every change of the feed, it asks this matcher for the task of its server that is still queued or
/// running.
/// </summary>
internal static class ServerTaskAdoption
{
    /// <summary>True while the task is queued, assigned to an agent or running.</summary>
    internal static bool IsInFlight(ServerTaskDto task) =>
        task.Status is TaskExecutionStatus.Pending or TaskExecutionStatus.Assigned or TaskExecutionStatus.Running;

    /// <summary>The first task of <paramref name="serverId"/> still in flight whose name starts with one of
    /// <paramref name="prefixes"/> (case-insensitive) and with none of <paramref name="notAdoptedPrefixes"/>. No
    /// prefix (or only blank ones) adopts nothing: it would match every task of the server. Pipeline steps are left
    /// to their run page unless <paramref name="includePipelineTasks"/>.</summary>
    internal static ServerTaskDto? FindInFlight(IEnumerable<ServerTaskDto> tasks, int serverId, IReadOnlyList<string> prefixes,
        IReadOnlyList<string>? notAdoptedPrefixes = null, bool includePipelineTasks = false)
    {
        var named = prefixes.Where(prefix => prefix.Length > 0).ToList();
        if (named.Count == 0) return null;
        return FindInFlight(tasks, serverId,
            name => named.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                && !(notAdoptedPrefixes ?? []).Any(excluded => name.StartsWith(excluded, StringComparison.OrdinalIgnoreCase)),
            includePipelineTasks);
    }

    /// <summary>The first task of <paramref name="serverId"/> still in flight whose name <paramref name="nameMatches"/>,
    /// for a page that launches one exact task (the name is then compared as the caller decides).</summary>
    internal static ServerTaskDto? FindInFlight(IEnumerable<ServerTaskDto> tasks, int serverId, Func<string, bool> nameMatches,
        bool includePipelineTasks = false) =>
        tasks.FirstOrDefault(task => task.ServerId == serverId
            && (includePipelineTasks || task.PipelineRunId is null)
            && IsInFlight(task)
            && nameMatches(task.Name));
}
