// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>« Récupérer la place », an administrator action, never run on its own.</summary>
public interface IReclaimSpaceService
{
    Task<ReclaimSpaceStatusDto> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Starts the <c>VACUUM FULL</c> of one purged table and returns once it is under way: 400 for a
    /// table that is not purged by the data retention, 409 while a pipeline runs or another table is
    /// being processed.</summary>
    Task<ReclaimSpaceResultDto> StartAsync(string table, string requestedBy, CancellationToken ct = default);
}
