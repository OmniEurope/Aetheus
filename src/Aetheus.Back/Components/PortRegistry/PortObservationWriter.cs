// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.PortRegistry;

/// <summary>
/// Writes what a scan SAW, as opposed to what somebody CLAIMED. The two are deliberately separate
/// responsibilities: a claim is a decision the registry defends, an observation is evidence it merely
/// records, and no discrepancy between them is ever turned into an automatic correction.
///
/// Everything here is keyed by (port, protocol). The two stacks bind independently, so a host
/// listening on 53/tcp and 53/udp occupies two different ports and collapsing them would hide one
/// behind the other.
/// </summary>
internal sealed class PortObservationWriter(
    IPortRegistryRepository repo,
    TimeProvider timeProvider,
    ILogger logger)
{
    /// <summary>Every observed row shares one owner key: an observation names no project, and pretending
    /// otherwise would let a sighting win a conflict against a real declaration.</summary>
    internal const string ObservedOwnerKey = "observed";

    /// <summary>How old the stamp of an unchanged observed row may get before a scan rewrites it. An observed row is
    /// deleted when its port stops listening, so only the claims' stamps (always rewritten when confirmed) are
    /// compared with the scan time; the stamp of an observed row is "last time this row was written".</summary>
    internal static readonly TimeSpan ObservedStampRefresh = TimeSpan.FromMinutes(5);

    /// <summary>Shown when neither a container name nor a process name could be read on the host.</summary>
    internal const string ObservedFallbackLabel = "host process";

    /// <summary>
    /// Anything that is not recognisably UDP is read as TCP: that is what every Aetheus deployment
    /// binds, so an unreadable protocol must fall on the side that is actually defended rather than
    /// create a phantom UDP row nothing would ever check against.
    /// </summary>
    internal static string NormalizeProtocol(string? protocol) =>
        string.Equals(protocol?.Trim(), PortRegistryLimits.UdpProtocol, StringComparison.OrdinalIgnoreCase)
            ? PortRegistryLimits.UdpProtocol
            : PortRegistryLimits.TcpProtocol;

    /// <summary>
    /// Replaces the <c>Observed</c> rows of every protocol the scan really measured. A shorter list
    /// really means "these ports stopped listening", never "the report was truncated", which is why the
    /// caller must send the complete scan of each protocol it names. A protocol whose probe failed is not
    /// named: its rows are kept, because nothing was learned about it. <paramref name="protocols"/> null is
    /// a legacy report in which every protocol was measured at <paramref name="observedAt"/>.
    /// </summary>
    public async Task ReplaceAsync(
        int serverId, IReadOnlyCollection<ObservedPortDto> ports, DateTime observedAt,
        IReadOnlyCollection<ObservedProtocolScanDto>? protocols, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ports);
        var measured = MeasuredProtocols(protocols, observedAt);
        // A report that measured nothing says nothing: no row is dropped and no stamp moves.
        if (measured.Count == 0) return;

        // Only a TCP measurement moves the server's last-scan stamp: declared and manual claims are TCP
        // only, and that stamp is what their own is read against ("listening now"). A UDP-only report
        // learned nothing about them.
        DateTime? tcpAt = measured.TryGetValue(PortRegistryLimits.TcpProtocol, out var tcpMeasuredAt) ? tcpMeasuredAt : null;
        var serverKnown = tcpAt is { } stamped
            ? await repo.SetPortsObservedAtAsync(serverId, stamped, ct).ConfigureAwait(false)
            : await repo.ServerExistsAsync(serverId, ct).ConfigureAwait(false);
        if (!serverKnown)
        {
            // The server was deleted between the scan and its report. Writing observations for it would
            // resurrect rows the cascade just removed.
            logger.LogDebug("Dropped a port observation for unknown server {ServerId}.", serverId);
            return;
        }

        var seen = SeenPorts(ports, measured);
        var existing = await repo.GetTrackedObservedReservationsAsync(serverId, ct).ConfigureAwait(false);
        foreach (var stale in existing.Where(reservation =>
                     measured.ContainsKey(NormalizeProtocol(reservation.Protocol))
                     && !seen.ContainsKey((reservation.Port, NormalizeProtocol(reservation.Protocol)))))
        {
            repo.RemoveReservation(stale);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var ((port, protocol), observed) in seen)
            WriteSighting(serverId, port, protocol, observed, existing, measured[protocol], now);

        // A declared or manual claim confirmed by this scan gets its observation stamp, which is what
        // the UI reads to say "declared AND listening" instead of "declared, never seen". TCP only:
        // everything Aetheus declares is TCP, so a UDP sighting confirms nothing about a claim.
        if (tcpAt is { } tcpStamp)
            await StampClaimsAsync(
                serverId,
                [.. seen.Keys
                    .Where(key => key.Protocol == PortRegistryLimits.TcpProtocol)
                    .Select(key => key.Port)],
                tcpStamp,
                ct).ConfigureAwait(false);

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The listings of the protocols the report measured, one per (port, protocol). A listing of a
    /// protocol the report did not measure is ignored: it is not evidence.</summary>
    private static Dictionary<(int Port, string Protocol), ObservedPortDto> SeenPorts(
        IReadOnlyCollection<ObservedPortDto> ports, Dictionary<string, DateTime> measured)
    {
        var seen = new Dictionary<(int Port, string Protocol), ObservedPortDto>();
        foreach (var observed in ports)
        {
            if (observed is null || observed.Port is <= 0 or > 65535) continue;
            if (seen.Count >= PortRegistryLimits.MaxObservedPorts) break;
            var protocol = NormalizeProtocol(observed.Protocol);
            if (!measured.ContainsKey(protocol)) continue;
            // The same port listening on several interfaces is one occupied port.
            seen.TryAdd((observed.Port, protocol), observed);
        }

        return seen;
    }

    /// <summary>Records one sighting: a new row, or the refresh of the row already holding that port.</summary>
    private void WriteSighting(
        int serverId, int port, string protocol, ObservedPortDto observed,
        List<ServerPortReservation> existing, DateTime measuredAt, DateTime now)
    {
        var label = Truncate(observed.Holder, PortRegistryLimits.MaxOwnerLabelLength);
        if (label.Length == 0) label = ObservedFallbackLabel;

        var row = existing.Find(reservation =>
            reservation.Port == port && NormalizeProtocol(reservation.Protocol) == protocol);
        if (row is null)
        {
            repo.TrackReservation(new ServerPortReservation
            {
                ServerId = serverId,
                Port = port,
                Protocol = protocol,
                OwnerKey = ObservedOwnerKey,
                OwnerLabel = label,
                Source = PortReservationSource.Observed,
                DeclaredAt = now,
                UpdatedAt = now,
                ObservedAt = measuredAt
            });
            return;
        }

        // A sighting that says nothing new writes nothing (R-2): the row's existence already means "listening",
        // so its stamp is refreshed once per ObservedStampRefresh, not on every scan of every heartbeat.
        if (row.OwnerLabel == label && row.ObservedAt is { } stamp && measuredAt - stamp < ObservedStampRefresh)
            return;

        row.OwnerLabel = label;
        row.UpdatedAt = now;
        row.ObservedAt = measuredAt;
    }

    /// <summary>The protocols a report measured, each with the instant of its own measurement: the receipt
    /// time minus the age the agent reported on its own clock, so a cached scan keeps its real date and the
    /// agent's wall clock is never trusted. A name the registry does not store, or an age that is not a
    /// finite number, measures nothing. Null is a legacy report: every protocol, measured at receipt.</summary>
    private static Dictionary<string, DateTime> MeasuredProtocols(
        IReadOnlyCollection<ObservedProtocolScanDto>? protocols, DateTime receivedAt)
    {
        if (protocols is null)
            return new Dictionary<string, DateTime>(StringComparer.Ordinal)
            {
                [PortRegistryLimits.TcpProtocol] = receivedAt,
                [PortRegistryLimits.UdpProtocol] = receivedAt
            };

        var measured = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var scan in protocols)
        {
            var name = scan?.Protocol?.Trim().ToLowerInvariant();
            if (name is not (PortRegistryLimits.TcpProtocol or PortRegistryLimits.UdpProtocol)
                || !double.IsFinite(scan!.AgeSeconds))
                continue;
            var age = Math.Clamp(scan.AgeSeconds, 0, PortRegistryLimits.MaxObservedScanAgeSeconds);
            measured.TryAdd(name, receivedAt - TimeSpan.FromSeconds(age));
        }

        return measured;
    }

    /// <summary>
    /// Marks the declared and manual claims this scan confirmed. A claim the scan did NOT see keeps its
    /// previous stamp rather than being cleared: for a claim the field means "last seen listening", and
    /// readers (<c>PortRegistryService.ObservationFor</c>, the ports section) read "listening now" as a
    /// stamp not older than <c>Server.PortsObservedAt</c>, the server's last scan time written on every
    /// scan, so stale evidence is never lost and never mistaken for a live listener either. A stamp is
    /// therefore never a scan time on its own: a claim whose port stopped keeps its old stamp, and an
    /// unchanged observed row is not rewritten (see <see cref="ObservedStampRefresh"/>).
    /// </summary>
    private async Task StampClaimsAsync(
        int serverId, IReadOnlyCollection<int> observedPorts, DateTime observedAt, CancellationToken ct)
    {
        if (observedPorts.Count == 0) return;

        foreach (var source in new[] { PortReservationSource.Declared, PortReservationSource.Manual })
        {
            var tracked = await repo
                .GetTrackedReservationsForPortsAsync(serverId, observedPorts, source, ct)
                .ConfigureAwait(false);
            foreach (var row in tracked)
                row.ObservedAt = observedAt;
        }
    }

    private static string Truncate(string value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
