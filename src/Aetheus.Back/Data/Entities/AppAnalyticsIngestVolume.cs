// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>Records the size and time of an accepted audience batch in the shared database. Summing
/// the application rows over the last hour accounts for traffic across both blue-green
/// colours.</summary>
public class AppAnalyticsIngestVolume
{
    public long Id { get; set; }
    public int MonitoredAppId { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public int Count { get; set; }
    public MonitoredApp MonitoredApp { get; set; } = null!;
}
