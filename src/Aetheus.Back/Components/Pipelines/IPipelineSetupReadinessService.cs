// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Answers "will the pipelines this wizard is about to create be able to run at all?" BEFORE they
/// are created, rather than after the first launch has spent its way to a missing adapter script.
///
/// Everything it reports is derived from the selected templates themselves: the adapter scripts they
/// invoke, the environments they name, whether any of them deploys. Nothing is hard-coded about what
/// a pipeline "should" contain, so a template that changes changes the check with it.
///
/// It never refuses the creation. Writing the adapters after wiring the pipelines is a legitimate
/// order of work; the caller decides what to do with a blocking finding.
/// </summary>
public interface IPipelineSetupReadinessService
{
    Task<PipelineSetupReadinessDto> CheckAsync(
        int projectId,
        IReadOnlyList<string> templateNames,
        int? organizationId,
        CancellationToken ct = default);

    /// <summary>Resolves a pipeline's project directly or through its environment or server; null
    /// when no pipeline or project exists.</summary>
    Task<int?> FindPipelineProjectIdAsync(int pipelineId, CancellationToken ct = default);

    Task<PipelineSetupReadinessDto> CheckProjectAsync(
        int projectId,
        int? pipelineId,
        int? organizationId,
        CancellationToken ct = default);
}
