// SPDX-License-Identifier: EUPL-1.2

using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Front.Components.Shared;

public static class DisplayFormatting
{
    private static readonly Regex DockerSizePattern = new(
        @"^(?<n>\d+(?:[.,]\d+)?)\s*(?<u>[A-Za-z]+)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private const double Kilo = 1024d;
    private const double Mega = Kilo * 1024d;
    private const double Giga = Mega * 1024d;

    /// <summary>The number of a counter badge in the header or the menu (tasks, approvals, notifications, alerts):
    /// 1 to 9 as they are, "9+" above (user decision of 2026-10-24: a larger figure tells nothing more).</summary>
    public static string BadgeCount(int count) =>
        count > 9 ? "9+" : count.ToString(CultureInfo.CurrentCulture);

    public static OmniTone TaskLogBadge(TaskLogLevel level) => level switch
    {
        TaskLogLevel.Error => OmniTone.Danger,
        TaskLogLevel.Warning => OmniTone.Warning,
        TaskLogLevel.Info => OmniTone.Accent,
        _ => OmniTone.Neutral
    };

    /// <summary>The one size format of the site (recette R-37, R-47): French units o, Ko, Mo and Go, base 1024
    /// (the figures read like <c>pg_size_pretty</c>, <c>df</c> and the Docker engine), one decimal at most,
    /// the culture decimal separator ("3,9 Go"). Go from one gigabyte, Mo from one megabyte, never a binary
    /// unit name (KiB, MiB, GiB) nor an English one (KB, MB, GB).</summary>
    public static string Size(long bytes)
    {
        var value = Math.Max(0, bytes);
        return value switch
        {
            >= 1_073_741_824 => string.Create(CultureInfo.CurrentCulture, $"{value / Giga:0.#} Go"),
            >= 1_048_576 => string.Create(CultureInfo.CurrentCulture, $"{value / Mega:0.#} Mo"),
            >= 1024 => string.Create(CultureInfo.CurrentCulture, $"{value / Kilo:0.#} Ko"),
            _ => string.Create(CultureInfo.CurrentCulture, $"{value} o")
        };
    }

    /// <summary><see cref="Size(long)"/> of a figure the agent reports in Go (1024³ bytes), such as the disk
    /// sizes of a heartbeat: the same units and rounding, never a second format.</summary>
    public static string SizeFromGo(double gigabytes) => Size(ToBytes(gigabytes, Giga));

    /// <summary><see cref="Size(long)"/> of a figure the agent reports in Mo (1024² bytes), such as the
    /// memory of a heartbeat or of a container.</summary>
    public static string SizeFromMo(double megabytes) => Size(ToBytes(megabytes, Mega));

    /// <summary>The size text the Docker CLI prints ("182MB", "1.2GB", "512kB", "0B"; Docker counts in
    /// powers of 1000, "GiB" in powers of 1024) in the site format. A text that is not a size is shown
    /// as received rather than as a made-up figure.</summary>
    public static string DockerSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var raw = text.Trim();
        var match = DockerSizePattern.Match(raw);
        if (!match.Success
            || !double.TryParse(match.Groups["n"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return raw;
        var unit = match.Groups["u"].Value.ToUpperInvariant() switch
        {
            "B" => 1d,
            "KB" => 1e3,
            "MB" => 1e6,
            "GB" => 1e9,
            "TB" => 1e12,
            "KIB" => Kilo,
            "MIB" => Mega,
            "GIB" => Giga,
            "TIB" => Giga * Kilo,
            _ => 0d
        };
        return unit == 0d ? raw : Size(ToBytes(number, unit));
    }

    private static long ToBytes(double value, double unit) =>
        double.IsFinite(value) ? (long)Math.Round(Math.Max(0d, value) * unit) : 0;

    /// <summary>The one duration format of the site (recette R-23): under a minute the seconds ("42s"), under
    /// an hour the whole minutes without seconds ("42min"), from an hour the hours and minutes ("1h44min").
    /// A negative span reads as zero.</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration.TotalHours >= 1)
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours}h{duration.Minutes:D2}min");
        if (duration.TotalMinutes >= 1)
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMinutes}min");
        return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalSeconds}s");
    }

    /// <summary>The measurement variant of <see cref="Duration"/> (recette R-23 follow-up): same units, but the
    /// seconds are kept below ten minutes ("42s", "1min40s", "9min05s") so that the average, last and CPU columns
    /// of the run Gantt and its axis ticks stay distinguishable ("1min40s", "2min05s" and "1min30s" would lose
    /// their difference in <see cref="Duration"/>). From ten minutes it is exactly <see cref="Duration"/>.</summary>
    public static string PreciseDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration.TotalMinutes is < 1 or >= 10) return Duration(duration);
        return string.Create(CultureInfo.InvariantCulture, $"{duration.Minutes}min{duration.Seconds:D2}s");
    }

    public static string ShortSha(string commitHash) =>
        commitHash[..Math.Min(8, commitHash.Length)];
}
