// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.AppMonitoring;

/// <summary>R-27: why a server measure (response times, database size) never arrived from a supervised application,
/// read from what the server already knows about it: no SSH, no guess about the application's own configuration.
/// The first cause that applies in the order of this enum is the one reported.</summary>
public enum AppNoMeasureCause
{
    /// <summary>No ingestion key was ever created (or it was revoked): the application has nothing to send with.</summary>
    NoIngestKey = 0,

    /// <summary>The application is switched off in Aetheus, so the server refuses everything it sends.</summary>
    AppDisabled = 1,

    /// <summary>The current ingestion key expired: the server refuses it.</summary>
    KeyExpired = 2,

    /// <summary>The server refused a key of this application (see the key refusal log): the sender still holds a rotated or
    /// expired key.</summary>
    KeyRefused = 3,

    /// <summary>A valid key exists but nothing at all ever arrived: the Aetheus.Telemetry package is absent, telemetry is off
    /// in the application, or the key was never set there.</summary>
    NothingReceived = 4,

    /// <summary>Other measures arrive but not this one: the measure is turned off in the application, or the package is too old
    /// to send it.</summary>
    MeasureMissing = 5
}

/// <summary>The reason a measure view is empty, with the dates that situate it.</summary>
public sealed record AppNoMeasureDto
{
    public AppNoMeasureCause Cause { get; init; }

    /// <summary>The date the cause is about: when the key expired (<see cref="AppNoMeasureCause.KeyExpired"/>) or when the
    /// server last refused a key of the application (<see cref="AppNoMeasureCause.KeyRefused"/>). Null otherwise.</summary>
    public DateTime? CauseAt { get; init; }

    /// <summary>When the server last received anything from the application (UTC); null when it never did.</summary>
    public DateTime? LastReceptionAt { get; init; }
}
