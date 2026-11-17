// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Data.Entities;

public class AppAnalyticsPeriodIdentity
{
    public long Id { get; set; }
    public int MonitoredAppId { get; set; }
    public AnalyticsPeriodKind PeriodKind { get; set; }
    public DateOnly PeriodStartUtc { get; set; }
    public string Pseudonym { get; set; } = string.Empty;
    /// <summary>Instant of the first event of this pseudonym in the period; set once on insert.</summary>
    public DateTime FirstSeenAtUtc { get; set; }

    /// <summary>
    /// Normalized route (no query string) of the first page view of a daily identity, set once; null until
    /// a page view arrives and on rows stored before the column existed. Never an IP address or user agent.
    /// </summary>
    public string? EntryRoute { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public int KeyVersion { get; set; }
    public MonitoredApp MonitoredApp { get; set; } = null!;
}
