// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Collectors.MachineJobs;

/// <summary>
/// The systemd timers the planner shows (recette R-27), the one place the list is kept. A machine runs dozens of timers; the
/// planner is about the moments the host is busy enough to collide with a pipeline, a backup or a scan, so only the timers
/// that are known to read or write a lot (disk, package manager, network) or to touch a service are listed, by their exact
/// unit name. Each entry says why. Left out on purpose: the timers that fire every few minutes or every hour (sysstat
/// collection: a wall of blocks that says nothing), the timers of the user manager, the ones
/// the agent itself or Aetheus installs (they are already in Aetheus's own schedules), and any unit an administrator wrote
/// (its name is unknown here: it belongs to a custom list, not to a guess). Adding a unit is a code change reviewed with the
/// reason, never a setting a remote request can widen.
/// </summary>
internal static class MachineTimerWhitelist
{
    private static readonly Dictionary<string, string> Reasons = new(StringComparer.Ordinal)
    {
        ["logrotate.timer"] = "rotates and compresses the logs: disk and CPU burst, may restart services",
        ["apt-daily.timer"] = "refreshes the package lists: network and the package manager lock",
        ["apt-daily-upgrade.timer"] = "installs the unattended upgrades: package manager lock, may restart services",
        ["dpkg-db-backup.timer"] = "copies the dpkg database: short disk burst",
        ["certbot.timer"] = "renews the certificates: network, may reload the web server",
        ["snap.certbot.renew.timer"] = "same as certbot.timer, when certbot comes from snap",
        ["fstrim.timer"] = "trims the filesystems: heavy disk I/O",
        ["e2scrub_all.timer"] = "checks the ext4 metadata online: disk I/O",
        ["mdcheck_start.timer"] = "starts the software RAID consistency check: heavy disk I/O for hours",
        ["man-db.timer"] = "rebuilds the manual page index: disk and CPU burst",
        ["plocate-updatedb.timer"] = "rebuilds the file index: walks every filesystem",
        ["fwupd-refresh.timer"] = "refreshes the firmware metadata: network",
        ["motd-news.timer"] = "fetches the message of the day: network",
        ["dailyaidecheck.timer"] = "AIDE integrity check: reads every monitored file"
    };

    /// <summary>Whether a timer unit is listed. The comparison is exact: a unit that only resembles a listed one is not.</summary>
    public static bool Contains(string unit) => Reasons.ContainsKey(unit);

    /// <summary>The whitelisted units, in a stable order.</summary>
    public static IReadOnlyCollection<string> Units => [.. Reasons.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Why a unit is listed; null when it is not.</summary>
    public static string? ReasonFor(string unit) => Reasons.GetValueOrDefault(unit);
}
