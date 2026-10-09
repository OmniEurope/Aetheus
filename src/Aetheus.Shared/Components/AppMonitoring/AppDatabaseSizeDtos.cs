// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.AppMonitoring;

/// <summary>
/// PLAN-013 lot 1 (R2-113): the size of a supervised application's database, as its <c>Aetheus.Telemetry</c>
/// package last measured it (the database size measurement turned on): the whole database and its largest
/// tables, with what they measured about 24 hours earlier.
/// </summary>
public sealed record AppDatabaseSizeReportDto
{
    /// <summary>When the application last sent a measurement. Null when it never did.</summary>
    public DateTime? MeasuredAt { get; init; }

    public string? DatabaseName { get; init; }

    /// <summary>The database on disk, in bytes.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>The same figure about 24 hours before <see cref="MeasuredAt"/>; null without a measurement then.</summary>
    public long? TotalBytesDayBefore { get; init; }

    /// <summary>The largest tables, largest first.</summary>
    public List<AppDatabaseTableSizeDto> Tables { get; init; } = [];
}

/// <summary>One table: its size on disk (indexes and TOAST included), its estimated row count, and its size
/// about 24 hours earlier. A figure the measurement did not carry stays null rather than zero.</summary>
public sealed record AppDatabaseTableSizeDto
{
    public string Schema { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public long? TotalBytes { get; init; }
    public long? EstimatedRows { get; init; }
    public long? TotalBytesDayBefore { get; init; }
}
