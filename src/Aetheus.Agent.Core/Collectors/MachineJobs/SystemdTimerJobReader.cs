// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Collectors.MachineReboot;
using Aetheus.Agent.Core.Services;
using Aetheus.Shared.Components.Servers;

namespace Aetheus.Agent.Core.Collectors.MachineJobs;

/// <summary>What the timers gave: the jobs, and the whitelisted timers the reader saw but could not place in time.</summary>
internal sealed record TimerJobsReading(List<MachineJobEntryDto> Jobs, int Skipped);

/// <summary>
/// The whitelisted systemd timers (<see cref="MachineTimerWhitelist"/>) as planner jobs, read with argv only
/// (<see cref="IShellRunner.RunExecAsync(string, IReadOnlyList{string}, CancellationToken, TimeSpan?)"/>, no shell, read-only
/// <c>systemctl</c> verbs). <c>systemctl list-timers --all --output=json</c> names the timers and their next elapse; it gives no
/// calendar, so each whitelisted timer is then asked for its <c>TimersCalendar</c> and <c>RandomizedDelayUSec</c>
/// (<c>systemctl show</c>). A calendar the <see cref="OnCalendarConverter"/> reads becomes a five-field expression; a timer
/// with a random delay (its real time is anywhere in the delay) or with no calendar (<c>OnBootSec</c>...) is reported as the
/// single next instant systemd computed. A systemd too old for <c>--output=json</c> falls back to the plain table, from which
/// only the unit names are read (no next instant). A <c>systemctl</c> that is absent or fails reads as no timers.
/// </summary>
internal static partial class SystemdTimerJobReader
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    // The latest instant DateTimeOffset.FromUnixTimeMilliseconds accepts.
    private const long MaxUnixMilliseconds = 253402300799999;

    [GeneratedRegex(@"OnCalendar=(?<v>[^;}]*?)\s*[;}]")]
    private static partial Regex CalendarPattern();

    [GeneratedRegex(@"(?<![\w.@\\:-])(?<u>[A-Za-z0-9_@:.\\\-]+\.timer)(?![\w.@\\:-])")]
    private static partial Regex UnitPattern();

    [GeneratedRegex(@"^0+(us|ms|s|sec|min|m|h)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ZeroDelayPattern();

    public static async Task<TimerJobsReading> ReadAsync(IShellRunner shell, string? machineZoneId, CancellationToken ct)
    {
        List<MachineJobEntryDto> jobs = [];
        var skipped = 0;
        var listed = await ListTimersAsync(shell, ct).ConfigureAwait(false);
        foreach (var (unit, next) in listed.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            // The whitelist is exact, so the unit name handed to systemctl below is always one of the fixed names.
            if (!MachineTimerWhitelist.Contains(unit)) continue;
            var found = await ReadUnitAsync(shell, unit, next, machineZoneId, ct).ConfigureAwait(false);
            jobs.AddRange(found.Jobs);
            skipped += found.Skipped;
        }

        return new TimerJobsReading(jobs, skipped);
    }

    private static async Task<TimerJobsReading> ReadUnitAsync(
        IShellRunner shell, string unit, DateTime? next, string? machineZoneId, CancellationToken ct)
    {
        var properties = await ShowAsync(shell, unit, ct).ConfigureAwait(false);
        var randomized = properties.TryGetValue("RandomizedDelayUSec", out var delay) && IsDelay(delay);
        List<MachineJobEntryDto> jobs = [];
        var skipped = 0;
        if (!randomized && properties.TryGetValue("TimersCalendar", out var calendars))
        {
            foreach (Match calendar in CalendarPattern().Matches(calendars))
            {
                if (OnCalendarConverter.TryConvert(calendar.Groups["v"].Value, out var cron, out var zone)
                    && (zone ?? machineZoneId) is { } zoneId)
                {
                    jobs.Add(Job(unit) with { CronExpression = cron, TimeZoneId = zoneId });
                }
                else
                {
                    skipped++;
                }
            }
        }

        // No usable calendar (a monotonic timer, a random delay, a form the converter does not read): the one instant systemd
        // computed is the only time known.
        if (jobs.Count == 0 && next is { } at)
        {
            jobs.Add(Job(unit) with { AtUtc = at });
            skipped = 0;
        }
        else if (jobs.Count == 0 && skipped == 0)
        {
            skipped = 1;
        }

        return new TimerJobsReading(jobs, skipped);
    }

    private static MachineJobEntryDto Job(string unit) =>
        new() { Source = MachineJobSource.SystemdTimer, Name = unit, Detail = unit };

    private static bool IsDelay(string value)
    {
        var text = value.Trim();
        return text.Length > 0 && !ZeroDelayPattern().IsMatch(text);
    }

    /// <summary>The timers systemd knows, by unit, with the next instant when it gave one.</summary>
    private static async Task<Dictionary<string, DateTime?>> ListTimersAsync(IShellRunner shell, CancellationToken ct)
    {
        var json = await RunAsync(shell, ["list-timers", "--all", "--no-pager", "--output=json"], ct).ConfigureAwait(false);
        if (json is not null && ParseJson(json) is { } parsed) return parsed;

        var table = await RunAsync(shell, ["list-timers", "--all", "--no-pager", "--no-legend"], ct).ConfigureAwait(false);
        return table is null ? [] : ParseTable(table);
    }

    private static Dictionary<string, DateTime?>? ParseJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            Dictionary<string, DateTime?> timers = new(StringComparer.Ordinal);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("unit", out var unit) || unit.ValueKind != JsonValueKind.String
                    || unit.GetString() is not { Length: > 0 } name)
                {
                    continue;
                }

                timers[name] = element.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.Number
                    && next.TryGetInt64(out var microseconds) && microseconds / 1000 is > 0 and <= MaxUnixMilliseconds
                    ? DateTimeOffset.FromUnixTimeMilliseconds(microseconds / 1000).UtcDateTime
                    : null;
            }

            return timers;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The table is localized and wraps dates in several words: only the unit names (words ending in .timer) are read.
    private static Dictionary<string, DateTime?> ParseTable(string table)
    {
        Dictionary<string, DateTime?> timers = new(StringComparer.Ordinal);
        foreach (var line in table.Split('\n'))
        {
            if (UnitPattern().Match(line) is { Success: true } unit) timers[unit.Groups["u"].Value] = null;
        }

        return timers;
    }

    /// <summary>The <c>Key=Value</c> lines <c>systemctl show</c> prints for the unit's two properties.</summary>
    private static async Task<Dictionary<string, string>> ShowAsync(IShellRunner shell, string unit, CancellationToken ct)
    {
        Dictionary<string, string> properties = new(StringComparer.Ordinal);
        var output = await RunAsync(
            shell, ["show", "--no-pager", "--property=TimersCalendar", "--property=RandomizedDelayUSec", "--", unit], ct).ConfigureAwait(false);
        if (output is null) return properties;
        foreach (var line in output.Split('\n'))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0) properties[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return properties;
    }

    /// <summary>stdout of a <c>systemctl</c> call; null when it is absent, times out or exits non-zero.</summary>
    private static async Task<string?> RunAsync(IShellRunner shell, string[] args, CancellationToken ct)
    {
        try
        {
            var result = await shell.RunExecAsync("systemctl", args, ct, CommandTimeout).ConfigureAwait(false);
            return result.ExitCode == 0 ? result.StdOut : null;
        }
        catch (Win32Exception)
        {
            // No systemctl on this host (a container, a non-systemd distribution): no timers to read.
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The command timed out: the heartbeat must not wait on it.
            return null;
        }
    }
}
