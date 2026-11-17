// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>R-11: the pipeline files of a project's external repository mirrors, imported read-only.</summary>
public sealed class ExternalPipelinesApi(HttpClient http) : ApiClientBase(http)
{
    public async Task<ExternalPipelineCandidatesDto> GetCandidatesAsync(int projectId, CancellationToken ct = default) =>
        await GetJsonAsync<ExternalPipelineCandidatesDto>($"api/projects/{projectId}/external-pipelines", ct).ConfigureAwait(false)
        ?? new ExternalPipelineCandidatesDto();

    /// <summary>Imports one file at the commit that was listed; the refusal reason is kept for the page.</summary>
    public Task<ApiOutcome<PipelineDto, ApiError>> ImportAsync(
        int projectId, ImportExternalPipelineRequest request, CancellationToken ct = default) =>
        PostForApiErrorOutcomeAsync<ImportExternalPipelineRequest, PipelineDto>(
            $"api/projects/{projectId}/external-pipelines", request, ct);
}
