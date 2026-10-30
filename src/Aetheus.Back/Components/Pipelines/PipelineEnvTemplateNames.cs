// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Recette R-68: the variable names the environment sample (<c>env_template</c>) of a pipeline asks for. A library entry
/// the sample reads is consumed through <c>#{NAME}#</c>, which the <c>$(NAME)</c> scan of the pipeline cannot see, so the
/// readiness reads the sample itself: an entry left empty that the sample needs is a value to fill before the run, the
/// same verdict the run reaches when it renders the file.
/// </summary>
internal sealed class PipelineEnvTemplateNames(
    IProjectConfigRepositoryService configRepository, IGitLightService git, ILogger logger)
{
    /// <summary>The names every sample the definition's steps name asks for. A sample that cannot be read adds nothing: a
    /// missing file is reported by the run, not guessed here.</summary>
    public async Task<IReadOnlyCollection<string>> ReadAsync(PipelineYamlDefinition definition, int projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var paths = YamlParsingHelper.FlattenJobs(definition)
            .SelectMany(stage => stage.Steps)
            .Select(step => step.EnvTemplate)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => PipelineRunHelpers.SubstituteVariables(path!, definition.Variables).Trim())
            .Where(PipelineConfigTemplateRenderer.IsSafeConfigPath)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (paths.Count == 0) return [];

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
            if (await ReadTemplateAsync(projectId, path, ct).ConfigureAwait(false) is { } template)
                names.UnionWith(PipelineEnvTemplateRenderer.ReferencedNames(template));
        return names;
    }

    private async Task<string?> ReadTemplateAsync(int projectId, string path, CancellationToken ct)
    {
        if (await configRepository.ReadAsync(projectId, path, null, null, ct).ConfigureAwait(false) is { } fromConfiguration)
            return fromConfiguration;

        // Not in the configuration repository: the project's own source, on its default branch.
        var repositories = (await git.GetRepositoriesAsync(projectId, ct).ConfigureAwait(false))
            .Where(item => !item.IsAdditionalSource && !item.IsConfiguration && !item.IsEmpty)
            .ToList();
        if (repositories.Count == 0) return null;
        var repository = repositories[0];
        var branch = string.IsNullOrWhiteSpace(repository.DefaultBranch) ? "main" : repository.DefaultBranch;
        try
        {
            var blob = await git.GetBlobAsync(repository.Id, branch, path, ct).ConfigureAwait(false);
            return blob is { IsBinary: false, Content: { } content } ? content : null;
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(
                exception, "Setup readiness could not read '{Path}' on '{Branch}' of repository {RepositoryId}.",
                path, branch, repository.Id);
            return null;
        }
    }
}
