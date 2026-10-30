// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Servers;

namespace Aetheus.Back.Components.Servers.Events;

/// <summary>
/// What the agent's kernel log says about one killed process (see <see cref="OomKillVictimDto"/>), with
/// the container named from the containers of the same heartbeat. Every figure is null when the agent
/// did not read it: unknown, never 0.
/// </summary>
/// <param name="Container">The docker container name, or its short id when the heartbeat does not list
/// it; null when the cgroup is not a docker scope.</param>
public sealed record OomKillDetail(
    string Process,
    int Pid,
    OomKillKind Kind,
    string? Container,
    string? Cgroup,
    long? AnonRssKb,
    long? FileRssKb,
    long? ShmemRssKb)
{
    /// <summary>Whether the agent reported anything beyond the process and its pid.</summary>
    public bool HasDetail => Kind != OomKillKind.Unknown || Container is not null || Cgroup is not null || ResidentKb is not null;

    /// <summary>Anonymous, file and shared resident memory added; null when none was reported.</summary>
    public long? ResidentKb => AnonRssKb is null && FileRssKb is null && ShmemRssKb is null
        ? null
        : (AnonRssKb ?? 0) + (FileRssKb ?? 0) + (ShmemRssKb ?? 0);

    /// <summary>kB rounded to the nearest MB, halves up.</summary>
    public static long Megabytes(long kb) => (kb + 512) / 1024;
}
