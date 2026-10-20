// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>Indexes metric names on first ingestion to avoid scanning all retained samples when
/// listing names. The retention sweep removes a name once no sample carries it.</summary>
public class AppMetricName
{
    public int Id { get; set; }
    public int MonitoredAppId { get; set; }
    public string Name { get; set; } = string.Empty;

    // Navigation
    public MonitoredApp MonitoredApp { get; set; } = null!;
}
