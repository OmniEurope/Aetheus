// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Agent.Core.Operations;

/// <summary>Reads the lines <c>docker stats</c> prints for a scanner container: CPU, memory, disk and
/// network figures. Kept apart from <see cref="ScannerProcessRunner"/> so the runner and the usage record it
/// publishes do not depend on each other.</summary>
internal static class DockerStatsParser
{
    internal static bool TryParseUsage(string value, out double cpuPercent, out long memoryBytes)
    {
        cpuPercent = 0;
        memoryBytes = 0;
        var parts = value.Trim().Split('|', 2);
        if (parts.Length != 2
            || !double.TryParse(parts[0].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out cpuPercent))
            return false;
        var memory = parts[1].Split('/', 2)[0].Trim();
        return TryParseBytes(memory, out memoryBytes);
    }

    internal static bool TryParseUsage(
        string value,
        out double cpuPercent,
        out long memoryBytes,
        out long diskReadBytes,
        out long diskWrittenBytes,
        out long networkReceivedBytes,
        out long networkSentBytes)
    {
        cpuPercent = 0;
        memoryBytes = 0;
        diskReadBytes = 0;
        diskWrittenBytes = 0;
        networkReceivedBytes = 0;
        networkSentBytes = 0;
        var parts = value.Trim().Split('|', 4);
        return parts.Length == 4
               && TryParseUsage($"{parts[0]}|{parts[1]}", out cpuPercent, out memoryBytes)
               && TryParseIoPair(parts[2], out diskReadBytes, out diskWrittenBytes)
               && TryParseIoPair(parts[3], out networkReceivedBytes, out networkSentBytes);
    }

    private static bool TryParseIoPair(string value, out long first, out long second)
    {
        first = 0;
        second = 0;
        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && TryParseBytes(parts[0], out first)
               && TryParseBytes(parts[1], out second);
    }

    private static bool TryParseBytes(string value, out long bytes)
    {
        bytes = 0;
        var unitStart = value.TakeWhile(character => char.IsDigit(character) || character is '.' or ',').Count();
        if (unitStart == 0 || !double.TryParse(value[..unitStart].Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var amount)) return false;
        var unit = value[unitStart..].Trim().ToUpperInvariant();
        var factor = unit switch
        {
            "B" => 1d,
            "KB" => 1_000d,
            "KIB" => 1_024d,
            "MB" => 1_000_000d,
            "MIB" => 1_048_576d,
            "GB" => 1_000_000_000d,
            "GIB" => 1_073_741_824d,
            _ => 0d
        };
        if (factor == 0) return false;
        bytes = checked((long)Math.Round(amount * factor));
        return true;
    }
}
