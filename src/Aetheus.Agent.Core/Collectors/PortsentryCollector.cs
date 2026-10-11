// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors;

public sealed partial class PortsentryCollector(
    ILogger<PortsentryCollector> logger,
    IShellRunner shell,
    Func<string, bool>? fileExists = null,
    Func<string, string>? readAllText = null,
    Func<string, CancellationToken, Task<string>>? readAllTextAsync = null)
    : BaseShellCollector<PortsentryCollector>(logger, shell), IPortsentryCollector
{
    private static readonly string[] KnownPaths =
    [
        "/usr/sbin/portsentry",
        "/usr/local/sbin/portsentry"
    ];

    private const string ConfigPath = "/etc/portsentry/portsentry.conf";
    private const string DebianDefaultsPath = "/etc/default/portsentry";

    /// <summary>
    /// <c>BLOCKED_FILE</c> of the Debian/Ubuntu package's <c>portsentry.conf</c>, used when the config names none.
    /// PortSentry 1.2 appends the detection mode to it: <c>portsentry.blocked.tcp</c>, <c>.atcp</c>, <c>.stcp</c>,
    /// <c>.udp</c>, <c>.audp</c>, <c>.sudp</c>, one file per running mode.
    /// </summary>
    internal const string DefaultBlockedFileBase = "/var/lib/portsentry/portsentry.blocked";

    private static readonly (string Suffix, string Protocol)[] BlockedFileModes =
    [
        ("tcp", "TCP"), ("atcp", "TCP"), ("stcp", "TCP"),
        ("udp", "UDP"), ("audp", "UDP"), ("sudp", "UDP")
    ];

    public async Task<PortsentryDataDto> CollectAsync(CancellationToken ct = default)
    {
        try
        {
            var binaryPath = await DetectBinaryAsync(ct).ConfigureAwait(false);
            if (binaryPath is null)
                return new PortsentryDataDto();

            var isRunning = await IsRunningAsync(ct).ConfigureAwait(false);
            var version = await GetVersionAsync(ct).ConfigureAwait(false);
            var mode = GetMode();
            var (tcpPorts, udpPorts) = GetMonitoredPorts();
            var blockedIps = await GetBlockedIpsAsync(ct).ConfigureAwait(false);

            return new PortsentryDataDto
            {
                IsInstalled = true,
                IsRunning = isRunning,
                Version = version,
                Mode = mode,
                TcpPorts = tcpPorts,
                UdpPorts = udpPorts,
                BlockedCount = blockedIps.Count,
                BlockedIps = blockedIps
            };
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to collect PortSentry data");
            return new PortsentryDataDto();
        }
    }

    private async Task<string?> DetectBinaryAsync(CancellationToken ct)
    {
        foreach (var path in KnownPaths)
        {
            if ((fileExists ?? File.Exists)(path))
                return path;
        }

        var res = await Shell.RunExecAsync("which", ["portsentry"], ct).ConfigureAwait(false);
        return res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.StdOut) ? res.StdOut.Trim() : null;
    }

    private async Task<bool> IsRunningAsync(CancellationToken ct)
    {
        var svc = await Shell.RunExecAsync("systemctl", ["is-active", "portsentry"], ct).ConfigureAwait(false);
        if (svc.StdOut.Trim().Equals("active", StringComparison.OrdinalIgnoreCase))
            return true;

        var pidof = await Shell.RunExecAsync("pidof", ["portsentry"], ct).ConfigureAwait(false);
        return pidof.ExitCode == 0 && !string.IsNullOrWhiteSpace(pidof.StdOut);
    }

    private async Task<string> GetVersionAsync(CancellationToken ct)
    {
        try
        {
            // Was: dpkg -l portsentry | grep '^ii' | awk '{print $3}'  (Debian)
            var dpkg = await Shell.RunExecAsync("dpkg", ["-l", "portsentry"], ct).ConfigureAwait(false);
            foreach (var line in dpkg.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("ii", StringComparison.Ordinal)) continue;
                var cols = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length >= 3) return cols[2];
            }

            // Fallback: rpm -q portsentry --qf '%{VERSION}'  (RHEL)
            var rpm = await Shell.RunExecAsync("rpm", ["-q", "portsentry", "--qf", "%{VERSION}"], ct).ConfigureAwait(false);
            return rpm.ExitCode == 0 ? rpm.StdOut.Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private string ReadConfigLines() => ReadText(ConfigPath);

    private string ReadText(string path)
    {
        try
        {
            return (fileExists ?? File.Exists)(path)
                ? (readAllText ?? File.ReadAllText)(path)
                : string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to read {Path}", path);
            return string.Empty;
        }
    }

    private string GetMode()
    {
        // The Debian package's portsentry-setup writes TCP_MODE to /etc/default/portsentry, which the init
        // script passes to the daemon; portsentry.conf only carries it on hand-made installs.
        var relevant = (ReadText(DebianDefaultsPath) + "\n" + ReadConfigLines())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.TrimStart().StartsWith("TCP_MODE", StringComparison.Ordinal)
                     || l.TrimStart().StartsWith("BLOCK_TCP", StringComparison.Ordinal));
        var joined = string.Join('\n', relevant);

        if (joined.Contains("atcp", StringComparison.OrdinalIgnoreCase))
            return "atcp";
        if (joined.Contains("stcp", StringComparison.OrdinalIgnoreCase))
            return "stcp";
        return "tcp";
    }

    private (string TcpPorts, string UdpPorts) GetMonitoredPorts()
    {
        var tcp = string.Empty;
        var udp = string.Empty;

        foreach (var line in ReadConfigLines().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("TCP_PORTS=", StringComparison.Ordinal))
                tcp = trimmed["TCP_PORTS=".Length..].Trim('"');
            else if (trimmed.StartsWith("UDP_PORTS=", StringComparison.Ordinal))
                udp = trimmed["UDP_PORTS=".Length..].Trim('"');
        }

        return (tcp, udp);
    }

    private async Task<List<PortsentryBlockedIpDto>> GetBlockedIpsAsync(CancellationToken ct)
    {
        var result = new List<PortsentryBlockedIpDto>();
        try
        {
            var blockedFileBase = BlockedFileBase(ReadConfigLines());
            foreach (var (suffix, protocol) in BlockedFileModes)
                await ParseBlockedFileAsync($"{blockedFileBase}.{suffix}", protocol, result, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to parse PortSentry blocked files");
        }
        return result;
    }

    private async Task ParseBlockedFileAsync(string path, string protocol, List<PortsentryBlockedIpDto> result, CancellationToken ct)
    {
        string output;
        try
        {
            output = (fileExists ?? File.Exists)(path)
                ? await (readAllTextAsync ?? File.ReadAllTextAsync)(path, ct).ConfigureAwait(false)
                : string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to read {Path}", path);
            return;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ParseBlockedLine(line, protocol) is { } blocked)
                result.Add(blocked);
        }
    }

    /// <summary>The <c>BLOCKED_FILE</c> the config sets (quoted or not), else the Debian package's default.</summary>
    internal static string BlockedFileBase(string config)
    {
        foreach (var line in config.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("BLOCKED_FILE=", StringComparison.Ordinal)) continue;
            var value = trimmed["BLOCKED_FILE=".Length..].Trim().Trim('"');
            if (value.Length > 0) return value;
        }
        return DefaultBlockedFileBase;
    }

    /// <summary>
    /// One line of a PortSentry 1.2 blocked (or history) file, as its <c>WriteBlocked</c> writes it:
    /// <c>&lt;epoch&gt; - &lt;MM/DD/YYYY HH:MM:SS&gt; Host: &lt;resolved name&gt;/&lt;ip&gt; Port: &lt;port&gt; &lt;TCP|UDP&gt; Blocked</c>
    /// (the name is the IP itself when <c>RESOLVE_HOST</c> is off). <see cref="PortsentryBlockedIpDto.BlockedAt"/> is
    /// the epoch (UTC), else the local date of this host, and stays <see cref="DateTime.MinValue"/> ("unknown")
    /// only for a line that carries no date, such as a bare IP. Null for a comment or an unreadable line.
    /// </summary>
    internal static PortsentryBlockedIpDto? ParseBlockedLine(string line, string fileProtocol)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            return null;

        var match = BlockedLineRegex().Match(trimmed);
        if (!match.Success)
        {
            // Not a PortSentry line; a bare address is still a blocked host, with no known date.
            return IPAddress.TryParse(trimmed, out _) && !trimmed.Contains('/', StringComparison.Ordinal)
                ? new PortsentryBlockedIpDto { IpAddress = trimmed, Protocol = fileProtocol, Reason = "Blocked by PortSentry" }
                : null;
        }

        var host = match.Groups["host"].Value;
        var ip = host[(host.LastIndexOf('/') + 1)..];
        if (!IPAddress.TryParse(ip, out _))
            return null;

        var protocol = match.Groups["proto"].Value.ToUpperInvariant();
        return new PortsentryBlockedIpDto
        {
            IpAddress = ip,
            Protocol = protocol is "TCP" or "UDP" ? protocol : fileProtocol,
            BlockedAt = BlockedAt(match.Groups["epoch"].Value, match.Groups["date"].Value),
            Reason = $"Blocked by PortSentry (port {match.Groups["port"].Value})"
        };
    }

    private static DateTime BlockedAt(string epoch, string date)
    {
        if (long.TryParse(epoch, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0 && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        // The agent runs on the host that wrote the line, so its local time zone is the line's.
        return DateTime.TryParseExact(date, "MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            ? local.ToUniversalTime()
            : DateTime.MinValue;
    }

    [GeneratedRegex(@"^(?:(?<epoch>\d+)\s+-\s+)?(?:(?<date>\d{2}/\d{2}/\d{4}\s\d{2}:\d{2}:\d{2})\s+)?Host:\s+(?<host>\S+)\s+Port:\s+(?<port>\d+)\s+(?<proto>[A-Za-z]+)\s+Blocked", RegexOptions.CultureInvariant)]
    private static partial Regex BlockedLineRegex();
}
