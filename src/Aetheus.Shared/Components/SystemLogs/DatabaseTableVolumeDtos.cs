// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.SystemLogs;

/// <summary>One table of the application schema: estimated rows, size on disk and, for an integer
/// primary key fed by a sequence, how far the key went toward the limit of its type.</summary>
public sealed record DatabaseTableVolumeDto
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Planner estimate (<c>pg_class.reltuples</c>, else <c>n_live_tup</c>); null when PostgreSQL has neither.</summary>
    public long? EstimatedRows { get; init; }

    /// <summary>Heap, indexes and TOAST (<c>pg_total_relation_size</c>).</summary>
    public long TotalBytes { get; init; }

    /// <summary>smallint, integer or bigint when the primary key is a single integer column.</summary>
    public string? KeyType { get; init; }

    /// <summary>The highest key handed out by the key's sequence (null while none was, or no sequence feeds the key).</summary>
    public long? MaxKey { get; init; }

    /// <summary>The largest value the key type holds.</summary>
    public long? KeyLimit { get; init; }

    /// <summary><see cref="MaxKey"/> over <see cref="KeyLimit"/>, in percent.</summary>
    public double? KeyLimitPercent { get; init; }
}
