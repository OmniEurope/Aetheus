// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>R-11 (decision of 2026-11-10, option A): lists the pipeline files of a project's external repository
/// mirrors against the project's pipelines, and imports one read-only.</summary>
public interface IPipelineExternalImportService
{
    Task<ExternalPipelineCandidatesDto> ListAsync(int projectId, CancellationToken ct = default);

    /// <summary>Imports the file at the commit the caller reviewed. Importing the same content again changes nothing;
    /// importing a changed file replaces the stored definition, still read-only and manual.</summary>
    /// <exception cref="BadRequestException">The request or the file is refused by the import policy.</exception>
    /// <exception cref="ConflictException">The mirror moved since the review, or the name belongs to a pipeline defined
    /// in Aetheus or imported from another file.</exception>
    /// <exception cref="NotFoundException">The repository is not an external mirror of this project, or the file is gone.</exception>
    Task<PipelineDto> ImportAsync(int projectId, ImportExternalPipelineRequest request, string actor, CancellationToken ct = default);
}
