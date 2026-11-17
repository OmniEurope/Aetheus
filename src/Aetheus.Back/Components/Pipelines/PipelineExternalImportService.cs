// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The import keeps the security properties the policy states (<see cref="PipelineExternalImportPolicy"/>): the stored
/// definition is the reviewed file verbatim, pinned by its commit and hash; the pipeline is
/// <see cref="PipelineTriggerType.Manual"/> whatever the file says; it is bound to the mirror for its workspace and
/// never written back to it. The caller (controller) checks WRITE on the project before an import.
/// </summary>
internal sealed class PipelineExternalImportService(
    PipelineExternalMirrorReader mirrors,
    IPipelineExternalImportRepository pipelines,
    IPipelineRepository pipelineRepository,
    IAuditService audit,
    IEntityChangeNotifier notifier,
    TimeProvider timeProvider) : IPipelineExternalImportService
{
    public async Task<ExternalPipelineCandidatesDto> ListAsync(int projectId, CancellationToken ct = default)
    {
        var repositories = await mirrors.GetMirrorsAsync(projectId, ct).ConfigureAwait(false);
        if (repositories.Count == 0) return new ExternalPipelineCandidatesDto();

        var rows = await pipelines.GetProjectPipelinesAsync(projectId, ct).ConfigureAwait(false);
        var items = new List<ExternalPipelineCandidateDto>();
        var unreadable = new List<string>();
        foreach (var repository in repositories)
        {
            var snapshot = await mirrors.ReadAsync(projectId, repository, ct, allowCached: true).ConfigureAwait(false);
            if (snapshot.Unreadable is not null)
            {
                unreadable.Add($"{repository.Name}: {snapshot.Unreadable}");
                continue;
            }
            items.AddRange(snapshot.Files.Select(file => Describe(file, rows)));
            if (snapshot.SkippedFiles > 0)
                unreadable.Add(
                    $"{repository.Name}: only the first {PipelineExternalMirrorReader.MaxFiles} files of .pipeline/ are read, {snapshot.SkippedFiles} more are ignored.");
        }
        return new ExternalPipelineCandidatesDto { HasExternalRepository = true, Items = items, UnreadableRepositories = unreadable };
    }

    public async Task<PipelineDto> ImportAsync(
        int projectId, ImportExternalPipelineRequest request, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var (repository, file) = await ResolveImportableFileAsync(projectId, request, ct).ConfigureAwait(false);

        var existing = await pipelines.FindProjectPipelineAsync(projectId, file.Name, ct).ConfigureAwait(false);
        var hash = PipelineRepositoryDefinitionGuard.Sha256(file.Yaml!);
        if (existing is not null)
        {
            EnsureSameSource(existing, repository.Id, file);
            if (string.Equals(existing.ExternalImportSha256, hash, StringComparison.Ordinal))
                return PipelineDtoMapper.Map(existing);
        }

        var pipeline = existing ?? new Pipeline { Name = file.Name, ProjectId = projectId, CreatedByUsername = actor };
        Apply(pipeline, repository, file, hash, actor);
        await PersistAsync(pipeline, isNew: existing is null, file.Name, ct).ConfigureAwait(false);
        await audit.LogAsync(
            existing is null ? "ExternalImported" : "ExternalReimported", "Pipeline", pipeline.Id,
            $"{pipeline.Name}: {file.FilePath} of {repository.Name} at {file.CommitSha}", ct).ConfigureAwait(false);
        var organizationId = await pipelineRepository.GetPipelineOwnerOrganizationIdAsync(projectId, null, null, ct)
            .ConfigureAwait(false);
        await notifier.BroadcastAsync(
            ResourceType.Pipeline, pipeline.Id, existing is null ? EntityChangeOps.Created : EntityChangeOps.Updated,
            ct, organizationId).ConfigureAwait(false);
        return PipelineDtoMapper.Map(pipeline);
    }

    private async Task<(GitLightRepoDto Repository, PipelineExternalMirrorFile File)> ResolveImportableFileAsync(
        int projectId, ImportExternalPipelineRequest request, CancellationToken ct)
    {
        if (!PipelineExternalImportPolicy.IsSafeFilePath(request.FilePath))
            throw new BadRequestException("The file must be a .pipeline/*.yaml file of the repository.");
        if (!PipelineRunHelpers.IsGitCommitHash(request.CommitSha))
            throw new BadRequestException("The commit must be a full Git commit hash.");

        var repository = (await mirrors.GetMirrorsAsync(projectId, ct).ConfigureAwait(false))
            .FirstOrDefault(mirror => mirror.Id == request.RepositoryId)
            ?? throw new NotFoundException($"Repository {request.RepositoryId} is not an external repository mirror of project {projectId}.");
        var snapshot = await mirrors.ReadAsync(projectId, repository, ct).ConfigureAwait(false);
        if (snapshot.Unreadable is not null)
            throw new ConflictException($"The mirror {repository.Name} cannot be read: {snapshot.Unreadable}");
        if (!string.Equals(snapshot.CommitSha, request.CommitSha, StringComparison.OrdinalIgnoreCase))
            throw new ConflictException(
                $"The mirror {repository.Name} moved since the list was read (now at {snapshot.CommitSha}): reload it and review the file again.");
        var file = snapshot.Files.FirstOrDefault(candidate => string.Equals(candidate.FilePath, request.FilePath, StringComparison.Ordinal))
            ?? throw new NotFoundException($"{request.FilePath} is not in the mirror {repository.Name} at {request.CommitSha}.");
        if (file.Yaml is null || file.Problems.Count > 0)
            throw new BadRequestException(
                $"{request.FilePath} cannot be imported: " + string.Join(" ", file.Problems));
        return (repository, file);
    }

    private async Task PersistAsync(Pipeline pipeline, bool isNew, string name, CancellationToken ct)
    {
        if (!isNew)
        {
            await pipelines.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }
        // Two imports of the same file at once both found nothing: the project's unique pipeline name lets one win.
        if (!await pipelines.TryAddAsync(pipeline, ct).ConfigureAwait(false))
            throw new ConflictException(
                $"The pipeline '{name}' was imported by another request at the same time: reload the list and review it again.");
    }

    private void Apply(Pipeline pipeline, GitLightRepoDto repository, PipelineExternalMirrorFile file, string hash, string actor)
    {
        pipeline.YamlDefinition = file.Yaml!;
        pipeline.Description = $"Imported read-only from {file.FilePath} of the external repository {repository.Name}.";
        pipeline.TriggerType = PipelineTriggerType.Manual;
        pipeline.SourceRepositoryId = repository.Id;
        pipeline.SourceBranch = repository.DefaultBranch;
        pipeline.ExternalImportPath = file.FilePath;
        pipeline.ExternalImportCommit = file.CommitSha;
        pipeline.ExternalImportSha256 = hash;
        pipeline.ExternalImportedAt = timeProvider.GetUtcNow().UtcDateTime;
        pipeline.ExternalImportedBy = actor;
        pipeline.RowVersion = Guid.NewGuid();
        PipelineTemplateReferenceMetadata.Apply(pipeline, file.Yaml!);
    }

    private static void EnsureSameSource(Pipeline existing, int repositoryId, PipelineExternalMirrorFile file)
    {
        if (!existing.IsExternalImport)
            throw new ConflictException(
                $"A pipeline named '{file.Name}' is already defined in Aetheus: {file.FilePath} is never imported over it.");
        if (existing.SourceRepositoryId != repositoryId
            || !string.Equals(existing.ExternalImportPath, file.FilePath, StringComparison.Ordinal))
            throw new ConflictException(
                $"The pipeline '{file.Name}' was imported from {existing.ExternalImportPath} of another repository or file.");
    }

    private static ExternalPipelineCandidateDto Describe(PipelineExternalMirrorFile file, List<PipelineExternalImportRow> rows)
    {
        var row = rows.FirstOrDefault(candidate => string.Equals(candidate.Name, file.Name, StringComparison.Ordinal));
        var state = row switch
        {
            null => file.Problems.Count > 0 || file.Yaml is null ? ExternalPipelineImportState.Refused : ExternalPipelineImportState.Available,
            { ExternalImportCommit: null } => ExternalPipelineImportState.NameTaken,
            _ when row.SourceRepositoryId != file.Repository.Id
                   || !string.Equals(row.ExternalImportPath, file.FilePath, StringComparison.Ordinal) => ExternalPipelineImportState.NameTaken,
            _ when file.Yaml is not null
                   && string.Equals(row.ExternalImportSha256, PipelineRepositoryDefinitionGuard.Sha256(file.Yaml), StringComparison.Ordinal)
                => ExternalPipelineImportState.Imported,
            _ => ExternalPipelineImportState.ChangedSinceImport
        };
        return new ExternalPipelineCandidateDto
        {
            RepositoryId = file.Repository.Id,
            RepositoryName = file.Repository.Name,
            FilePath = file.FilePath,
            Name = file.Name,
            CommitSha = file.CommitSha,
            State = state,
            PipelineId = row?.Id,
            ImportedCommitSha = row?.ExternalImportCommit,
            Problems = file.Problems
        };
    }
}
