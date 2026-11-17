// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.SystemLogs;

public interface IDatabaseTableVolumeService
{
    Task<List<DatabaseTableVolumeDto>> ListAsync(CancellationToken ct = default);
}

/// <summary>Turns the catalog rows into the view of the tables, the closest to their key limit first.</summary>
public sealed class DatabaseTableVolumeService(IDatabaseTableVolumeRepository repository) : IDatabaseTableVolumeService
{
    public async Task<List<DatabaseTableVolumeDto>> ListAsync(CancellationToken ct = default) =>
        [.. (await repository.ListAsync(ct).ConfigureAwait(false))
            .Select(Map)
            .OrderByDescending(table => table.KeyLimitPercent ?? -1)
            .ThenByDescending(table => table.EstimatedRows ?? -1)
            .ThenBy(table => table.Name, StringComparer.Ordinal)];

    internal static long? LimitOf(string? keyType) => keyType switch
    {
        "smallint" => short.MaxValue,
        "integer" => int.MaxValue,
        "bigint" => long.MaxValue,
        _ => null
    };

    internal static DatabaseTableVolumeDto Map(DatabaseTableVolumeRow row)
    {
        var limit = LimitOf(row.KeyType);
        return new DatabaseTableVolumeDto
        {
            Name = row.TableName,
            EstimatedRows = row.EstimatedRows is { } rows && double.IsFinite(rows) ? (long)Math.Round(Math.Max(0, rows)) : null,
            TotalBytes = Math.Max(0, row.TotalBytes),
            KeyType = row.KeyType,
            MaxKey = row.LastKey,
            KeyLimit = limit,
            KeyLimitPercent = limit is { } max && row.LastKey is { } last ? (double)last / max * 100 : null
        };
    }
}
