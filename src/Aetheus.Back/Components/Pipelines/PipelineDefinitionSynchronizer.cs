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

                var definition = YamlParsingHelper.ParseAndValidate(blob.Content, logger);
                if (definition is null)
                {
                    logger.LogWarning(
                        "Invalid pipeline YAML in .pipeline/{File} for repo {Repo}", file.Name, moved.RepositoryName);
                    continue;
                }

                var pipelineName = string.IsNullOrWhiteSpace(definition.Name)
                    ? Path.GetFileNameWithoutExtension(file.Name)
                    : definition.Name;

                await pipelineService.UpsertPipelineFromYamlAsync(
                    pipelineName, blob.Content, moved.ProjectId, definition.Trigger, ct,
                    sourceBranch, moved.DefaultBranch, moved.GitRepoId).ConfigureAwait(false);
                logger.LogInformation(
                    "Synced pipeline '{Name}' from {Commit}:.pipeline/{File}",
                    pipelineName, update.NewObjectId, file.Name);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sync pipelines from .pipeline/ for repo {Repo}", moved.RepositoryName);
        }
    }
}
