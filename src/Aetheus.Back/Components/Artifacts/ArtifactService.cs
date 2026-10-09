// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Artifacts;

public sealed class ArtifactService(
    IArtifactRepository repo,
    IArtifactStorageService storage,
    IArtifactRetentionService retention,
    // No IPipelineRunService, no IReleaseRepository. Both were injected for reads of entities this
    // module already shares, and both put Artifacts inside a module cycle. The reads moved to
    // IArtifactRepository; the writes stayed where they were.
    IConfiguration configuration,
    ILogger<ArtifactService> logger,
    IDbTransactionScope? transaction = null,
    IPostgresLeaderLease? operationLock = null,
    TimeProvider? timeProvider = null) : IArtifactService
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> ProjectUploadLocks = new();

    // Per-project storage quota (bytes). 0 = unlimited; unset uses the 20 GiB default. Enforced before a save so a compromised
    // or runaway agent can't saturate the artifact volume via many sub-1-GiB uploads (audit: per-request
    // cap alone bounds one upload, not the aggregate). Default 20 GiB.
    private readonly long _projectQuotaBytes = configuration.GetValue<long>("ArtifactStorage:ProjectQuotaBytes", 20L * 1024 * 1024 * 1024);

    public async Task<PipelineArtifactDto?> PublishArtifactAsync(
        int runId, string name, string? stageName, long contentLength, Stream fileContent, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);

        var context = await repo.GetRunPipelineContextAsync(runId, ct).ConfigureAwait(false);
        if (context is null) return null;

        var (pipelineId, projectId) = context.Value;

        // BACK-009: refused before a byte is stored. Without an owning project there is no quota to
        // apply, no retention to resolve, and no permission under which the artifact could be read.
        if (projectId is not int pid)
            throw new BadRequestException("The pipeline of this run has no owning project; its artifacts cannot be stored.");

        var uploadLock = ProjectUploadLocks.GetOrAdd(pid, static _ => new SemaphoreSlim(1, 1));
        await uploadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureProjectCapacityAsync(pid, contentLength, ct).ConfigureAwait(false);
            return await SaveArtifactAsync(runId, pipelineId, pid, name, stageName, fileContent, ct).ConfigureAwait(false);
        }
        finally
        {
            uploadLock.Release();
        }
    }

    private async Task EnsureProjectCapacityAsync(int projectId, long incomingSizeBytes, CancellationToken ct)
    {
        if (_projectQuotaBytes <= 0) return;

        var used = await repo.GetProjectTotalSizeBytesAsync(projectId, ct).ConfigureAwait(false);
        if (HasCapacity(used, incomingSizeBytes)) return;

        // Build artifacts are the disposable quota cohort. Released/deployed artifacts are never
        // touched, and the newest build of every pipeline is retained so a recent fast deployment
        // remains possible. Eviction is deterministic and happens before ASP.NET reads the request
        // body (paired with Expect: 100-continue on the agent), avoiding a multi-hundred-MB upload
        // that can only be rejected.
        var builds = await repo.GetProjectBuildArtifactsAsync(projectId, ct).ConfigureAwait(false) ?? [];
        var protectedIds = builds
            .GroupBy(a => a.PipelineId)
            .Select(group => group.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).First().Id)
            .ToHashSet();

        // A checkpoint resume holds a retention lease on the artifacts it reuses; evicting one would
        // break that resume. Leased artifacts are skipped here and revalidated under the lock below,
        // since a lease can be taken after this query.
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var candidates = builds.Where(a => !protectedIds.Contains(a.Id) && !IsLeased(a, now));

        foreach (var artifact in candidates)
        {
            try
            {
                // R-463: the same per-artifact lock as the expiry cleanup and the checkpoint reuse, so
                // two paths never delete one artifact at the same time.
                var evicted = false;
                if (operationLock is null)
                    evicted = await EvictUnlessLeasedAsync(artifact, now, ct).ConfigureAwait(false);
                else
                    await operationLock.RunSerializedAsync(
                        ArtifactRetentionLock.For(artifact.Id),
                        async lockToken => evicted = await EvictUnlessLeasedAsync(artifact, now, lockToken).ConfigureAwait(false),
                        ct).ConfigureAwait(false);
                if (!evicted)
                {
                    logger.LogInformation(
                        "Artifact quota eviction: kept build artifact {ArtifactId}, a checkpoint resume holds its retention lease",
                        artifact.Id);
                    continue;
                }

                used = Math.Max(0, used - artifact.SizeBytes);
                logger.LogInformation(
                    "Artifact quota eviction: deleted build artifact {ArtifactId} ({Size} bytes) for project {ProjectId}",
                    artifact.Id, artifact.SizeBytes, projectId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Artifact quota eviction failed for artifact {ArtifactId}; trying the next candidate", artifact.Id);
            }

            if (HasCapacity(used, incomingSizeBytes)) return;
        }

        logger.LogWarning(
            "Artifact upload rejected for project {ProjectId}: quota cannot fit {Incoming} bytes ({Used}/{Quota} bytes used)",
            projectId, incomingSizeBytes, used, _projectQuotaBytes);
        throw new ConflictException("Project artifact storage quota cannot fit this artifact without deleting retained artifacts.");
    }

    private static bool IsLeased(PipelineArtifact artifact, DateTime now) =>
        artifact.RetentionLeaseExpiresAt is { } leaseExpiresAt && leaseExpiresAt > now;

    private async Task<bool> EvictUnlessLeasedAsync(PipelineArtifact artifact, DateTime now, CancellationToken ct)
    {
        if (await repo.HasActiveRetentionLeaseAsync(artifact.Id, now, ct).ConfigureAwait(false))
            return false;

        await storage.DeleteArtifactAsync(artifact.FilePath, ct).ConfigureAwait(false);
        await repo.RemoveAsync(artifact, ct).ConfigureAwait(false);
        return true;
    }

    private bool HasCapacity(long usedBytes, long incomingSizeBytes) =>
        incomingSizeBytes <= _projectQuotaBytes && usedBytes <= _projectQuotaBytes - incomingSizeBytes;

    private async Task<PipelineArtifactDto> SaveArtifactAsync(
        int runId,
        int pipelineId,
        int? projectId,
        string name,
        string? stageName,
        Stream fileContent,
        CancellationToken ct)
    {
        var fileName = $"{name}-{runId}.zip";

        var (relativePath, sha256) = await storage.SaveArtifactAsync(
            projectId ?? 0, pipelineId, runId, fileName, fileContent, ct).ConfigureAwait(false);

        var sizeBytes = storage.GetArtifactSize(relativePath);

        var artifact = new PipelineArtifact
        {
            PipelineRunId = runId,
            PipelineId = pipelineId,
            ProjectId = projectId,
            Name = name,
            FilePath = relativePath,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            StageName = stageName
        };

        await repo.AddAsync(artifact, ct).ConfigureAwait(false);
        await retention.ApplyBuildRetentionAsync(artifact, ct).ConfigureAwait(false);

        // A release step and the post-stage artifact collection can legitimately complete in either
        // order. When the release already exists, attach this freshly-uploaded payload immediately;
        // otherwise ReleaseService performs the symmetric lookup after creating the release.
        var release = await repo.FindReleaseForRunAsync(runId, ct).ConfigureAwait(false);
        if (release is not null)
            await retention.ApplyReleaseRetentionAsync(artifact, release.Id, ct).ConfigureAwait(false);

        logger.LogInformation("Artifact {Name} published for run {RunId} ({Size} bytes)", name, runId, sizeBytes);
        return MapToDto(artifact);
    }

    public async Task<PaginatedResult<PipelineArtifactDto>> GetProjectArtifactsAsync(int projectId, ProjectArtifactsRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetByProjectPagedAsync(
            projectId, request.Policy, request.PipelineId, page, pageSize, ct,
            request.Filters, request.SortBy,
            request.SortBy is null || request.SortDescending).ConfigureAwait(false);

        return new PaginatedResult<PipelineArtifactDto>
        {
            Items = items.Select(MapToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>Recette R-210: the names the project artifacts grid's checkable filters offer.</summary>
    public Task<ProjectArtifactFilterValuesDto> GetProjectArtifactFilterValuesAsync(int projectId, CancellationToken ct = default) =>
        repo.GetProjectFilterValuesAsync(projectId, ct);

    public async Task<PipelineArtifactDto?> GetArtifactAsync(int id, CancellationToken ct = default)
    {
        var artifact = await repo.FindAsync(id, ct).ConfigureAwait(false);
        return artifact is null ? null : MapToDto(artifact);
    }

    public async Task<Stream?> DownloadArtifactAsync(int id, CancellationToken ct = default)
    {
        var artifact = await repo.FindAsync(id, ct).ConfigureAwait(false);
        if (artifact is null) return null;
        return storage.OpenArtifact(artifact.FilePath);
    }

    public async Task<PipelineArtifactDto?> PromoteToEnvironmentAsync(int id, string environmentName, CancellationToken ct = default)
    {
        var artifact = await repo.FindAsync(id, ct).ConfigureAwait(false);
        if (artifact is null) return null;

        await retention.ApplyDeployRetentionAsync(artifact, environmentName, ct).ConfigureAwait(false);
        logger.LogInformation("Artifact {Id} promoted to environment {Env}", id, environmentName);
        return MapToDto(artifact);
    }

    public async Task<PipelineArtifactDto?> PromoteToReleaseAsync(int artifactId, int releaseId, CancellationToken ct = default)
    {
        var artifact = await repo.FindAsync(artifactId, ct).ConfigureAwait(false);
        if (artifact is null) return null;

        // The two ids arrive independently, so nothing but this check keeps them in the same project.
        // Fail-closed on an unresolvable owner: an artifact or release whose project cannot be named
        // is refused rather than promoted across a tenancy boundary.
        var artifactProjectId = await repo.GetArtifactOwningProjectIdAsync(artifactId, ct).ConfigureAwait(false);
        var releaseProjectId = await repo.GetReleaseProjectIdAsync(releaseId, ct).ConfigureAwait(false);
        if (artifactProjectId is not { } owner || releaseProjectId is not { } target || owner != target)
        {
            logger.LogWarning(
                "Refused promotion of artifact {ArtifactId} (project {ArtifactProject}) to release {ReleaseId} (project {ReleaseProject}): different or unresolvable owners.",
                artifactId, artifactProjectId, releaseId, releaseProjectId);
            return null;
        }

        await retention.ApplyReleaseRetentionAsync(artifact, releaseId, ct).ConfigureAwait(false);
        logger.LogInformation("Artifact {Id} promoted to release {ReleaseId}", artifactId, releaseId);
        return MapToDto(artifact);
    }

    public async Task<bool> MarkDeployedAsync(
        int artifactId, string environmentName, int? releaseId = null, CancellationToken ct = default)
    {
        var artifact = await repo.FindAsync(artifactId, ct).ConfigureAwait(false); // FindAsync includes Releases
        if (artifact is null) return false;

        async Task PersistAsync()
        {
            await retention.ApplyDeployRetentionAsync(artifact, environmentName, ct).ConfigureAwait(false);

            // A project has exactly one active deployed release. Demote the former active release in
            // the same transaction before promoting the release linked to this artifact.
            var targetRelease = releaseId is int selectedId
                ? artifact.Releases.SingleOrDefault(release => release.Id == selectedId)
                : null;
            if (releaseId.HasValue && targetRelease is null)
                throw new InvalidOperationException(
                    $"Release {releaseId.Value} is not linked to deployed artifact {artifactId}.");

            if (artifact.ProjectId is int projectId && targetRelease is not null)
            {
                var deployedReleases = await repo.GetDeployedProjectReleasesAsync(projectId, ct).ConfigureAwait(false);
                var demoted = false;
                foreach (var deployedRelease in deployedReleases.Where(release => release.Id != targetRelease.Id))
                {
                    deployedRelease.Status = ReleaseStatus.Superseded;
                    demoted = true;
                }
                if (demoted) await repo.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            if (targetRelease is not null && targetRelease.Status != ReleaseStatus.Deployed)
            {
                targetRelease.Status = ReleaseStatus.Deployed;
                await repo.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        if (transaction?.IsRelational == true)
        {
            await transaction.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await PersistAsync().ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }
        else
        {
            await PersistAsync().ConfigureAwait(false);
        }

        logger.LogInformation("Artifact {Id} marked deployed (env {Env})", artifactId, environmentName);
        return true;
    }

    public async Task<bool> IsAgentAssignedToRunAsync(int runId, int serverId, CancellationToken ct = default) =>
        await repo.IsServerAssignedToRunAsync(runId, serverId, ct).ConfigureAwait(false);

    public async Task<int?> GetOwningProjectIdAsync(int artifactId, CancellationToken ct = default) =>
        await repo.GetArtifactOwningProjectIdAsync(artifactId, ct).ConfigureAwait(false);

    public async Task<(DeployDownloadStatus Status, Stream? Stream, string? FileName)> OpenArtifactForAgentAsync(
        int artifactId, int deployRunId, int agentServerId, CancellationToken ct = default)
    {
        var artifact = await repo.FindAsync(artifactId, ct).ConfigureAwait(false);
        if (artifact is null) return (DeployDownloadStatus.NotFound, null, null);

        // Fail-closed: a project-less artifact has no org to authorise against.
        if (artifact.ProjectId is null || artifact.Project is null)
            return (DeployDownloadStatus.Forbidden, null, null);

        // The agent must be a participant of the DEPLOY run it claims to be executing. In scenario 3
        // the artifact's own PipelineRunId points at a different (build) run, so we deliberately do
        // NOT check assignment against artifact.PipelineRunId.
        if (!await repo.IsServerAssignedToRunAsync(deployRunId, agentServerId, ct).ConfigureAwait(false))
            return (DeployDownloadStatus.Forbidden, null, null);

        // Org isolation: the agent's server org must equal the artifact's project org.
        var serverOrg = await repo.GetServerOrganizationIdAsync(agentServerId, ct).ConfigureAwait(false);
        if (serverOrg is null || serverOrg.Value != artifact.Project.OrganizationId)
            return (DeployDownloadStatus.Forbidden, null, null);

        var stream = storage.OpenArtifact(artifact.FilePath);
        if (stream is null) return (DeployDownloadStatus.NotFound, null, null);

        logger.LogInformation("Deploy download: artifact {Id} served to server {ServerId} for deploy run {RunId}",
            artifactId, agentServerId, deployRunId);
        return (DeployDownloadStatus.Ok, stream, $"{artifact.Name}.zip");
    }

    private static PipelineArtifactDto MapToDto(PipelineArtifact a) => new()
    {
        Id = a.Id,
        PipelineRunId = a.PipelineRunId,
        PipelineId = a.PipelineId,
        ProjectId = a.ProjectId,
        Name = a.Name,
        FilePath = a.FilePath,
        SizeBytes = a.SizeBytes,
        Sha256 = a.Sha256,
        StageName = a.StageName,
        StepName = a.StepName,
        CreatedAt = a.CreatedAt,
        RetentionPolicy = a.RetentionPolicy,
        RetentionExpiresAt = a.RetentionExpiresAt,
        EnvironmentName = a.EnvironmentName,
        PipelineName = a.Pipeline?.Name,
        ProjectName = a.Project?.Name,
        // Git context inherited from the producing run (req. b). The run carries the branch/commit;
        // fall back to the first linked release's git fields when the run nav isn't loaded.
        BranchName = a.PipelineRun?.BranchName ?? (a.Releases.Count > 0 ? a.Releases[0].BranchName : null),
        CommitHash = a.PipelineRun?.CommitHash ?? (a.Releases.Count > 0 ? a.Releases[0].CommitHash : null),
        RepositoryUrl = a.Project?.RepositoryUrl,
        SourceRepositoryId = a.Pipeline?.SourceRepositoryId,
        Releases = a.Releases.Select(GitGraphMapper.ToLink).ToList(),
        Commits = a.Commits.Select(GitGraphMapper.ToLink).ToList(),
        Branches = a.Branches.Select(GitGraphMapper.ToLink).ToList()
    };
}
