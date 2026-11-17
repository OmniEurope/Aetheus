// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

public interface IPipelineFleetBulkUpdateService
{
    /// <summary>R-8, R-45: moves every pipeline of the scope pinned on an older version of a shipped template (generic or
    /// versioned) to its latest version, one by one through the manual fleet update. <paramref name="canUpdate"/> is the authorization of
    /// that manual update, asked for each pipeline.</summary>
    Task<PipelineFleetBulkUpdateResultDto> UpdateGenericTemplatesAsync(
        int? projectId,
        IReadOnlyCollection<int>? organizationIds,
        IReadOnlyCollection<int>? accessiblePipelineIds,
        Func<int, CancellationToken, Task<bool>> canUpdate,
        CancellationToken ct = default);
}

/// <summary>
/// Each move is the update a user runs by hand (<see cref="IPipelineFleetService.PreviewUpdateAsync"/> then
/// <see cref="IPipelineFleetService.UpdateAsync"/>): only the <c>extends:</c> pin is rewritten, the definition is
/// committed to the pipeline's own repository and the move is audited <c>TemplateVersionAdopted</c>. What that path
/// refuses (a repository file changed outside Aetheus, an invalid definition, a missing repository) leaves the pipeline
/// as it is and is reported for it; so are overrides the new version orphans, which only a person may acknowledge on
/// the single update page. Running it again moves nothing already moved: a moved pipeline is on the latest version.
/// </summary>
internal sealed class PipelineFleetBulkUpdateService(
    IGenericTemplatePinRepository repository,
    IPipelineFleetService fleet,
    ILogger<PipelineFleetBulkUpdateService> logger) : IPipelineFleetBulkUpdateService
{
    public async Task<PipelineFleetBulkUpdateResultDto> UpdateGenericTemplatesAsync(
        int? projectId,
        IReadOnlyCollection<int>? organizationIds,
        IReadOnlyCollection<int>? accessiblePipelineIds,
        Func<int, CancellationToken, Task<bool>> canUpdate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(canUpdate);
        var snapshot = await repository.LoadAsync(
            GenericTemplatePinAdvancePlan.ShippedTemplateNames,
            new GenericTemplatePinScope(projectId, organizationIds, accessiblePipelineIds), ct).ConfigureAwait(false);
        var moves = snapshot is null ? [] : GenericTemplatePinAdvancePlan.Select(snapshot);
        var items = new List<PipelineFleetBulkUpdateItemDto>(moves.Count);
        foreach (var move in moves)
        {
            var (outcome, reason) = await canUpdate(move.PipelineId, ct).ConfigureAwait(false)
                ? await AdvanceAsync(move, ct).ConfigureAwait(false)
                : (PipelineFleetBulkUpdateOutcome.Forbidden, null);
            items.Add(new PipelineFleetBulkUpdateItemDto
            {
                PipelineId = move.PipelineId,
                PipelineName = move.PipelineName,
                TemplateName = move.TemplateName,
                FromVersion = move.FromVersion,
                ToVersion = move.ToVersion,
                Outcome = outcome,
                Reason = reason
            });
        }
        return new PipelineFleetBulkUpdateResultDto { Items = items };
    }

    private async Task<(PipelineFleetBulkUpdateOutcome Outcome, string? Reason)> AdvanceAsync(
        GenericTemplatePinMove move, CancellationToken ct)
    {
        try
        {
            var preview = await fleet.PreviewUpdateAsync(move.PipelineId, move.ToVersion, ct).ConfigureAwait(false);
            if (!string.Equals(preview.TemplateName, move.TemplateName, StringComparison.Ordinal)
                || preview.CurrentVersion != move.FromVersion)
                return (PipelineFleetBulkUpdateOutcome.Changed, null);
            if (preview.RequiresOrphanAcknowledgement)
                return Refused(move, "the new version no longer has what the pipeline overrides: "
                    + string.Join(", ", preview.OrphanOverrides));
            await fleet.UpdateAsync(move.PipelineId, new PipelineFleetUpdateRequest
            {
                TargetVersion = move.ToVersion,
                AcknowledgeOrphanOverrides = false,
                ExpectedSourceYamlHash = preview.SourceYamlHash
            }, ct).ConfigureAwait(false);
            logger.LogInformation("Pipeline {Pipeline} ({PipelineId}) moved from {Template}@{From} to @{To} (R-8)",
                move.PipelineName, move.PipelineId, move.TemplateName, move.FromVersion, move.ToVersion);
            return (PipelineFleetBulkUpdateOutcome.Updated, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Mandatory: the update path refuses with exceptions (409, 400, 404, an unreadable definition); the
            // pipelines before this one are already moved, so one refusal must not stop the others nor turn the
            // whole batch into an error: the caller reads it in the result, for this pipeline.
            return Refused(move, exception.Message);
        }
    }

    private (PipelineFleetBulkUpdateOutcome, string?) Refused(GenericTemplatePinMove move, string reason)
    {
        logger.LogWarning(
            "Pipeline {Pipeline} ({PipelineId}) stays on {Template}@{From}: the move to @{To} was refused (R-8): {Reason}",
            move.PipelineName, move.PipelineId, move.TemplateName, move.FromVersion, move.ToVersion, reason);
        return (PipelineFleetBulkUpdateOutcome.Refused, reason);
    }
}
