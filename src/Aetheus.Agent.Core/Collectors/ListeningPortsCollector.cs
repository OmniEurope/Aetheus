// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Collectors;

/// <summary>Scan TCP and UDP separately through unprivileged argv-only probes, preserving each
/// successful protocol result independently. Resolve holders by container, then process, then
/// unknown so proxy names do not obscure container ownership.</summary>
public sealed class ListeningPortsCollector(
    ILogger<ListeningPortsCollector> logger,
    IShellRunner shell)
    : BaseShellCollector<ListeningPortsCollector>(logger, shell), IListeningPortsCollector
{
    private const string UnknownHolder = "host process";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private const string NetTcpScript =
        "Get-NetTCPConnection -State Listen | Select-Object LocalAddress,LocalPort | ConvertTo-Csv -NoTypeInformation";

    /// <summary>
    /// The UDP counterpart. There is no listening STATE to filter on: a bound UDP socket is the whole
    /// notion of occupancy for that stack, and <c>Get-NetUDPEndpoint</c> lists exactly those.
    /// </summary>
    private const string NetUdpScript =
        "Get-NetUDPEndpoint | Select-Object LocalAddress,LocalPort | ConvertTo-Csv -NoTypeInformation";

    public async Task<List<ObservedPortDto>> CollectAsync(CancellationToken ct = default)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var tcp = isWindows
            ? await CollectWindowsAsync(NetTcpScript, "Get-NetTCPConnection", ct).ConfigureAwait(false)
            : await CollectLinuxAsync("-ltnpH", ct).ConfigureAwait(false);
        var udp = isWindows
            ? await CollectWindowsAsync(NetUdpScript, "Get-NetUDPEndpoint", ct).ConfigureAwait(false)
            : await CollectLinuxAsync("-lunpH", ct).ConfigureAwait(false);
        if (tcp.Count == 0 && udp.Count == 0) return [];

        var containers = await CollectDockerPortMapAsync(ct).ConfigureAwait(false);

        var byPort = new Dictionary<(int Port, string Protocol), ObservedPortDto>();
        Absorb(byPort, tcp, PortRegistryLimits.TcpProtocol, containers);
        Absorb(byPort, udp, PortRegistryLimits.UdpProtocol, containers);

        return
        [
            .. byPort.Values
                .OrderBy(observed => observed.Port)
                .ThenBy(observed => observed.Protocol, StringComparer.Ordinal)
        ];
    }

    private static void Absorb(
        Dictionary<(int Port, string Protocol), ObservedPortDto> byPort,
        List<(int Port, string Interface, string? Process)> listeners,
        string protocol,
        Dictionary<(int Port, string Protocol), string> containers)
    {
        foreach (var (port, iface, process) in listeners)
        {
            var holder = containers.TryGetValue((port, protocol), out var container)
                ? container
                : process ?? UnknownHolder;

            // The same port on several interfaces is one occupied port. The first sighting wins, and a
            // later one only upgrades the holder when the earlier one was the unknown fallback.
            var key = (port, protocol);
            if (byPort.TryGetValue(key, out var existing))
            {
                if (existing.Holder == UnknownHolder && holder != UnknownHolder)
                    byPort[key] = existing with { Holder = Bound(holder) };
                continue;
            }

            byPort[key] = new ObservedPortDto
            {
                Port = port,
                Protocol = protocol,
                Holder = Bound(holder),
                Interface = Bound(iface, PortRegistryLimits.MaxInterfaceLength)
            };
        }
    }

    private async Task<List<(int Port, string Interface, string? Process)>> CollectLinuxAsync(
        string arguments, CancellationToken ct)
    {
        try
        {
            // -l listening/bound, -t tcp or -u udp, -n numeric (no DNS round-trip), -p processes when
            // readable, -H no header.
            var result = await Shell
                .RunExecAsync("ss", [arguments], ct, ProbeTimeout)
                .ConfigureAwait(false);
            if (result.ExitCode == 0)
                return ListeningPortsParser.ParseSs(result.StdOut);

            Logger.LogDebug(
                "[ListeningPortsCollector] ss {Arguments} exited {ExitCode}: {StdErr}",
                arguments, result.ExitCode, result.StdErr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "[ListeningPortsCollector] ss {Arguments} probe failed", arguments);
        }

        return [];
    }

    private async Task<List<(int Port, string Interface, string? Process)>> CollectWindowsAsync(
        string script, string probeName, CancellationToken ct)
    {
        try
        {
            var result = await Shell
                .RunExecAsync(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command", script],
                    ct,
                    ProbeTimeout)
                .ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                // Both cmdlets expose OwningProcess, not a process NAME, and resolving it would need a
                // second lookup per socket; the holder stays unknown unless Docker names it.
                return
                [
                    .. ListeningPortsParser
                        .ParseEndpointCsv(result.StdOut)
                        .Select(entry => (entry.Port, entry.Interface, (string?)null))
                ];
            }

            Logger.LogDebug(
                "[ListeningPortsCollector] {Probe} exited {ExitCode}: {StdErr}",
                probeName, result.ExitCode, result.StdErr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "[ListeningPortsCollector] {Probe} probe failed", probeName);
        }

        return [];
    }

    private async Task<Dictionary<(int Port, string Protocol), string>> CollectDockerPortMapAsync(
        CancellationToken ct)
    {
        try
        {
            var result = await Shell
                .RunExecAsync("docker", ["ps", "--format", "{{.Names}}|{{.Ports}}"], ct, ProbeTimeout)
                .ConfigureAwait(false);
            if (result.ExitCode == 0)
                return ListeningPortsParser.ParseDockerPortMap(result.StdOut);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No Docker on the host is the normal case on many servers, not an error worth a warning.
            Logger.LogDebug(ex, "[ListeningPortsCollector] docker ps probe failed");
        }

        return [];
    }

    private static string Bound(string value, int max = PortRegistryLimits.MaxOwnerLabelLength)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return UnknownHolder;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
