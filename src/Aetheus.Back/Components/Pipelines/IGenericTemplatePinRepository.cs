// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>A pipeline whose stored definition extends a template with an explicit <c>@N</c> (R-8).</summary>
internal sealed record GenericTemplatePinCandidate(
    int PipelineId,
    string PipelineName,
    string TemplateName,
    int PinnedVersion,
    int OrganizationId,
    bool ImportedFromExternalRepository);

/// <summary>What the R-8 bulk update reads: the shipped catalog of the platform organization, the pipelines of the
/// scope pinned on one of its templates, and the organizations owning a template of the same name.</summary>
internal sealed record GenericTemplatePinSnapshot(
    IReadOnlyDictionary<string, int> CatalogLatestVersions,
    IReadOnlyList<GenericTemplatePinCandidate> Candidates,
    IReadOnlySet<(int OrganizationId, string TemplateName)> OrganizationOwnedTemplates);

/// <summary>The pipelines a bulk update considers: those the fleet lists for the same caller and project.</summary>
internal sealed record GenericTemplatePinScope(
    int? ProjectId,
    IReadOnlyCollection<int>? OrganizationIds,
    IReadOnlyCollection<int>? AccessiblePipelineIds);

internal interface IGenericTemplatePinRepository
{
    /// <summary>Null when the platform organization does not exist (nothing was seeded).</summary>
    Task<GenericTemplatePinSnapshot?> LoadAsync(
        IReadOnlyCollection<string> templateNames, GenericTemplatePinScope scope, CancellationToken ct);
}
