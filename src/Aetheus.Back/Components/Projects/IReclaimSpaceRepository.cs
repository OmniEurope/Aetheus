// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary> The size on disk of a purged table and its <c>VACUUM FULL</c>. Only the tables of <see
/// cref="ReclaimSpaceRepository.Statements"/> are accepted; anything else throws. </summary>
public interface IReclaimSpaceRepository
{
    /// <summary>The table's <c>pg_total_relation_size</c> (heap, indexes, TOAST); null off PostgreSQL.</summary>
    Task<long?> GetTableBytesAsync(string table, CancellationToken ct = default);

    /// <summary>Rewrites the table to give its free space back to the system. Holds an exclusive lock on
    /// the table for its whole duration.</summary>
    Task VacuumFullAsync(string table, CancellationToken ct = default);

    /// <summary>Pipeline runs that have not ended.</summary>
    Task<int> CountActiveRunsAsync(CancellationToken ct = default);
}
