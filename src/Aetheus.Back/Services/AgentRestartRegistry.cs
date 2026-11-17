// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;

namespace Aetheus.Back.Services;

/// <summary>
/// Whether the host rebooted between two agent processes, as far as the two boot ids say.
/// </summary>
public enum HostRebootVerdict
{
    /// <summary>A boot id is missing (Windows, an older agent): neither yes nor no.</summary>
    Unknown,
    Yes,
    No
}

/// <summary>What an agent reported about how its previous process ended.</summary>
public sealed record AgentRestartInfo(
    string Kind,
    string? Reason,
    DateTime? OccurredAtUtc,
    HostRebootVerdict HostRebooted,
    DateTime ReportedAtUtc)
{
    public static AgentRestartInfo From(AgentExitReportDto report, DateTime reportedAtUtc) => new(
        report.Kind,
        report.Reason,
        report.OccurredAtUtc,
        string.IsNullOrWhiteSpace(report.PreviousBootId) || string.IsNullOrWhiteSpace(report.BootId)
            ? HostRebootVerdict.Unknown
            : string.Equals(report.PreviousBootId, report.BootId, StringComparison.Ordinal)
                ? HostRebootVerdict.No
                : HostRebootVerdict.Yes,
        reportedAtUtc);

    /// <summary>The verdict as it reads in a log line.</summary>
    public string HostRebootedText => HostRebooted switch
    {
        HostRebootVerdict.Yes => "yes",
        HostRebootVerdict.No => "no",
        _ => "unknown"
    };

    /// <summary>The cause as a sentence for a failed task: what the agent said, never more.</summary>
    public string Describe()
    {
        var reason = string.IsNullOrWhiteSpace(Reason) ? "no reason given" : Reason.Trim();
        var when = OccurredAtUtc is { } at ? $" at {at:yyyy-MM-dd HH:mm:ss} UTC" : string.Empty;
        return $"Agent restart cause reported by the agent: {Kind}{when} ({reason}); host rebooted: {HostRebootedText}.";
    }
}

/// <summary>
/// Keeps the newest restart cause each agent reported, in memory, so the sweep that fails the tasks of a
/// replaced session can say why. A cause expires: a task failed long after the report would otherwise carry
/// a reason that belongs to another restart. Lost on a backend restart, in which case the task keeps the
/// generic text rather than an invented cause.
/// </summary>
public sealed class AgentRestartRegistry(TimeProvider timeProvider)
{
    /// <summary>How long a reported cause may explain a replaced session.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<int, AgentRestartInfo> _byServer = new();

    public void Record(int serverId, AgentRestartInfo info) => _byServer[serverId] = info;

    /// <summary>The cause the server's agent reported within <see cref="Retention"/>, or null.</summary>
    public AgentRestartInfo? Find(int serverId)
    {
        if (!_byServer.TryGetValue(serverId, out var info))
            return null;
        if (timeProvider.GetUtcNow().UtcDateTime - info.ReportedAtUtc <= Retention)
            return info;
        _byServer.TryRemove(serverId, out _);
        return null;
    }
}
