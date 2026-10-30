// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

internal static class PipelineAuthoritativeGitCoordinator
{
    public static async Task CommitCreateAsync(
        Pipeline pipeline, IPipelineGitService pipelineGit, ILogger logger,
        string actor, CancellationToken ct)
    {
        if (pipeline.ProjectId is not { } projectId) return;

        // R-95: a creation never overwrites a definition the repository already holds. The same
        // content is nothing to write; other content is a change Aetheus has not seen, refused
        // rather than adopted (the user typed this definition on purpose, so replacing it with the
        // repository's would hand them a pipeline they did not write).
        var repositoryYaml = await pipelineGit.ReadProjectPipelineYamlAsync(
            projectId, pipeline.Name, ct, pipeline.SourceBranch, pipeline.SourceRepositoryId)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(repositoryYaml))
        {
            if (PipelineRepositoryDefinitionGuard.Same(repositoryYaml, pipeline.YamlDefinition))
            {
                logger.LogInformation(
                    "Pipeline '{Name}' already has this definition in the project's git repository; nothing to commit.",
                    pipeline.Name);
                return;
            }
            throw new ConflictException(
                $"The project's git repository already holds a definition file for a pipeline named '{pipeline.Name}' "
                + $"on branch '{pipeline.SourceBranch ?? "default"}' with different content, and creating the pipeline "
                + "would overwrite it. Choose another name or branch, or remove that file from the repository first.");
        }

        var (outcome, error) = await pipelineGit.WriteProjectPipelineYamlAsync(
            projectId, pipeline.Name, pipeline.YamlDefinition, actor, ct,
            pipeline.SourceBranch, pipeline.SourceRepositoryId)
            .ConfigureAwait(false);
        if (outcome == GitWriteOutcome.NoRepo)
            logger.LogInformation(
                "Pipeline '{Name}' has no internal git repo; DB definition is authoritative.", pipeline.Name);
        else if (outcome == GitWriteOutcome.Failed)
            throw new ConflictException(
                $"Pipeline definition could not be committed to the project's git repository: {error}");
    }

    public static async Task CopyToLinkedEnvironmentProjectAsync(
        Pipeline pipeline, IPipelineRepository repo, IPipelineGitService pipelineGit,
        ILogger logger, string actor, CancellationToken ct)
    {
        if (pipeline.EnvironmentId is not { } environmentId) return;
        var target = await repo.GetEnvironmentCopyTargetAsync(environmentId, ct).ConfigureAwait(false);
        if (target is not { } copyTarget) return;
        try
        {
            await pipelineGit.CopyEnvironmentPipelinesToProjectAsync(
                environmentId, copyTarget.Name, copyTarget.ProjectId, actor, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Env-linked pipeline copy failed for environment {EnvId} → project {ProjectId}.",
                environmentId, copyTarget.ProjectId);
        }
    }
}
