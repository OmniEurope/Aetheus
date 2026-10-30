// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Servers;

/// <summary>
/// Raises an alert for each heartbeat that reports new out-of-memory kills (PLAN-010 lot 1). The
/// kernel counter only grows until a reboot, so a new kill is its increase over the value stored on
/// the server, kept across heartbeats and backend restarts; a new boot id means the counter restarted
/// at 0 and every kill it counts is new. The first report only records the baseline, and a heartbeat
/// without a report (Windows, older agent, unreadable counter) clears it: unknown never alerts.
/// </summary>
internal sealed class OomKillMonitor(
    IHubContext<AlertHub> alertHub,
    TimeProvider timeProvider,
    IDomainEventDispatcher? domainEvents,
    ILogger logger)
{
    /// <returns>Whether the server's stored counter changed and must be saved.</returns>
    public async Task<bool> CheckAsync(Server server, ServerHeartbeatDto heartbeat, CancellationToken ct)
    {
        var report = heartbeat.OomKills;
        if (report is null || string.IsNullOrWhiteSpace(report.BootId) || report.KillCount < 0)
            return Store(server, null, null);

        var newKills = NewKills(server, report);
        var changed = Store(server, report.BootId, report.KillCount);
        if (newKills > 0)
            await RaiseAsync(server, report, newKills, heartbeat.Docker.Containers, ct).ConfigureAwait(false);
        return changed;
    }

    internal static long NewKills(Server server, OomKillReportDto report)
    {
        if (server.OomKillCount is not { } previous || server.OomKillBootId is null) return 0;
        if (!string.Equals(server.OomKillBootId, report.BootId, StringComparison.Ordinal))
            return report.KillCount;
        return Math.Max(0, report.KillCount - previous);
    }

    /// <summary>"process (pid N)" for the newest <paramref name="newKills"/> victims the agent named.</summary>
    internal static List<string> NameVictims(OomKillReportDto report, long newKills)
    {
        var named = (int)Math.Min(newKills, report.RecentVictims.Count);
        return report.RecentVictims
            .Skip(report.RecentVictims.Count - named)
            .Select(victim => $"{victim.Process} (pid {victim.Pid})")
            .ToList();
    }

    /// <summary>
    /// What the agent read about the newest <paramref name="newKills"/> victims: kind, container (named from
    /// the containers of the same heartbeat, else its short id) and memory. Empty when none of them carries
    /// anything beyond process and pid, i.e. for an older agent.
    /// </summary>
    internal static List<OomKillDetail> DescribeKills(
        OomKillReportDto report, long newKills, IReadOnlyList<DockerContainerDto> containers)
    {
        var named = (int)Math.Min(newKills, report.RecentVictims.Count);
        var kills = report.RecentVictims
            .Skip(report.RecentVictims.Count - named)
            .Select(victim => new OomKillDetail(
                victim.Process, victim.Pid, victim.Kind, ContainerName(victim.ContainerId, containers), victim.Cgroup,
                victim.AnonRssKb, victim.FileRssKb, victim.ShmemRssKb))
            .ToList();
        return kills.Exists(kill => kill.HasDetail) ? kills : [];
    }

    private static string? ContainerName(string? shortId, IReadOnlyList<DockerContainerDto> containers)
    {
        if (string.IsNullOrEmpty(shortId)) return null;
        var match = containers.FirstOrDefault(container => container.ContainerId.Length > 0
            && (container.ContainerId.StartsWith(shortId, StringComparison.Ordinal)
                || shortId.StartsWith(container.ContainerId, StringComparison.Ordinal)));
        return string.IsNullOrWhiteSpace(match?.Name) ? shortId : match.Name;
    }

    internal static string BuildMessage(
        string serverName, OomKillReportDto report, long newKills, IReadOnlyList<string> victims,
        IReadOnlyList<OomKillDetail>? kills = null)
    {
        if (kills is { Count: > 0 })
        {
            var text = string.Join("; ", kills.Select(Describe));
            var more = newKills - kills.Count;
            return more > 0
                ? $"OOM kill on {serverName}: {text} and {more} more not named."
                : $"OOM kill on {serverName}: {text}.";
        }
        if (victims.Count == 0)
        {
            var reason = report.VictimsReadable
                ? "the kernel log no longer holds its line"
                : "the agent cannot read the kernel log";
            return newKills == 1
                ? $"OOM kill on {serverName}; process not named ({reason})."
                : $"{newKills} OOM kills on {serverName}; processes not named ({reason}).";
        }
        var unnamed = newKills - victims.Count;
        return unnamed > 0
            ? $"OOM kill on {serverName}: {string.Join(", ", victims)} and {unnamed} more not named."
            : $"OOM kill on {serverName}: {string.Join(", ", victims)}.";
    }

    private static string Describe(OomKillDetail kill)
    {
        var who = $"{kill.Process} (pid {kill.Pid})";
        var memory = MemorySuffix(kill);
        var cause = kill.Kind switch
        {
            OomKillKind.Cgroup when kill.Container is not null => $"container memory limit reached ({kill.Container})",
            OomKillKind.Cgroup when kill.Cgroup is not null => $"cgroup memory limit reached ({kill.Cgroup})",
            OomKillKind.Cgroup => "cgroup memory limit reached",
            OomKillKind.Global => "server out of memory",
            OomKillKind.Cpuset or OomKillKind.MemoryPolicy => "out of memory in a restricted zone (cpuset or memory policy)",
            _ => null
        };
        return cause is null ? $"{who}{memory}" : $"{cause}: {who}{memory}";
    }

    private static string MemorySuffix(OomKillDetail kill)
    {
        if (kill.ResidentKb is not { } resident) return string.Empty;
        var parts = new List<string>(3);
        if (kill.AnonRssKb is { } anon) parts.Add($"anon {Mb(anon)} MB");
        if (kill.FileRssKb is { } file) parts.Add($"file {Mb(file)} MB");
        if (kill.ShmemRssKb is { } shmem) parts.Add($"shmem {Mb(shmem)} MB");
        return $", {Mb(resident)} MB resident ({string.Join(", ", parts)})";

        static string Mb(long kb) => OomKillDetail.Megabytes(kb).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task RaiseAsync(
        Server server, OomKillReportDto report, long newKills, IReadOnlyList<DockerContainerDto> containers, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var victims = NameVictims(report, newKills);
        var kills = DescribeKills(report, newKills, containers);
        var message = BuildMessage(server.Name, report, newKills, victims, kills);
        logger.LogWarning("OOM kill on server {ServerId}: {Message}", server.Id, message);
        await alertHub.Clients.Group(HubGroups.Alerts).SendAsync("AlertTriggered", new AlertTriggeredDto
        {
            RuleName = "OOM",
            ServerId = server.Id,
            ServerName = server.Name,
            Metric = "OomKill",
            Severity = "Critical",
            Message = message,
            TriggeredAt = now
        }, ct).ConfigureAwait(false);
        // Awaited, observer semantics: a handler failure is logged by the dispatcher and the heartbeat
        // goes on (same contract as the sudoers drift alert).
        if (domainEvents is not null)
        {
            await domainEvents.DispatchAsync(
                new OomKillDetectedEvent(server.Id, server.Name, newKills, victims, message, now, kills), ct)
                .ConfigureAwait(false);
        }
    }

    private static bool Store(Server server, string? bootId, long? count)
    {
        if (string.Equals(server.OomKillBootId, bootId, StringComparison.Ordinal) && server.OomKillCount == count)
            return false;
        server.OomKillBootId = bootId;
        server.OomKillCount = count;
        return true;
    }
}
