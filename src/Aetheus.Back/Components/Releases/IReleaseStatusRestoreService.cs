// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Releases;

/// <summary>Recette R-13: puts a release back to a status its deployment history can end in.</summary>
public interface IReleaseStatusRestoreService
{
    /// <summary>Sets the release to <paramref name="request"/>'s status. Restoring Deployed makes the
    /// project's other deployed release Superseded, in the same save, so a project keeps one deployed
    /// release. Every change is audited with the reason and broadcast like any other status change.</summary>
    Task<ReleaseDto> RestoreAsync(int releaseId, RestoreReleaseStatusRequest request, CancellationToken ct = default);
}
