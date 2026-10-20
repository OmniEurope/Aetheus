// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Push handling synchronizes definitions before triggering pipelines so modified
/// definitions apply immediately. Downstream trigger failures are logged without reporting an
/// already-written Git push as failed.</summary>
internal sealed class GitPushPipelineTriggerHandler(
    IPipelineService pipelineService,
    IPipelineRunService pipelineRunService,
    IPipelineAutoTriggerRepository pipelineRepo,
    IGitLightCliService cli,
    ILogger<GitPushPipelineTriggerHandler> logger)
    : IDomainEventHandler<GitPushProcessedEvent>
{
    public async Task HandleAsync(GitPushProcessedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // Definitions first: a push that changes a pipeline's YAML must trigger the changed one.
        foreach (var update in domainEvent.RefUpdates)
            await SyncPipelinesFromRepoAsync(domainEvent, update, ct).ConfigureAwait(false);
        await TriggerPushPipelinesAsync(domainEvent, ct).ConfigureAwait(false);
    }

    private async Task TriggerPushPipelinesAsync(GitPushProcessedEvent push, CancellationToken ct)
    {
        try
        {
            var pipelines = await pipelineService
                .GetWebhookTriggeredPipelinesForProjectAsync(push.ProjectId, ct).ConfigureAwait(false);
            foreach (var pipeline in pipelines)
                await TryTriggerPushPipelineAsync(pipeline, push, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to trigger pipelines on push for {Slug}", push.Slug);
        }
    }

    private async Task TryTriggerPushPipelineAsync(
        PipelineDto pipeline,
        GitPushProcessedEvent push,
        CancellationToken ct)
    {
        var definition = YamlParsingHelper.ParseAndValidate(pipeline.YamlDefinition, logger);
        var matchingUpdate = definition is null
            ? null
            : push.RefUpdates.FirstOrDefault(update =>
                PipelineBranchFilter.Matches(definition.Branches, update.Reference, logger));
        if (matchingUpdate is null) return;
        if (await PushOnlyTouchesIgnoredPathsAsync(definition!, push, matchingUpdate, ct).ConfigureAwait(false))
        {
            logger.LogInformation(
                "Push to {Reference} only touches paths_ignore entries of pipeline {PipelineId} "
                + "({PipelineName}); no run created.",
                matchingUpdate.Reference, pipeline.Id, pipeline.Name);
            return;
        }
        var variables = new Dictionary<string, string>
        {
            ["WEBHOOK_REF"] = matchingUpdate.Reference,
            ["AETHEUS_SOURCE_COMMIT"] = matchingUpdate.NewObjectId
        };
        using var pipelineLock = await PipelineTriggerLocks.AcquireAsync(pipeline.Id, ct).ConfigureAwait(false);
        var hasActiveRun = await pipelineService.HasActiveRunAsync(pipeline.Id, ct).ConfigureAwait(false);
        if (hasActiveRun && definition!.SupersedeRunning != true) return;
        if (!hasActiveRun)
        {
            logger.LogInformation(
                "Git push triggering pipeline {PipelineId} ({PipelineName})", pipeline.Id, pipeline.Name);
            await pipelineRunService.TriggerAutomatedRunAsync(
                pipeline.Id, "GitPush", variables, ct).ConfigureAwait(false);
            return;
        }
        await ReplaceActivePushRunAsync(pipeline, variables, ct).ConfigureAwait(false);
    }

    /// <summary>Skip only when a complete readable diff proves every path irrelevant. New branches
    /// or unreadable diffs trigger execution because absence of evidence cannot justify an
    /// invisible skipped run.</summary>
    private async Task<bool> PushOnlyTouchesIgnoredPathsAsync(
        PipelineYamlDefinition definition,
        GitPushProcessedEvent push,
        GitRefUpdate update,
        CancellationToken ct)
    {
        if (definition.PathsIgnore.Count == 0) return false;
        if (push.DiskPath.Length == 0) return false;
        // A new branch: git announces all-zero as the previous id, so there is no range. Build it.
        if (update.OldObjectId.Length == 0 || update.OldObjectId.All(character => character == '0'))
            return false;

        IReadOnlyList<string>? changed;
        try
        {
            changed = await cli.GetChangedPathsAsync(
                push.DiskPath, update.OldObjectId, update.NewObjectId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the changed paths of {Reference}; the run proceeds.", update.Reference);
            return false;
        }

        return PipelinePathFilter.ShouldSkip(definition.PathsIgnore, changed, logger);
    }

    /// <summary>
    /// Latest-wins: prepare and authorize the replacement BEFORE cancelling anything, so a refused
    /// run does not leave the pipeline with nothing running.
    /// </summary>
    private async Task ReplaceActivePushRunAsync(
        PipelineDto pipeline,
        Dictionary<string, string> variables,
        CancellationToken ct)
    {
        var preparation = await pipelineRunService.PrepareAutomatedRunAsync(
            pipeline.Id, "GitPush", variables, ct).ConfigureAwait(false);
        if (preparation is null) return;
        var activeRunIds = await pipelineRepo.GetActiveRunIdsAsync(pipeline.Id, ct).ConfigureAwait(false);
        foreach (var activeRunId in activeRunIds)
            await pipelineRunService.CancelRunAsync(
                activeRunId, PipelineRunCancellation.BySystem("Superseded by a newer push (latest wins)"), ct).ConfigureAwait(false);
        logger.LogInformation(
            "Git push latest-wins requested cancellation of {Count} superseded run(s) for pipeline {PipelineId}",
            activeRunIds.Count, pipeline.Id);
        var replacement = await pipelineRunService.TriggerPreparedAutomatedRunAsync(
            preparation, "GitPush", variables, ct).ConfigureAwait(false);
        if (replacement is null)
            logger.LogWarning(
                "Git push replacement for pipeline {PipelineId} could not be persisted", pipeline.Id);
    }

    private async Task SyncPipelinesFromRepoAsync(
        GitPushProcessedEvent push, GitRefUpdate update, CancellationToken ct)
    {
        try
        {
            if (push.ProjectId == 0 || push.DiskPath.Length == 0) return;

            // Carried over verbatim, including its flaw: a non-branch ref (a tag) is sliced at the
            // length of "refs/heads/" anyway and yields a nonsense source branch. Left as-is so this
            // commit stays a move; fixing it here would hide a behaviour change inside a refactor.
            var sourceBranch = update.Reference["refs/heads/".Length..];
            var tree = await cli.GetTreeAsync(push.DiskPath, update.NewObjectId, ".pipeline", ct).ConfigureAwait(false);
            var yamlFiles = tree.Where(entry => entry.Name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                                          || entry.Name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)).ToList();
            if (yamlFiles.Count == 0) return;

            foreach (var file in yamlFiles)
            {
                var blob = await cli.GetBlobAsync(
                    push.DiskPath, update.NewObjectId, $".pipeline/{file.Name}", ct).ConfigureAwait(false);
                if (blob is null || string.IsNullOrWhiteSpace(blob.Content)) continue;

                var definition = YamlParsingHelper.ParseAndValidate(blob.Content, logger);
                if (definition is null)
                {
                    logger.LogWarning(
                        "Invalid pipeline YAML in .pipeline/{File} for repo {Repo}", file.Name, push.RepositoryName);
                    continue;
                }

                var pipelineName = string.IsNullOrWhiteSpace(definition.Name)
                    ? Path.GetFileNameWithoutExtension(file.Name)
                    : definition.Name;

                await pipelineService.UpsertPipelineFromYamlAsync(
                    pipelineName, blob.Content, push.ProjectId, definition.Trigger, ct,
                    sourceBranch, push.DefaultBranch, push.GitRepoId).ConfigureAwait(false);
                logger.LogInformation(
                    "Synced pipeline '{Name}' from {Commit}:.pipeline/{File}",
                    pipelineName, update.NewObjectId, file.Name);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sync pipelines from .pipeline/ for repo {Repo}", push.RepositoryName);
        }
    }
}
