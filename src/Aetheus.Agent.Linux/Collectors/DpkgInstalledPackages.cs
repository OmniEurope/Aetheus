// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Services;

namespace Aetheus.Agent.Linux.Collectors;

/// <summary> Which Debian packages dpkg holds as installed. A package left in the <c>rc</c> state
/// by <c>apt-get remove</c> (removed, configuration files kept) or purged is not installed,
/// whatever unit file or configuration directory it left behind. </summary>
internal static class DpkgInstalledPackages
{
    // One line per package dpkg knows: name, a tab, then the three-letter status abbreviation
    // (desired action, current status, error flag), e.g. "dovecot-core\tii " or "dovecot-core\trc ".
    // Listing every package reads the same status database a filtered query reads, and an absent
    // package is simply missing from the list instead of an exit code and a localized error message.
    private static readonly string[] QueryArgv = ["-W", "-f=${Package}\\t${db:Status-Abbrev}\\n"];

    /// <summary>
    /// The names of the installed packages, or null when dpkg cannot answer (no dpkg on this
    /// distribution, a failed query, an empty answer): the caller then keeps its other evidence.
    /// </summary>
    public static async Task<HashSet<string>?> ReadAsync(IShellRunner shellRunner, ILogger logger, CancellationToken ct)
    {
        try
        {
            var res = await shellRunner.RunExecAsync("dpkg-query", QueryArgv, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            return res.ExitCode == 0 ? Parse(res.StdOut) : null;
        }
        catch (Exception ex)
        {
            // dpkg-query absent (not a Debian-family host): the launch itself throws.
            logger.LogDebug(ex, "dpkg-query is unavailable; package states are not checked");
            return null;
        }
    }

    internal static HashSet<string>? Parse(string stdout)
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var known = 0;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2 || parts[1].Length < 2 || string.IsNullOrWhiteSpace(parts[0])) continue;
            known++;
            if (IsInstalledStatus(parts[1][1]))
                installed.Add(parts[0].Trim());
        }
        return known == 0 ? null : installed;
    }

    // The second letter is the current status. `n` (not installed) and `c` (configuration files only,
    // the `rc` left by `apt-get remove`) mean the package's programs and units are gone. Every other
    // status, half-installed or half-configured included, still has files on disk: it stays installed
    // so the operator can uninstall it, rather than vanish into a state no button can reach.
    private static bool IsInstalledStatus(char status) => status is not ('n' or 'c');
}
