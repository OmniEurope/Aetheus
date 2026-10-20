// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Collectors;

/// <summary> Turns the host's listening-socket listing into ports. Pure text, no process launching,
/// so the parsing is testable against captured output from both platforms. </summary>
public static class ListeningPortsParser
{
    /// <summary>
    /// Parses <c>ss -ltnpH</c> or <c>ss -lunpH</c>. Columns are
    /// <c>State Recv-Q Send-Q Local-Address:Port Peer-Address:Port [users:((...))]</c>, so the local
    /// endpoint is the fourth field. Handles IPv4 (<c>0.0.0.0:22</c>), IPv6 (<c>[::]:80</c>),
    /// interface-scoped IPv6 (<c>[fe80::1%eth0]:53</c>) and the wildcard form <c>*:9100</c>.
    /// The layout is the same for both protocols (UDP shows <c>UNCONN</c> where TCP shows
    /// <c>LISTEN</c>, in a column this parser does not read), so the caller labels the protocol from
    /// the command it ran rather than the parser guessing it back out of the text.
    /// The process name is present only for sockets the agent may inspect (its own, or any when it
    /// runs as root); its absence is normal and reported as an unknown holder, never guessed.
    /// </summary>
    public static List<(int Port, string Interface, string? Process)> ParseSs(string? output)
    {
        var result = new List<(int, string, string?)>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        foreach (var line in output.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4) continue;
            // A header line ("State Recv-Q ...") survives when -H is unsupported; it fails to parse
            // a port, so it drops out here rather than needing its own special case.
            if (TrySplitEndpoint(fields[3], out var port, out var address))
                result.Add((port, address, ExtractSsProcess(line)));
        }

        return result;
    }

    /// <summary>Reads the first process name out of <c>users:(("nginx",pid=812,fd=6))</c>.</summary>
    private static string? ExtractSsProcess(string line)
    {
        var marker = line.IndexOf("users:((\"", StringComparison.Ordinal);
        if (marker < 0) return null;
        var start = marker + "users:((\"".Length;
        var end = line.IndexOf('"', start);
        if (end <= start) return null;
        var name = line[start..end].Trim();
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// Parses the CSV emitted by
    /// <c>Get-NetTCPConnection -State Listen | Select-Object LocalAddress,LocalPort | ConvertTo-Csv</c>
    /// and, with the same two columns, by <c>Get-NetUDPEndpoint</c>.
    /// CSV rather than the default table because the table pads and truncates columns, which silently
    /// mangles IPv6 addresses.
    /// </summary>
    public static List<(int Port, string Interface)> ParseEndpointCsv(string? output)
    {
        var result = new List<(int, string)>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        var header = true;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (header)
            {
                // ConvertTo-Csv -NoTypeInformation still emits a header row; skip exactly one.
                header = false;
                if (trimmed.Contains("LocalPort", StringComparison.OrdinalIgnoreCase)) continue;
            }

            var cells = trimmed.Split(',');
            if (cells.Length < 2) continue;
            var address = Unquote(cells[0]);
            if (!int.TryParse(Unquote(cells[1]), out var port)) continue;
            if (port is <= 0 or > 65535) continue;
            result.Add((port, address.Length == 0 ? "0.0.0.0" : address));
        }

        return result;
    }

    /// <summary>
    /// Parses <c>docker ps --format {{.Names}}|{{.Ports}}</c> into "published host port → container
    /// name". Only published ports appear, which is exactly what occupies a host port; a container
    /// port with no host mapping is invisible to the host and is skipped here too.
    /// Keyed by protocol as well as number: Docker publishes <c>53/udp</c> and <c>53/tcp</c>
    /// separately, and merging them would name the wrong container as the holder of one of the two.
    /// </summary>
    public static Dictionary<(int Port, string Protocol), string> ParseDockerPortMap(string? output)
    {
        var map = new Dictionary<(int, string), string>();
        if (string.IsNullOrWhiteSpace(output)) return map;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var separator = trimmed.IndexOf('|', StringComparison.Ordinal);
            if (separator <= 0) continue;

            var name = trimmed[..separator].Trim();
            if (name.Length == 0) continue;

            // "0.0.0.0:10031->80/tcp, [::]:10031->80/tcp, 53/udp"
            foreach (var mapping in trimmed[(separator + 1)..].Split(','))
            {
                var arrow = mapping.IndexOf("->", StringComparison.Ordinal);
                if (arrow < 0) continue; // unpublished container port: not a host port
                if (!TrySplitEndpoint(mapping[..arrow].Trim(), out var hostPort, out _)) continue;
                map.TryAdd((hostPort, ProtocolOf(mapping[(arrow + 2)..])), name);
            }
        }

        return map;
    }

    /// <summary>
    /// Reads the protocol out of a Docker container-port side (<c>80/tcp</c>, <c>53/udp</c>). Anything
    /// unreadable falls back to TCP, which is what a published port is unless Docker says otherwise.
    /// </summary>
    private static string ProtocolOf(string containerSide)
    {
        var slash = containerSide.LastIndexOf('/');
        if (slash < 0) return PortRegistryLimits.TcpProtocol;
        var protocol = containerSide[(slash + 1)..].Trim();
        return string.Equals(protocol, PortRegistryLimits.UdpProtocol, StringComparison.OrdinalIgnoreCase)
            ? PortRegistryLimits.UdpProtocol
            : PortRegistryLimits.TcpProtocol;
    }

    /// <summary>Splits an <c>address:port</c> endpoint, keeping the address side intact for IPv6.</summary>
    private static bool TrySplitEndpoint(string endpoint, out int port, out string address)
    {
        port = 0;
        address = string.Empty;
        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || separator == endpoint.Length - 1) return false;

        if (!int.TryParse(endpoint[(separator + 1)..], out port) || port is <= 0 or > 65535)
            return false;

        address = endpoint[..separator];
        if (address == "*") address = "0.0.0.0";
        return address.Length > 0;
    }

    private static string Unquote(string cell)
    {
        var trimmed = cell.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }
}
