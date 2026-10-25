// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The repository of a project's system configuration files (Apache virtual hosts, environment sample):
/// internal, in the project, named after it, and never its source (ADR-057). The deployment reads its files
/// from here first; the user may edit them in Git or ignore them. Nothing is stored in the database.
/// </summary>
public interface IProjectConfigRepositoryService
{
    /// <summary>The project's configuration repository, or null when it has none yet. It is the one repository of
    /// the project flagged as such, whichever it is: the one the wizard created, or another the user designated
    /// since (<c>UpdateGitLightRepoRequest.IsConfiguration</c>), which then replaces it for every read and write.</summary>
    Task<GitLightRepoDto?> FindAsync(int projectId, CancellationToken ct = default);

    /// <summary>Creates the repository when it is missing and commits the files it does not hold yet, keyed by
    /// path below <c>.pipeline/configs/</c>. A file already there is never overwritten.</summary>
    Task<ProjectConfigRepositoryDto> EnsureAsync(
        int projectId, IReadOnlyDictionary<string, string> files, CancellationToken ct = default);

    /// <summary>Commits a new content of one file the project's configuration repository already holds (the one
    /// exception to <see cref="EnsureAsync"/>, which never overwrites: the repair of a file the wizard wrote itself,
    /// recette R-5). Nothing outside the project's own configuration repository is read or written. Returns false,
    /// writing nothing, when the file is not there or already holds <paramref name="content"/>.</summary>
    Task<bool> ReplaceAsync(
        int projectId, string relativePath, string content, string commitMessage, CancellationToken ct = default);

    /// <summary>A versioned configuration file for a deployment: the configuration repository's version
    /// first, else the project's own source at the run's immutable revision. Null when neither has it.</summary>
    Task<string?> ReadAsync(
        int projectId, string relativePath, string? sourceRevision, int? sourceRepositoryId,
        CancellationToken ct = default);
}

