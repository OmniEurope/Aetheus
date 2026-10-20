// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Creates missing libraries and vaults declared by selected templates, copying key names
/// only. Values remain empty for the operator.</summary>
public interface IPipelineRequirementsProvisioner
{
    /// <param name="readableLibraryIds">Libraries the caller may read; null means all (admin). The key
    /// names are only ever copied from one of these.</param>
    /// <param name="readableVaultIds">Vaults the caller may read; null means all.</param>
    Task<PipelineRequirementsProvisionResultDto> ProvisionAsync(
        int projectId,
        IReadOnlyList<string> templateNames,
        int? organizationId,
        List<int>? readableLibraryIds,
        List<int>? readableVaultIds,
        CancellationToken ct = default);

    /// <summary>The libraries and vaults the project's own pipelines require and the project lacks,
    /// each with the pipelines waiting for it.</summary>
    Task<List<UnmetRequirementDto>> GetUnmetAsync(int projectId, int? organizationId, CancellationToken ct = default);

    /// <summary>Creates what <see cref="GetUnmetAsync"/> reports, keys only, values empty. The
    /// overview's one-click fix for pipelines already created.</summary>
    Task<PipelineRequirementsProvisionResultDto> ProvisionUnmetAsync(
        int projectId,
        int? organizationId,
        List<int>? readableLibraryIds,
        List<int>? readableVaultIds,
        CancellationToken ct = default);
}
