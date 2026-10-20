// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>Persists physical artifact-volume measurements across deployments and leader changes
/// for the 24-hour growth alert. Retain the last 24 hours and the newest measurement before that
/// window.</summary>
public class ArtifactStorageMeasurement
{
    public long Id { get; set; }
    public DateTime MeasuredAtUtc { get; set; }
    public long Bytes { get; set; }
}
