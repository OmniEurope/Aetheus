// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Projects;

/// <summary>
/// PLAN-013 lot 5: what a project occupies in the Aetheus database, estimated from its rows in each detail
/// table and the bytes per row the database size measurement (lot 1) gives for that table.
/// </summary>
public sealed record ProjectStorageUsageDto
{
    public int ProjectId { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public long BudgetBytes { get; init; }
    public long UsedBytes { get; init; }
    public int UsedPercent { get; init; }
    public List<ProjectStorageTableDto> Tables { get; init; } = [];
}

/// <summary>One table's share of a project's usage.</summary>
public sealed record ProjectStorageTableDto(string Table, long Rows, long Bytes);

/// <summary>PLAN-013 lot 5: the usage of every project, and whether the bytes per row were measured.</summary>
public sealed record ProjectStorageReportDto
{
    /// <summary>When the database size measurement used was taken; null when the fixed estimates were used.</summary>
    public DateTime? MeasuredAt { get; init; }
    public List<ProjectStorageUsageDto> Projects { get; init; } = [];
}
