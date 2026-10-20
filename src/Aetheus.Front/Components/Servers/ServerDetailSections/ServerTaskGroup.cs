// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Recette R3-37: the tasks one click launches together (a Docker project's bulk action queues one
/// task per container). Each member is the start of one expected task name; it binds to the first new
/// task of the server carrying that name, and records how it ended. <see cref="ServerTaskFollower"/>
/// shows the group as one line: how many finished, and the first failure with its cause.
/// </summary>
internal sealed class ServerTaskGroup(IEnumerable<string> namePrefixes)
{
    private sealed class Member(string prefix)
    {
        public string Prefix { get; } = prefix;
        public int? TaskId { get; set; }
        public TaskExecutionStatus? Outcome { get; set; }
    }

    private readonly List<Member> _members = namePrefixes.Select(prefix => new Member(prefix)).ToList();

    public int Count => _members.Count;

    public int Finished => _members.Count(member => member.Outcome is not null);

    public bool AllFinished => _members.All(member => member.Outcome is not null);

    /// <summary>The first member that ended in anything but success, if any.</summary>
    public int? FirstFailedTaskId => _members
        .FirstOrDefault(member => member.Outcome is { } outcome && outcome != TaskExecutionStatus.Success)?.TaskId;

    /// <summary>A member whose task could not be launched: it is not waited for.</summary>
    public void Drop(string namePrefix) =>
        _members.RemoveAll(member => member.TaskId is null && string.Equals(member.Prefix, namePrefix, StringComparison.Ordinal));

    /// <summary>Binds the unbound members to the new tasks of the feed; true when one was bound.</summary>
    public bool Bind(IEnumerable<ServerTaskDto> tasks, int serverId, IReadOnlySet<int> known)
    {
        var bound = false;
        foreach (var task in tasks)
        {
            if (task.ServerId != serverId || known.Contains(task.Id) || _members.Any(member => member.TaskId == task.Id))
                continue;
            var member = Unbound(task.Name);
            if (member is null) continue;
            member.TaskId = task.Id;
            bound = true;
        }
        return bound;
    }

    /// <summary>True when one of the bound, unfinished members is running.</summary>
    public bool AnyRunning(IEnumerable<ServerTaskDto> tasks) => tasks.Any(task =>
        task.Status == TaskExecutionStatus.Running
        && _members.Any(member => member.TaskId == task.Id && member.Outcome is null));

    /// <summary>Records the end of a member's task; false when the task is not one of the group.
    /// A task so fast it ended before the feed listed it still binds by its name.</summary>
    public bool Complete(TaskCompletedNotification notification, int serverId, IReadOnlySet<int> known)
    {
        var member = _members.FirstOrDefault(candidate => candidate.TaskId == notification.TaskId);
        if (member is null && notification.ServerId == serverId && !known.Contains(notification.TaskId))
        {
            member = Unbound(notification.TaskName);
            if (member is not null) member.TaskId = notification.TaskId;
        }
        if (member is null || member.Outcome is not null) return false;
        member.Outcome = notification.Status;
        return true;
    }

    private Member? Unbound(string taskName) => _members.FirstOrDefault(member =>
        member.TaskId is null && taskName.StartsWith(member.Prefix, StringComparison.OrdinalIgnoreCase));
}
