// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary> The setup wizard's "start from an existing project". Plans and makes adapted copies of
/// a project's pipelines in another project, with the structure of the libraries and vaults they
/// use (names only, never a value). </summary>
public interface IPipelineProjectCopyService
{
    /// <summary>What the copy would do, read only.</summary>
    /// <param name="readablePipelineIds">Pipelines the caller may read; null means all (admin). Only
    /// these are offered from the source project.</param>
    /// <param name="readableLibraryIds">Libraries the caller may read; null means all. Variable names
    /// are only read from these.</param>
    /// <param name="readableVaultIds">Vaults the caller may read; null means all.</param>
    Task<PipelineProjectCopyPlanDto> PlanAsync(
        PipelineProjectCopyRequest request,
        List<int>? readablePipelineIds,
        List<int>? readableLibraryIds,
        List<int>? readableVaultIds,
        CancellationToken ct = default);

    /// <summary>Makes the copies the plan allows, then reads what the target project still lacks to
    /// run them. All or nothing: a failure removes what this call created.</summary>
    Task<PipelineProjectCopyResultDto> CopyAsync(
        PipelineProjectCopyRequest request,
        int? organizationId,
        List<int>? readablePipelineIds,
        List<int>? readableLibraryIds,
        List<int>? readableVaultIds,
        CancellationToken ct = default);
}
