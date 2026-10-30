// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Recette R-113: puts the free text of <c>docker ps --format {{.Status}}</c> ("Up 2 hours (healthy)",
/// "Exited (0) 3 minutes ago", "Created"...) in the user's language. Docker writes it in English from
/// a fixed grammar (state, optional exit code, a human duration, optional health), so it is read back
/// into its parts and rebuilt from resources. A text that does not match the grammar is returned as
/// Docker wrote it, never hidden and never thrown on.
/// </summary>
internal static partial class DockerContainerStatusText
{
    // "Up 2 hours", "Up About an hour (healthy)", "Up 3 days (Paused)", "Up 5 minutes (health: starting)".
    [GeneratedRegex(@"^Up (?<duration>.+?)(?: \((?<flag>healthy|unhealthy|health: starting|Paused)\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex UpPattern();

    // "Exited (0) 3 minutes ago", "Restarting (1) 5 seconds ago".
    [GeneratedRegex(@"^(?<state>Exited|Restarting) \((?<code>-?\d+)\) (?<duration>.+?) ago$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    // go-units HumanDuration: "Less than a second", "1 second", "5 seconds", "About a minute", "About an hour",
    // "N minutes|hours|days|weeks|months|years".
    [GeneratedRegex(@"^(?<count>\d+) (?<unit>second|minute|hour|day|week|month|year)s?$", RegexOptions.CultureInvariant)]
    private static partial Regex CountedDurationPattern();

    /// <summary>The status in the user's language, or <paramref name="status"/> unchanged when it is not one Docker's grammar produces.</summary>
    public static string Format(string? status, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (string.IsNullOrWhiteSpace(status))
            return status ?? string.Empty;

        var text = status.Trim();
        switch (text)
        {
            case "Created": return localizer["DockerStatusCreated"].Value;
            case "Dead": return localizer["DockerStatusDead"].Value;
            case "Paused": return localizer["DockerStatusPaused"].Value;
            case "Removal In Progress": return localizer["DockerStatusRemoving"].Value;
        }

        var up = UpPattern().Match(text);
        if (up.Success && TryDuration(up.Groups["duration"].Value, localizer, out var upDuration))
        {
            var running = localizer["DockerStatusUp", upDuration].Value;
            var flag = up.Groups["flag"];
            return flag.Success
                ? $"{running} ({localizer[FlagKey(flag.Value)].Value})"
                : running;
        }

        var coded = CodePattern().Match(text);
        if (coded.Success && TryDuration(coded.Groups["duration"].Value, localizer, out var codedDuration))
        {
            var key = coded.Groups["state"].Value == "Exited" ? "DockerStatusExited" : "DockerStatusRestarting";
            return localizer[key, coded.Groups["code"].Value, codedDuration].Value;
        }

        return status;
    }

    // "running(2)", "running(1), exited(1)": `docker compose ls` counts containers per state.
    [GeneratedRegex(@"^(?<state>running|exited|restarting|paused|created|dead|removing)\((?<count>\d+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex ComposePartPattern();

    /// <summary>
    /// A compose stack's status (<c>docker compose ls</c>: "running(1), exited(1)") in the user's language,
    /// as "En marche (1), arrêté (1)". Any text that is not a comma-separated list of known states with a
    /// count is returned as Docker wrote it.
    /// </summary>
    public static string FormatCompose(string? status, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (string.IsNullOrWhiteSpace(status))
            return status ?? string.Empty;

        var parts = new List<string>();
        foreach (var raw in status.Split(',', StringSplitOptions.TrimEntries))
        {
            var match = ComposePartPattern().Match(raw);
            if (!match.Success)
                return status;
            parts.Add($"{localizer[$"DockerComposeState_{match.Groups["state"].Value}"].Value} ({match.Groups["count"].Value})");
        }

        var text = string.Join(", ", parts);
        return char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
    }

    private static string FlagKey(string flag) => flag switch
    {
        "healthy" => "DockerStatusHealthy",
        "unhealthy" => "DockerStatusUnhealthy",
        "health: starting" => "DockerStatusHealthStarting",
        _ => "DockerStatusPausedFlag"
    };

    private static bool TryDuration(string duration, IStringLocalizer localizer, out string text)
    {
        switch (duration)
        {
            case "Less than a second": text = localizer["DockerDurationLessThanSecond"].Value; return true;
            case "About a minute": text = localizer["DockerDurationAboutMinute"].Value; return true;
            case "About an hour": text = localizer["DockerDurationAboutHour"].Value; return true;
        }

        var counted = CountedDurationPattern().Match(duration);
        if (!counted.Success)
        {
            text = string.Empty;
            return false;
        }

        var count = int.Parse(counted.Groups["count"].Value, CultureInfo.InvariantCulture);
        var unit = counted.Groups["unit"].Value;
        var key = count == 1 ? $"DockerDuration_{unit}" : $"DockerDuration_{unit}s";
        text = localizer[key, count.ToString(CultureInfo.CurrentCulture)].Value;
        return true;
    }
}
