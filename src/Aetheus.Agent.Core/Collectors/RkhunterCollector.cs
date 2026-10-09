// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors;

public sealed partial class RkhunterCollector(ILogger<RkhunterCollector> logger, IShellRunner shell, Func<string, bool>? fileExists = null)
    : BaseShellCollector<RkhunterCollector>(logger, shell), IRkhunterCollector
{
    // Injectable binary-path probe so a unit test can force "not installed" deterministically. In
    // production it is File.Exists; without this seam the test reads the real host filesystem and
    // fails on any CI agent that actually has rkhunter installed (the VPS runner does).
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;

    private const string LogPath = "/var/log/rkhunter.log";
    private const string RkhunterBinPath = "/usr/bin/rkhunter";
    private const string MirrorsDatPath = "/var/lib/rkhunter/db/mirrors.dat";
    private const string RkhunterDatPath = "/var/lib/rkhunter/db/rkhunter.dat";

    public async Task<RkhunterDataDto> CollectAsync(CancellationToken ct = default)
    {
        // Item #10.1: IsInstalled is decoupled from the rest of the collection. If we found a
        // binary, the UI should show "RKHunter installed" - period. Downstream metadata
        // (version, DB info, last scan) is best-effort; a single failure must NOT flip the
        // section back to the install wizard.
        var binaryPath = await DetectBinaryAsync(ct).ConfigureAwait(false);
        if (binaryPath is null)
            return new RkhunterDataDto();

        var version = string.Empty;
        var dbVersion = string.Empty;
        var lastScanTime = DateTime.MinValue;
        var lastScanStatus = string.Empty;
        var warningCount = 0;
        var dbLastUpdated = DateTime.MinValue;

        try { version = await GetVersionAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { Logger.LogDebug(ex, "[RkhunterCollector] GetVersion failed"); }

        try { dbVersion = GetDatabaseVersion(); }
        catch (Exception ex) { Logger.LogDebug(ex, "[RkhunterCollector] GetDatabaseVersion failed"); }

        try { (lastScanTime, lastScanStatus, warningCount) = GetLastScanInfo(); }
        catch (Exception ex) { Logger.LogDebug(ex, "[RkhunterCollector] GetLastScanInfo failed"); }

        try { dbLastUpdated = GetDatabaseLastUpdated(); }
        catch (Exception ex) { Logger.LogDebug(ex, "[RkhunterCollector] GetDatabaseLastUpdated failed"); }

        return new RkhunterDataDto
        {
            IsInstalled = true,
            Version = version,
            DatabaseVersion = dbVersion,
            LastScanTime = lastScanTime,
            LastScanStatus = lastScanStatus,
            WarningCount = warningCount,
            DatabaseLastUpdated = dbLastUpdated
        };
    }

    // Robust detection: check the canonical paths first (no exec at all), then fall back to
    // `which`. On minimal images that don't ship `which` (some Debian slim variants drop it),
    // the path probe still catches /usr/bin and /usr/sbin installs, which covers ~all packages.
    private async Task<string?> DetectBinaryAsync(CancellationToken ct)
    {
        ReadOnlySpan<string> knownPaths = [RkhunterBinPath, "/usr/sbin/rkhunter", "/usr/local/bin/rkhunter", "/usr/local/sbin/rkhunter"];
        foreach (var path in knownPaths)
        {
            if (_fileExists(path))
                return path;
        }

        try
        {
            var res = await Shell.RunExecAsync("which", ["rkhunter"], ct).ConfigureAwait(false);
            return res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.StdOut)
                ? res.StdOut.Trim()
                : null;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[RkhunterCollector] `which rkhunter` failed");
            return null;
        }
    }

    private async Task<string> GetVersionAsync(CancellationToken ct)
    {
        try
        {
            var res = await Shell.RunExecAsync("rkhunter", ["--version"], ct).ConfigureAwait(false);
            // Was: ... 2>&1 | head -1 | grep -oP '\d+\.\d+\.\d+'
            var match = VersionNumberRegex().Match(res.StdOut);
            if (!match.Success)
                match = VersionNumberRegex().Match(res.StdErr);
            return match.Success ? match.Value : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private string GetDatabaseVersion()
    {
        try
        {
            if (_fileExists(MirrorsDatPath))
            {
                foreach (var line in File.ReadLines(MirrorsDatPath))
                {
                    if (line.Contains("mirrors.dat", StringComparison.Ordinal))
                        return line.Trim();
                }
            }
            if (_fileExists(RkhunterDatPath))
                return new DateTimeOffset(File.GetLastWriteTimeUtc(RkhunterDatPath))
                    .ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to read rkhunter database version");
        }
        return string.Empty;
    }

    private (DateTime LastScanTime, string LastScanStatus, int WarningCount) GetLastScanInfo()
    {
        try
        {
            if (!_fileExists(LogPath))
                return (DateTime.MinValue, "none", 0);

            return ParseLastScan(File.ReadLines(LogPath));
        }
        catch
        {
            return (DateTime.MinValue, "none", 0);
        }
    }

    /// <summary>
    /// Reads the most recent scan of an rkhunter log: everything from its last <c>[Started]</c> line on
    /// (the whole log when no scan start was logged). Every warning of that scan is counted, and no
    /// line of an earlier scan is, however many lines either one holds.
    /// </summary>
    internal static (DateTime LastScanTime, string LastScanStatus, int WarningCount) ParseLastScan(IEnumerable<string> lines)
    {
        string? startedLine = null;
        var hasSummary = false;
        var warningCount = 0;
        foreach (var line in lines)
        {
            if (line.Contains("[Started]", StringComparison.Ordinal))
            {
                startedLine = line;
                hasSummary = false;
                warningCount = 0;
                continue;
            }
            if (line.Contains("System checks summary", StringComparison.OrdinalIgnoreCase))
                hasSummary = true;
            warningCount += WarningRegex().Matches(line).Count;
        }

        var lastScanTime = DateTime.MinValue;
        var startMatch = startedLine is null ? Match.Empty : StartedRegex().Match(startedLine);
        if (startMatch.Success && DateTime.TryParse(startMatch.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            lastScanTime = parsed;

        if (!hasSummary)
            return (lastScanTime, "none", 0);
        return (lastScanTime, warningCount > 0 ? "warning" : "clean", warningCount);
    }

    private DateTime GetDatabaseLastUpdated()
    {
        try
        {
            if (_fileExists(RkhunterDatPath))
                return File.GetLastWriteTimeUtc(RkhunterDatPath);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to read rkhunter database mtime");
        }
        return DateTime.MinValue;
    }

    [GeneratedRegex(@"\[Started\]\s*(.+)")]
    private static partial Regex StartedRegex();

    [GeneratedRegex(@"\[ Warning \]")]
    private static partial Regex WarningRegex();

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionNumberRegex();
}
