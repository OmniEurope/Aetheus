// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>One pipeline to move from <see cref="FromVersion"/> to <see cref="ToVersion"/> of its template.</summary>
internal sealed record GenericTemplatePinMove(
    int PipelineId, string PipelineName, string TemplateName, int FromVersion, int ToVersion);

/// <summary>
/// R-8 (decision of 2026-11-11), extended by R-45 (2026-11-12): which pipelines "Update all pipelines" moves. A shipped
/// template gains a version each time its file changes (generic templates, synchronized from one file) or a numbered
/// file is added (versioned templates such as <c>application-candidate</c> or <c>host-bluegreen-deploy</c>), while a
/// pipeline stays pinned on the <c>@N</c> it was created with, so a template fix only reaches it through an update.
/// Moved: a pipeline whose stored definition extends a shipped template of the platform organization with an explicit
/// older <c>@N</c>, to that template's latest version. Never moved: a pipeline imported read-only from an external
/// repository, a pipeline of an organization owning a template of the same name (the resolver reads that one, R-78),
/// and any template outside the shipped catalog (legacy, organization templates). Overrides the new version orphans
/// refuse the move afterwards, in the bulk service.
/// </summary>
internal static class GenericTemplatePinAdvancePlan
{
    /// <summary>Every template the platform ships, generic or versioned.</summary>
    internal static readonly IReadOnlyList<string> ShippedTemplateNames = DeliveryTemplateCatalog.Definitions.Keys.ToArray();

    private static readonly HashSet<string> GenericNames = new(ShippedTemplateNames, StringComparer.Ordinal);

    internal static IReadOnlyList<GenericTemplatePinMove> Select(GenericTemplatePinSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Candidates
            .Where(candidate => !candidate.ImportedFromExternalRepository
                && candidate.OrganizationId != 0
                && GenericNames.Contains(candidate.TemplateName)
                && snapshot.CatalogLatestVersions.TryGetValue(candidate.TemplateName, out var latest)
                && candidate.PinnedVersion < latest
                && !snapshot.OrganizationOwnedTemplates.Contains((candidate.OrganizationId, candidate.TemplateName)))
            .Select(candidate => new GenericTemplatePinMove(
                candidate.PipelineId,
                candidate.PipelineName,
                candidate.TemplateName,
                candidate.PinnedVersion,
                snapshot.CatalogLatestVersions[candidate.TemplateName]))
            .ToList();
    }
}
