// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Projects;

/// <summary>A project resources task with its parsed output.</summary>
public sealed record ProjectServerTaskReport(ServerTask Task, ProjectServerResourceReport Report, string Output)
{
    public ProjectServerTaskPhase Phase => Task.Operation == OperationKind.ProjectResourcesRemove
        ? ProjectServerTaskPhase.Removal
        : ProjectServerTaskPhase.Discovery;

    public bool Finished => Task.Status is not (TaskExecutionStatus.Pending or TaskExecutionStatus.Assigned or TaskExecutionStatus.Running);

    /// <summary>The agent ran to its closing line: what it printed is everything it had to say.</summary>
    public bool Readable => Finished && Report.Complete
        && (Task.Status == TaskExecutionStatus.Success || Phase == ProjectServerTaskPhase.Removal);

    /// <summary>Discovery: every resource read. Removal: every confirmed resource gone, nothing left behind.</summary>
    public bool Succeeded => Task.Status == TaskExecutionStatus.Success && Report.Complete && Report.Unreadable.Count == 0
        && Report.Resources.All(item => item.Outcome is ProjectServerResourceOutcome.Found
            ? Phase == ProjectServerTaskPhase.Discovery
            : item.Outcome is ProjectServerResourceOutcome.Removed or ProjectServerResourceOutcome.Absent);

    public IEnumerable<ProjectServerResourceRef> Found => Report.Resources
        .Where(item => item.Outcome == ProjectServerResourceOutcome.Found)
        .Select(item => new ProjectServerResourceRef(item.Kind, item.Name, item.Scope));
}

/// <summary>
/// Recette R-97 (ADR-058): what the server tasks of a deletion say, and whether they prove that nothing the
/// project deployed is left. A resource counts as removed only when the agent printed <c>REMOVED</c> or
/// <c>ABSENT</c> for it in a removal that ran to its closing line; anything else (a server not asked, a read
/// that failed, a removal that failed or did not finish) is a problem the user must see, and keeps the project
/// waiting for its cleanup (recette R-51). A value refused is said and audited, never waited for.
/// </summary>
internal static class ProjectServerCleanupReports
{
    private const int FailureTailLines = 8;

    public static ProjectServerResourceTaskDto ToDto(ProjectServerTaskReport report, ProjectServerCleanupTarget? target)
    {
        ArgumentNullException.ThrowIfNull(report);
        var state = !report.Finished ? ProjectServerTaskState.Running
            : report.Succeeded ? ProjectServerTaskState.Succeeded
            : ProjectServerTaskState.Failed;
        return new ProjectServerResourceTaskDto
        {
            TaskId = report.Task.Id,
            ServerId = report.Task.ServerId,
            ServerName = target?.ServerName ?? string.Empty,
            Phase = report.Phase,
            State = state,
            Resources = [.. report.Report.Resources.Select(item => new ProjectServerResourceDto(
                item.Kind, item.Name, item.Scope, target?.EnvironmentOf(item.Kind, item.Scope), item.Outcome, item.Reason))],
            Unreadable = [.. report.Report.Unreadable],
            Failure = state == ProjectServerTaskState.Failed && !report.Report.Complete ? FailureOf(report) : null,
        };
    }

    /// <param name="isRecent">Whether a discovery is recent enough to stand for the server's current state (see
    /// <c>ProjectServerCleanupService.DiscoveryValidity</c>); an older one proves nothing.</param>
    public static ProjectServerCleanupVerdict Verify(ProjectServerCleanupPlan plan, IReadOnlyList<ProjectServerTaskReport> reports,
        Func<ProjectServerTaskReport, bool> isRecent)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(isRecent);
        var problems = new List<string>();
        var removed = 0;
        foreach (var server in plan.Servers)
            removed += VerifyServer(server, reports, isRecent, problems);
        // Recette R-51: a value Aetheus refuses to use is never a cleanup failure, since relaunching the cleanup cannot
        // change it; it is audited as left in place. An environment without a server has nothing to remove at all.
        var leftInPlace = plan.Refused.Where(item => item.Reason != ProjectDeletionRefusal.NoServer)
            .Select(item => $"{item.Environment}: {item.Key}={item.Value} not used ({item.Reason})").ToList();

