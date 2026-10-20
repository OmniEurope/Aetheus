// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Releases;

/// <summary>
/// Recette R1-72, decision D2: the releases older than the retention window that nothing still needs
/// are archived. The rule, its protections and what archiving deletes are in
/// <see cref="ReleaseRetentionRepository"/>.
/// </summary>
public interface IReleaseRetentionRepository
{
    /// <summary>Up to <paramref name="batchSize"/> identifiers of releases not yet archived, dated on or
    /// before <paramref name="cutoff"/> and not protected at <paramref name="now"/>, lowest first.</summary>
    Task<List<int>> GetExpiredReleaseIdsAsync(DateTime cutoff, DateTime now, int batchSize, CancellationToken ct = default);

    /// <summary>Archives those of <paramref name="releaseIds"/> that are still expired and unprotected,
    /// checked again by the update statement itself, and deletes their artifacts and runs that nothing
    /// live needs; returns how many releases were archived.</summary>
    Task<int> ArchiveExpiredAsync(IReadOnlyCollection<int> releaseIds, DateTime cutoff, DateTime now, CancellationToken ct = default);
}
