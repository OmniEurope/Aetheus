// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// R-119: the systemd unit TeamSpeak runs under, and the systemctl argv the aetheus-teamspeak grant names. The
/// teamspeak-setup helper installs <c>teamspeak3.service</c>; a server installed by hand (the VPS simulator among them)
/// may run <c>teamspeak.service</c>, which the collector already accepts. The former <c>ts3server</c> unit name of the
/// graceful restart matched no install.
/// </summary>
internal static class TeamspeakUnit
{
    /// <summary>The units looked for, in this order; each verb on each of them is a line of the grant.</summary>
    internal static readonly string[] Candidates = ["teamspeak3.service", "teamspeak.service"];

    /// <summary>The first installed candidate, or null when none is.</summary>
    internal static async Task<string?> ResolveAsync(Func<string, CancellationToken, Task<bool>> unitExists, CancellationToken ct)
    {
        foreach (var unit in Candidates)
        {
            if (await unitExists(unit, ct).ConfigureAwait(false))
                return unit;
        }
        return null;
    }

    /// <summary>Whether systemd knows the unit, read without privilege (<c>systemctl show -p LoadState --value</c>
    /// prints <c>loaded</c>, or <c>not-found</c> for a unit that does not exist).</summary>
    internal static async Task<bool> ExistsAsync(string unit, ILogger logger, CancellationToken ct)
    {
        var (result, lines) = await ProcessRunner.CaptureAsync(
            "systemctl", ["show", "-p", "LoadState", "--value", unit], 15, logger, ct).ConfigureAwait(false);
        return result.ExitCode == 0 && lines.Any(line => line.Trim() == "loaded");
    }

    /// <summary>The exact <c>sudo</c> argv, word for word a line of the aetheus-teamspeak grant.</summary>
    internal static string[] BuildSystemctlArgv(string verb, string unit) => ["-n", "/bin/systemctl", verb, unit];

    /// <summary>The line the task log shows when no TeamSpeak unit is installed.</summary>
    internal static string NoUnitMessage =>
        $"No TeamSpeak systemd unit is installed on this host (looked for {string.Join(" and ", Candidates)}).";
}
