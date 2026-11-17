// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.SystemLogs;

/// <summary>Reads the volume of the application tables from the PostgreSQL catalog.</summary>
public interface IDatabaseTableVolumeRepository
{
    Task<IReadOnlyList<DatabaseTableVolumeRow>> ListAsync(CancellationToken ct = default);
}

/// <summary>One raw catalog row; the column aliases of <see cref="DatabaseTableVolumeRepository.Statement"/> name the properties.</summary>
public sealed class DatabaseTableVolumeRow
{
    public string TableName { get; set; } = string.Empty;
    public double? EstimatedRows { get; set; }
    public long TotalBytes { get; set; }
    public string? KeyType { get; set; }
    public long? LastKey { get; set; }
}