        // A project that deployed nothing Aetheus knows of has nothing to say about its servers.
        if (plan.Servers.Count == 0 && problems.Count == 0 && leftInPlace.Count == 0)
            return new ProjectServerCleanupVerdict(true, problems, string.Empty);
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"server resources removed: {removed}; servers: {plan.Servers.Count}; problems: {problems.Count}");
        if (problems.Count > 0) summary += " (" + string.Join("; ", problems.Take(20)) + ")";
        if (leftInPlace.Count > 0) summary += "; values not used, left in place: " + string.Join("; ", leftInPlace.Take(20));
        return new ProjectServerCleanupVerdict(problems.Count == 0, problems, summary);
    }

    /// <summary>Adds what is not proved removed on <paramref name="server"/>; returns how many resources are.</summary>
    private static int VerifyServer(ProjectServerCleanupTarget server, IReadOnlyList<ProjectServerTaskReport> reports,
        Func<ProjectServerTaskReport, bool> isRecent, List<string> problems)
    {
        if (server.State == ProjectDeletionServerState.NoWriteAccess)
        {
            problems.Add($"{server.ServerName}: not removed, no write access on this server");
            return 0;
        }
        if (server.State != ProjectDeletionServerState.Ready)
        {
            problems.Add($"{server.ServerName}: not asked ({server.State})");
            return 0;
        }
        var discoveries = reports
            .Where(item => item.Task.ServerId == server.ServerId && item.Phase == ProjectServerTaskPhase.Discovery && item.Readable)
            .ToList();
        var discovery = discoveries.Where(isRecent).MaxBy(item => item.Task.Id);
        if (discovery is null)
        {
            problems.Add(discoveries.Count > 0
                ? $"{server.ServerName}: its resources were not read recently (read again before deleting)"
                : $"{server.ServerName}: its resources were not read");
            return 0;
        }
        problems.AddRange(discovery.Report.Unreadable.Select(item =>
            $"{server.ServerName}: {item.Kind} of {item.Scope} could not be read ({item.Reason})"));
        var covered = discovery.Found.Where(item => server.Spec().Covers(item)).ToList();
        problems.AddRange(covered.Where(item => !server.CanRemove(item.Kind)).Select(item =>
            $"{server.ServerName}: {item.Kind} {item.Name} cannot be removed (the agent lacks {string.Join(", ", server.MissingCapabilities)})"));
        var found = covered.Where(item => server.CanRemove(item.Kind)).ToList();
        if (found.Count == 0) return 0;

        var removal = reports
            .Where(item => item.Task.ServerId == server.ServerId && item.Phase == ProjectServerTaskPhase.Removal
                && item.Task.Id > discovery.Task.Id && item.Finished)
            .MaxBy(item => item.Task.Id);
        if (removal is null || !removal.Report.Complete)
        {
            problems.Add($"{server.ServerName}: {found.Count} resource(s) not removed");
            return 0;
        }
        var gone = removal.Report.Resources
            .Where(item => item.Outcome is ProjectServerResourceOutcome.Removed or ProjectServerResourceOutcome.Absent)
            .Select(item => new ProjectServerResourceRef(item.Kind, item.Name, item.Scope))
            .ToHashSet();
        problems.AddRange(found.Where(item => !gone.Contains(item))
            .Select(item => $"{server.ServerName}: {item.Kind} {item.Name} not removed"));
        problems.AddRange(removal.Report.Resources
            .Where(item => item.Outcome == ProjectServerResourceOutcome.NotListed)
            .Select(item => $"{server.ServerName}: {item.Kind} {item.Name} appeared after the preview and was left"));
        problems.AddRange(removal.Report.Unreadable.Select(item =>
            $"{server.ServerName}: {item.Kind} of {item.Scope} could not be read after the removal ({item.Reason})"));
        return found.Count(gone.Contains);
    }

    private static string FailureOf(ProjectServerTaskReport report)
    {
        var tail = report.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(FailureTailLines).ToList();
        if (tail.Count > 0) return string.Join('\n', tail);
        return report.Task.FailureReason ?? report.Task.Status.ToString();
    }
}
