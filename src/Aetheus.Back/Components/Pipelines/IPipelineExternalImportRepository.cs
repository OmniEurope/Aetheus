// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>A project's pipeline as the external import compares it with a mirror file.</summary>
internal sealed record PipelineExternalImportRow(
    int Id, string Name, int? SourceRepositoryId, string? ExternalImportPath, string? ExternalImportSha256,
    string? ExternalImportCommit);

/// <summary>R-11: the pipeline rows the external import reads and writes.</summary>
internal interface IPipelineExternalImportRepository
{
    Task<List<PipelineExternalImportRow>> GetProjectPipelinesAsync(int projectId, CancellationToken ct = default);

    /// <summary>The project's pipeline of that name, tracked, or null.</summary>
    Task<Pipeline?> FindProjectPipelineAsync(int projectId, string name, CancellationToken ct = default);

    /// <summary>Adds and saves a new pipeline. False when the project's unique pipeline name was taken first.</summary>
    Task<bool> TryAddAsync(Pipeline pipeline, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
