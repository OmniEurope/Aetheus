// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>
/// PLAN-007: the QA of an automatic dependency update. The project's QA pipeline restores the artifacts of the CI run of the same commit
/// (its default <c>same-commit</c> selector), so it can only start once the CI run of the update has succeeded: launched earlier, it
/// would find nothing to test. Any other end of that CI run (failed, cancelled, partial, rejected) starts no QA and the reason is
/// written on the update. The QA is launched once per update, with the variables of the CI launch, and never anything else follows: no
/// candidate, no release, no deployment.
/// </summary>
internal sealed class DependencyUpdateQaFollowUp(
    DependencyUpdateRepository repository,
    IDependencyUpdatePipelines pipelines,
    TimeProvider timeProvider,
    ILogger<DependencyUpdateQaFollowUp> logger) : IDomainEventHandler<PipelineRunCompletedEvent>
{
    private const int MaxReasonLength = 2048;

    public async Task HandleAsync(PipelineRunCompletedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (await repository.FindOfCiRunAsync(domainEvent.PipelineRunId, ct).ConfigureAwait(false) is not { } update) return;
        if (update.QaPipelineId is not { } qaPipelineId || update.QaPipelineRunId is not null) return;

        if (domainEvent.Status != PipelineStatus.Success)
        {
            Note(update, $"QA not started: the CI run {domainEvent.PipelineRunId} ended as {domainEvent.Status}.");
            await repository.SaveAsync(ct).ConfigureAwait(false);
            return;
        }

        var launch = await pipelines.LaunchAsync(qaPipelineId, update.Branch!, update.CommitSha!, update.Id, ct).ConfigureAwait(false);
        update.QaPipelineRunId = launch.RunId;
        if (launch.Refusal is { } refusal) Note(update, $"QA not started: {refusal}");
        else update.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await repository.SaveAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Dependency update {UpdateId}: QA run {RunId} after CI run {CiRunId}", update.Id, launch.RunId, domainEvent.PipelineRunId);
    }

    private void Note(DependencyUpdate update, string note)
    {
        var text = string.IsNullOrWhiteSpace(update.Reason) ? note : $"{update.Reason} {note}";
        update.Reason = text.Length <= MaxReasonLength ? text : text[..MaxReasonLength];
        update.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
    }
}
