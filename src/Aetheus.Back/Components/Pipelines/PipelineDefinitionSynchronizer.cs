// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Git.Events;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Reads the <c>.pipeline/</c> definitions of the commit a branch now points at and upserts
/// them. The one place that decides how a moved branch updates the stored definitions, so a client
/// push and a branch the server moved itself (post-deployment advance, merged pull request) cannot
/// drift apart: the scheduler reads the cron of the stored definition, so a branch that moved
/// without this running leaves its schedules and trigger types stale.</summary>
internal sealed class PipelineDefinitionSynchronizer(
    IPipelineService pipelineService,
    IGitLightCliService cli,
    PipelineRunNotificationPublisher notifications,
    ILogger<PipelineDefinitionSynchronizer> logger)
{
    public async Task SyncAsync(IGitRefUpdatesEvent moved, GitRefUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(moved);
        ArgumentNullException.ThrowIfNull(update);
        try
        {
            if (moved.ProjectId == 0 || moved.DiskPath.Length == 0) return;

            // Carried over verbatim, including its flaw: a non-branch ref (a tag) is sliced at the
            // length of "refs/heads/" anyway and yields a nonsense source branch. Left as-is so this
            // stays a move; fixing it here would hide a behaviour change inside a refactor.
            var sourceBranch = update.Reference["refs/heads/".Length..];
            var tree = await cli.GetTreeAsync(moved.DiskPath, update.NewObjectId, ".pipeline", ct).ConfigureAwait(false);
            var yamlFiles = tree.Where(entry => entry.Name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                                          || entry.Name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)).ToList();
            if (yamlFiles.Count == 0) return;

            foreach (var file in yamlFiles)
            {
                var blob = await cli.GetBlobAsync(
                    moved.DiskPath, update.NewObjectId, $".pipeline/{file.Name}", ct).ConfigureAwait(false);
                if (blob is null || string.IsNullOrWhiteSpace(blob.Content)) continue;

                var definition = YamlParsingHelper.ParseAndValidate(blob.Content, out var parseError, logger);
                if (definition is null)
                {
                    logger.LogWarning(
                        "Invalid pipeline YAML in .pipeline/{File} for repo {Repo}: {Error}",
                        file.Name, moved.RepositoryName, parseError);
                    await ReportInvalidDefinitionAsync(moved, update, sourceBranch, file.Name, parseError, ct).ConfigureAwait(false);
                    continue;
                }

                var pipelineName = string.IsNullOrWhiteSpace(definition.Name)
                    ? Path.GetFileNameWithoutExtension(file.Name)
                    : definition.Name;

                await pipelineService.UpsertPipelineFromYamlAsync(
                    pipelineName, blob.Content, moved.ProjectId, definition.Trigger, ct,
                    sourceBranch, moved.DefaultBranch, moved.GitRepoId, moved.PusherUsername).ConfigureAwait(false);
                logger.LogInformation(
                    "Synced pipeline '{Name}' from {Commit}:.pipeline/{File}",
                    pipelineName, update.NewObjectId, file.Name);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to sync pipelines from .pipeline/ for repo {Repo}", moved.RepositoryName);
            await ReportSyncFailureAsync(moved, update, ex, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Recette R-49: a pushed file that cannot be read used to be skipped with a server log line
    /// only. The pipeline keeps the definition it had (nothing is written for the file), and the project's
    /// subscribers are told which file, at which commit, and the parser's own message (it names the line
    /// and column). The push itself is never blocked, and a failure to tell must not stop the other
    /// files.</summary>
    private Task ReportInvalidDefinitionAsync(
        IGitRefUpdatesEvent moved, GitRefUpdate update, string sourceBranch, string fileName, string? parseError,
        CancellationToken ct)
        => NotifyBestEffortAsync(
            moved, update, sourceBranch, fileName,
            $".pipeline/{fileName} cannot be read, so the stored pipeline was not updated (a run of it reads this same file and is refused): {parseError}",
            ct);

    /// <summary>Recette R-49: the whole sync of a ref failed (Git read, database write). The subscribers
    /// are told which repository, ref and commit, and the exception's message, instead of a server log
    /// line only.</summary>
    private Task ReportSyncFailureAsync(
        IGitRefUpdatesEvent moved, GitRefUpdate update, Exception failure, CancellationToken ct)
    {
        const string headsPrefix = "refs/heads/";
        var reference = update.Reference.StartsWith(headsPrefix, StringComparison.Ordinal)
            ? update.Reference[headsPrefix.Length..]
            : update.Reference;
        return NotifyBestEffortAsync(
            moved, update, reference, moved.RepositoryName,
            $"the pipelines of .pipeline/ could not be synchronized for repository {moved.RepositoryName}, so the stored pipelines were not updated: {failure.Message}",
            ct);
    }

    private async Task NotifyBestEffortAsync(
        IGitRefUpdatesEvent moved, GitRefUpdate update, string reference, string subject, string reason,
        CancellationToken ct)
    {
        if (moved.ProjectId == 0) return;
        try
        {
            await notifications.PublishDefinitionRefusedAsync(
                moved.ProjectId, subject, reference, update.NewObjectId, reason, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mandatory catch: telling is best effort; the remaining files must still be synced and a push
            // already written must not be reported failed.
            logger.LogWarning(ex, "The refusal of {Subject} of repo {Repo} could not be notified", subject, moved.RepositoryName);
        }
    }
}
