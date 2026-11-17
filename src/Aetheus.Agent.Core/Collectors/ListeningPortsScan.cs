// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Collectors;

/// <summary>A protocol whose probe really succeeded, and the instant that probe returned.</summary>
public sealed record ProtocolMeasurement(string Protocol, DateTimeOffset MeasuredAt);

/// <summary>
/// What a listening-ports scan saw, with the protocols it really measured. An empty
/// <see cref="Ports"/> is a reading only for the protocols listed in <see cref="Measured"/>: a probe that
/// failed measured nothing, and its absence from the list is what keeps the backend from reading "no
/// listener" into a failure. The measurement time is carried with the scan, so a cached scan reported
/// later is still dated by its own probe.
/// </summary>
public sealed record ListeningPortsScan(
    IReadOnlyList<ObservedPortDto> Ports,
    IReadOnlyList<ProtocolMeasurement> Measured)
{
    /// <summary>No protocol was measured: nothing is known, and nothing may be inferred from the empty listing.</summary>
    public static ListeningPortsScan None { get; } = new([], []);

    /// <summary>Whether at least one protocol was really measured.</summary>
    public bool HasMeasurement => Measured.Count > 0;

    /// <summary>The measured protocols as the backend reads them: each with the age of its probe on this
    /// agent's own clock, so neither this host's wall clock nor the time the report travelled matters.</summary>
    public List<ObservedProtocolScanDto> ProtocolReport(DateTimeOffset now) =>
    [
        .. Measured.Select(measurement => new ObservedProtocolScanDto
        {
            Protocol = measurement.Protocol,
            AgeSeconds = Math.Clamp((now - measurement.MeasuredAt).TotalSeconds, 0, PortRegistryLimits.MaxObservedScanAgeSeconds)
        })
    ];
}
