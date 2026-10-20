// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Artifacts;

/// <summary>Persists artifact-volume growth measurements.</summary>
public interface IArtifactStorageMeasurementRepository
{
    /// <summary>The kept measurements, oldest first.</summary>
    Task<List<(DateTime At, long Bytes)>> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves the measurement, then removes what a later growth no longer needs: every measurement older
    /// than the newest one taken at or before <paramref name="windowStartUtc"/>.
    /// </summary>
    Task RecordAsync(DateTime measuredAtUtc, long bytes, DateTime windowStartUtc, CancellationToken ct = default);
}