public sealed class ProjectConfigRepositoryService(
    IGitLightService git,
    IGitLightCliService cli,
    IGitCommitRecorder commitRecorder,
    IPipelineGitService pipelineGit,
    IPipelineRepository pipelines,
    ILogger<ProjectConfigRepositoryService> logger) : IProjectConfigRepositoryService
{
    internal const string RepositorySuffix = "-config";
    private const long MaxFileSize = 256 * 1024;

    public async Task<GitLightRepoDto?> FindAsync(int projectId, CancellationToken ct = default) =>
        (await git.GetAccessibleRepositoriesAsync([projectId], ct).ConfigureAwait(false))
            .Find(repository => repository.ProjectId == projectId && repository.IsConfiguration);

    public async Task<ProjectConfigRepositoryDto> EnsureAsync(
        int projectId, IReadOnlyDictionary<string, string> files, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach (var path in files.Keys)
            if (!PipelineConfigTemplateRenderer.IsSafeConfigPath(path))
                throw new BadRequestException($"'{path}' is not a safe path below .pipeline/configs/.");

        var repository = await FindAsync(projectId, ct).ConfigureAwait(false);
        var created = repository is null;
        repository ??= await CreateAsync(projectId, ct).ConfigureAwait(false);

        var added = new List<string>();
        var kept = new List<string>();
        foreach (var path in files.Keys.Order(StringComparer.Ordinal))
            (await ReadBlobAsync(repository, path, ct).ConfigureAwait(false) is null ? added : kept).Add(path);

        if (added.Count > 0)
        {
            var diskPath = git.ResolveDiskPath(projectId, repository.Slug);
            var (success, _, error) = await cli.CommitFilesAsync(
                diskPath, repository.DefaultBranch, [.. added.Select(path => (path, files[path]))],
                "chore(config): add the generated configuration files via Aetheus",
                PipelineGitService.ServiceAuthorName, PipelineGitService.ServiceAuthorEmail, ct).ConfigureAwait(false);
            if (!success)
                throw new BadRequestException(
                    $"The configuration files could not be committed to '{repository.Name}': {error}");
            await commitRecorder.RecordCommitAsync(repository.Id, ct).ConfigureAwait(false);
            logger.LogInformation(
                "Committed {Count} configuration file(s) to repository {Repository} of project {ProjectId}.",
                added.Count, repository.Slug, projectId);
        }

        return new ProjectConfigRepositoryDto
        {
            Id = repository.Id,
            Name = repository.Name,
            Slug = repository.Slug,
            DefaultBranch = repository.DefaultBranch,
            Created = created,
            FilesAdded = added,
            FilesKept = kept
        };
    }

    public async Task<bool> ReplaceAsync(
        int projectId, string relativePath, string content, string commitMessage, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitMessage);
        if (!PipelineConfigTemplateRenderer.IsSafeConfigPath(relativePath))
            throw new BadRequestException($"'{relativePath}' is not a safe path below .pipeline/configs/.");

        var repository = await FindAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Project {projectId} has no configuration repository.");
        var current = await ReadBlobAsync(repository, relativePath.Trim(), ct).ConfigureAwait(false);
        if (current is null || string.Equals(current, content, StringComparison.Ordinal)) return false;

        var diskPath = git.ResolveDiskPath(projectId, repository.Slug);
        var (success, _, error) = await cli.CommitFilesAsync(
            diskPath, repository.DefaultBranch, [(relativePath.Trim(), content)], commitMessage,
            PipelineGitService.ServiceAuthorName, PipelineGitService.ServiceAuthorEmail, ct).ConfigureAwait(false);
        if (!success)
            throw new BadRequestException(
                $"'{relativePath}' could not be committed to '{repository.Name}': {error}");
        await commitRecorder.RecordCommitAsync(repository.Id, ct).ConfigureAwait(false);
        logger.LogInformation(
            "Replaced {Path} in configuration repository {Repository} of project {ProjectId}.",
            relativePath, repository.Slug, projectId);
        return true;
    }

    public async Task<string?> ReadAsync(
        int projectId, string relativePath, string? sourceRevision, int? sourceRepositoryId,
        CancellationToken ct = default)
    {
        if (!PipelineConfigTemplateRenderer.IsSafeConfigPath(relativePath))
            throw new BadRequestException("Configuration files must use a safe path under .pipeline/configs/.");

        if (await FindAsync(projectId, ct).ConfigureAwait(false) is { } repository
            && await ReadBlobAsync(repository, relativePath.Trim(), ct).ConfigureAwait(false) is { } content)
            return content;

        return PipelineRunHelpers.IsGitCommitHash(sourceRevision)
            ? await pipelineGit.ReadProjectConfigAtRevisionAsync(
                projectId, relativePath, sourceRevision!, ct, sourceRepositoryId).ConfigureAwait(false)
            : null;
    }

    private async Task<GitLightRepoDto> CreateAsync(int projectId, CancellationToken ct)
    {
        var slug = await PipelineProjectResourceNames.FindSlugAsync(pipelines, projectId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Project {projectId} not found.");
        return await git.CreateConfigurationRepositoryAsync(new CreateGitLightRepoRequest
        {
            ProjectId = projectId,
            Name = slug + RepositorySuffix,
            Description = "System configuration files of the project (Apache virtual hosts, environment sample), "
                + "read by its deployment pipelines. Edit them here."
        }, ct).ConfigureAwait(false);
    }

    private async Task<string?> ReadBlobAsync(GitLightRepoDto repository, string path, CancellationToken ct)
    {
        try
        {
            var blob = await git.GetBlobAsync(repository.Id, repository.DefaultBranch, path, ct).ConfigureAwait(false);
            return blob is { IsBinary: false, Content: { } content } && blob.Size <= MaxFileSize ? content : null;
        }
        catch (InvalidOperationException)
        {
            // Mandatory: git reports an empty repository or an unborn branch as an error, which only means the
            // file is not there yet.
            return null;
        }
    }
}
