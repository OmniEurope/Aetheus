// SPDX-License-Identifier: EUPL-1.2
using System.Linq.Expressions;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Artifacts;

public sealed class ArtifactRepository(AppDbContext db) : IArtifactRepository
{
    private static readonly ReleaseStatus[] PublishedReleaseStatuses =
        [ReleaseStatus.Published, ReleaseStatus.Deployed, ReleaseStatus.Superseded];

    private static readonly ReleaseStatus[] DeployedReleaseStatuses =
        [ReleaseStatus.Deployed, ReleaseStatus.Superseded];

    /// <summary>How many of a project's most recently deployed releases keep their payload past
    /// ordinary retention: the deployed one and the two it superseded, the redeployable window.</summary>
    private const int RetainedDeployedReleases = 3;

    /// <summary> The header filters of a project's artifacts grid. The size column shows a
    /// formatted size and the release column a list of links, so the grid offers no filter on them.
    /// </summary>
    internal static readonly GridQueryMap<PipelineArtifact> ProjectColumns = new GridQueryMap<PipelineArtifact>()
        .Text("Name", a => a.Name)
        .Text("PipelineName", a => a.Pipeline.Name)
        .Enum("RetentionPolicy", a => a.RetentionPolicy)
        .Text("EnvironmentName", a => a.EnvironmentName)
        .Number("SizeBytes", a => a.SizeBytes)
        .Date("CreatedAt", a => a.CreatedAt);

    public async Task<PipelineArtifact?> FindAsync(int id, CancellationToken ct = default) =>
        await db.PipelineArtifacts
            .Where(a => a.Id == id)
            .Include(a => a.Pipeline)
            .Include(a => a.Project)
            .Include(a => a.Releases)
            .Include(a => a.Commits)
            .Include(a => a.Branches)
            .Include(a => a.PipelineRun)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public async Task<(List<PipelineArtifact> Items, int TotalCount)> GetByProjectPagedAsync(
        int projectId, ArtifactRetentionPolicy? policy, int? pipelineId, int page, int pageSize, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? columnFilters = null, string? sortBy = null, bool sortDescending = true)
    {
        var query = db.PipelineArtifacts
            .Where(a => a.ProjectId == projectId)
            .AsNoTracking();

        if (policy.HasValue)
            query = query.Where(a => a.RetentionPolicy == policy.Value);
        if (pipelineId.HasValue)
            query = query.Where(a => a.PipelineId == pipelineId.Value);
        query = ProjectColumns.ApplyFilters(query, columnFilters);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        var detailQuery = query
            .Include(a => a.Pipeline)
            .Include(a => a.Releases)
            .Include(a => a.PipelineRun)
            .AsSplitQuery();
        var ordered = ProjectColumns.ApplySorts(detailQuery,
            [new GridSort { Field = sortBy ?? "CreatedAt", Descending = sortDescending }])!;
        var items = await ordered.ThenBy(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        return (items, totalCount);
    }

    /// <summary>The pipeline and environment names present across a project's artifacts.</summary>
    public async Task<ProjectArtifactFilterValuesDto> GetProjectFilterValuesAsync(int projectId, CancellationToken ct = default)
    {
        var rows = await db.PipelineArtifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId)
            .Select(a => new { PipelineName = a.Pipeline.Name, a.EnvironmentName })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        static List<string> Distinct(IEnumerable<string?> values) => [.. values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
        return new ProjectArtifactFilterValuesDto
        {
            PipelineNames = Distinct(rows.Select(row => row.PipelineName)),
            EnvironmentNames = Distinct(rows.Select(row => row.EnvironmentName))
        };
    }

    public async Task<List<PipelineArtifact>> GetByPipelineAndProjectAsync(
        int pipelineId, int projectId, ArtifactRetentionPolicy policy, CancellationToken ct = default) =>
        await db.PipelineArtifacts
            .Where(a => a.PipelineId == pipelineId && a.ProjectId == projectId && a.RetentionPolicy == policy)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<List<PipelineArtifact>> GetByRunAsync(int pipelineRunId, CancellationToken ct = default) =>
        await db.PipelineArtifacts
            .Where(artifact => artifact.PipelineRunId == pipelineRunId)
            .OrderByDescending(artifact => artifact.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<List<PipelineArtifact>> GetByEnvironmentAsync(
        int pipelineId, int projectId, string environmentName, CancellationToken ct = default) =>
        await db.PipelineArtifacts
            .Where(a => a.PipelineId == pipelineId && a.ProjectId == projectId
                        && a.EnvironmentName == environmentName && a.RetentionPolicy == ArtifactRetentionPolicy.Deployed)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<List<PipelineArtifact>> GetReleasesAsync(
        int pipelineId, int projectId, CancellationToken ct = default) =>
        await db.PipelineArtifacts
            .Where(a => a.PipelineId == pipelineId && a.ProjectId == projectId
                        && a.RetentionPolicy == ArtifactRetentionPolicy.Released)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<List<PipelineArtifact>> GetExpiredAsync(DateTime cutoff, int batchSize, CancellationToken ct = default) =>
        await db.PipelineArtifacts
            .Where(a => a.RetentionExpiresAt <= cutoff
                && (a.RetentionLeaseExpiresAt == null || a.RetentionLeaseExpiresAt <= cutoff)
                // Protect the live deployed release and the two preceding deployments from ordinary
                // payload retention. A fourth deployment makes the oldest payload eligible again.
                && !a.Releases.Any(release => release.Status == ReleaseStatus.Deployed
                    || (release.Status == ReleaseStatus.Superseded
                        && db.Releases.Count(newer => newer.ProjectId == release.ProjectId
                            && (newer.Status == ReleaseStatus.Deployed || newer.Status == ReleaseStatus.Superseded)
                            && (newer.PublishedAt ?? newer.DetectedAt) > (release.PublishedAt ?? release.DetectedAt))
                        < RetainedDeployedReleases))
                && !db.DependencyTrackOutboxItems.Any(item =>
                    item.PipelineArtifactId == a.Id && item.CompletedAt == null))
            .OrderBy(a => a.RetentionExpiresAt)
            .Take(batchSize)
            .ToListAsync(ct).ConfigureAwait(false);

    public Task<bool> HasActiveRetentionLeaseAsync(
        int artifactId,
        DateTime at,
        CancellationToken ct = default) =>
        db.PipelineArtifacts
            .AsNoTracking()
            .AnyAsync(
                artifact => artifact.Id == artifactId
                    && artifact.RetentionLeaseExpiresAt != null
                    && artifact.RetentionLeaseExpiresAt > at,
                ct);

    public Task<List<string>> GetAllFilePathsAsync(CancellationToken ct = default) =>
        db.PipelineArtifacts.AsNoTracking().Select(artifact => artifact.FilePath).Distinct().ToListAsync(ct);

    public Task<bool> IsFilePathReferencedAsync(string filePath, CancellationToken ct = default) =>
        db.PipelineArtifacts.AsNoTracking().AnyAsync(artifact => artifact.FilePath == filePath, ct);

    public async Task<List<PipelineArtifact>> GetProjectBuildArtifactsAsync(
        int projectId, CancellationToken ct = default) =>
        await db.PipelineArtifacts
            .Where(a => a.ProjectId == projectId
                        && a.RetentionPolicy == ArtifactRetentionPolicy.Build
                        // The release link is the authoritative retention boundary. A stale policy
                        // value must never make a rollback payload eligible for quota eviction.
                        && !a.Releases.Any()
                        // A durable Dependency-Track delivery must retain its exact immutable SBOM
                        // payload until the worker reaches a terminal state.
                        && !db.DependencyTrackOutboxItems.Any(item =>
                            item.PipelineArtifactId == a.Id && item.CompletedAt == null))
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task AddAsync(PipelineArtifact artifact, CancellationToken ct = default)
    {
        db.PipelineArtifacts.Add(artifact);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task LinkReleaseAsync(PipelineArtifact artifact, int releaseId, CancellationToken ct = default)
    {
        var release = await db.Releases.FindAsync([releaseId], ct).ConfigureAwait(false);
        if (release is null) return;
        await db.Entry(artifact).Collection(a => a.Releases).LoadAsync(ct).ConfigureAwait(false);
        if (artifact.Releases.All(r => r.Id != releaseId))
            artifact.Releases.Add(release);
    }

    public async Task<bool> RemoveAsync(PipelineArtifact artifact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        // Delete by identifier so an already removed row is a successful idempotent outcome rather
        // than a concurrency error.
        if (db.Database.IsRelational())
        {
            // One transaction: a completed Dependency-Track delivery no longer needs its payload, but
            // its foreign key refuses the delete (ON DELETE RESTRICT), so the history row stays without
            // the link, exactly as the release retention does. An unfinished delivery keeps the link
            // and still refuses the delete: its payload is not released.
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await db.DependencyTrackOutboxItems
                .Where(item => item.PipelineArtifactId == artifact.Id && item.CompletedAt != null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.PipelineArtifactId, (int?)null), ct)
                .ConfigureAwait(false);
            var deleted = await db.PipelineArtifacts.Where(a => a.Id == artifact.Id)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            if (db.Entry(artifact).State != EntityState.Detached)
                db.Entry(artifact).State = EntityState.Detached;
            return deleted > 0;
        }

        var current = await db.PipelineArtifacts.FirstOrDefaultAsync(a => a.Id == artifact.Id, ct).ConfigureAwait(false);
        if (current is null) return false;
        var completedDeliveries = await db.DependencyTrackOutboxItems
            .Where(item => item.PipelineArtifactId == artifact.Id && item.CompletedAt != null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var delivery in completedDeliveries) delivery.PipelineArtifactId = null;
        db.PipelineArtifacts.Remove(current);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public Task<string?> FindNewestReleaseVersionAsync(int artifactId, CancellationToken ct = default) =>
        db.Releases.AsNoTracking()
            .Where(release => release.Artifacts.Any(artifact => artifact.Id == artifactId))
            .OrderByDescending(release => release.PublishedAt ?? release.DetectedAt)
            .Select(release => (string?)release.Version)
            .FirstOrDefaultAsync(ct);

    public async Task<int> GetNextBuildNumberAsync(int projectId, int pipelineId, CancellationToken ct = default)
    {
        var max = await db.PipelineArtifacts
            .Where(a => a.ProjectId == projectId && a.PipelineId == pipelineId)
            .MaxAsync(a => (int?)a.PipelineRunId, ct).ConfigureAwait(false);
        return (max ?? 0) + 1;
    }

    public async Task<long> GetProjectTotalSizeBytesAsync(int projectId, CancellationToken ct = default) =>
        await db.PipelineArtifacts
            .Where(a => a.ProjectId == projectId)
            .SumAsync(a => a.SizeBytes, ct).ConfigureAwait(false);

    public async Task<(int? DefaultDays, int? LatestDays)> GetProjectRetentionOverridesAsync(int projectId, CancellationToken ct = default)
    {
        var p = await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.ArtifactRetentionDays, p.ArtifactLatestRetentionDays })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return (p?.ArtifactRetentionDays, p?.ArtifactLatestRetentionDays);
    }

    public async Task<PipelineArtifact?> FindRunArtifactByNameAsync(int runId, string name, CancellationToken ct = default) =>
        await db.PipelineArtifacts
            .Where(a => a.PipelineRunId == runId && a.Name == name)
            .Include(a => a.Project)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public async Task<PipelineArtifact?> FindSuccessfulPipelineArtifactByCommitAsync(
        int projectId, string pipelineName, string commitHash, string name, CancellationToken ct = default) =>
        await db.PipelineArtifacts
            .Where(a => a.ProjectId == projectId
                        && a.Name == name
                        && a.Pipeline.Name == pipelineName
                        && a.PipelineRun.Status == PipelineStatus.Success
                        && a.PipelineRun.CommitHash == commitHash)
            .Include(a => a.Project)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public async Task<PipelineArtifact?> FindLatestSuccessfulPipelineArtifactAsync(
        int projectId, string pipelineName, string name, CancellationToken ct = default) =>
        await db.PipelineArtifacts
            .Where(a => a.ProjectId == projectId
                        && a.Name == name
                        && a.Pipeline.Name == pipelineName
                        && a.PipelineRun.Status == PipelineStatus.Success)
            .Include(a => a.Project)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public async Task<PipelineArtifact?> FindReleaseArtifactAsync(
        int projectId, string releaseSelector, string? artifactName = null, CancellationToken ct = default)
        => (await FindReleaseArtifactSelectionAsync(
            projectId, releaseSelector, artifactName, ct).ConfigureAwait(false))?.Artifact;

    public async Task<ReleaseArtifactSelection?> FindReleaseArtifactSelectionAsync(
        int projectId, string releaseSelector, string? artifactName = null, CancellationToken ct = default)
    {
        // Resolve the release first (id / version / "latest"), then select the requested linked
        // artifact. Without an explicit name, retain the legacy newest-linked-artifact behavior.
        var releases = db.Releases.Where(r => r.ProjectId == projectId);
        Release? release;
        if (string.Equals(releaseSelector, "current-deployed", StringComparison.OrdinalIgnoreCase))
            release = await releases
                .Where(r => r.Status == ReleaseStatus.Deployed)
                .Where(r => r.Artifacts.Any(a => artifactName == null || a.Name == artifactName))
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        else if (string.Equals(releaseSelector, "latest-published", StringComparison.OrdinalIgnoreCase))
            release = await releases
                .Where(r => r.Status == ReleaseStatus.Published
                            || r.Status == ReleaseStatus.Deployed
                            || r.Status == ReleaseStatus.Superseded)
                // Imported/tag-only releases legitimately have no retained payload. "latest-published"
                // means the newest rollback-capable release, not merely the newest metadata row.
                .Where(r => r.Artifacts.Any(a => artifactName == null || a.Name == artifactName))
                .OrderByDescending(r => r.PublishedAt ?? r.DetectedAt)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        else if (string.Equals(releaseSelector, "latest", StringComparison.OrdinalIgnoreCase))
            release = await releases.OrderByDescending(r => r.DetectedAt).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        else if (int.TryParse(releaseSelector, out var releaseId))
            release = await releases.FirstOrDefaultAsync(r => r.Id == releaseId, ct).ConfigureAwait(false);
        else
            release = await releases.Where(r => r.Version == releaseSelector)
                .OrderByDescending(r => r.DetectedAt).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (release is null) return null;

        var artifact = await db.PipelineArtifacts
            .Where(a => a.Releases.Any(r => r.Id == release.Id)
                        && (artifactName == null || a.Name == artifactName))
            .Include(a => a.Project)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return artifact is null ? null : new ReleaseArtifactSelection(artifact, release.Id);
    }

    public async Task<bool> HasDeployedRollbackContractReleaseAsync(
        int projectId, CancellationToken ct = default) =>
        await db.Releases
            .AsNoTracking()
            .Where(release => release.ProjectId == projectId
                              && release.Status == ReleaseStatus.Deployed
                              && release.Artifacts.Any())
            .Where(ProducedByRollbackContractPipeline)
            .AnyAsync(ct)
            .ConfigureAwait(false);

    public async Task<bool> HasPublishedRollbackContractReleaseAsync(int projectId, CancellationToken ct = default) =>
        await db.Releases
            .AsNoTracking()
            .Where(release => release.ProjectId == projectId
                               && (release.Status == ReleaseStatus.Published
                                   || release.Status == ReleaseStatus.Deployed
                                   || release.Status == ReleaseStatus.Superseded)
                               // Metadata alone is not a rollback contract: the retained bytes must
                               // still exist and be linked to the release.
                               && release.Artifacts.Any())
            .Where(ProducedByRollbackContractPipeline)
            .AnyAsync(ct)
            .ConfigureAwait(false);

    public async Task<PipelineArtifact?> FindPreviousPublishedReleaseArtifactAsync(
        int projectId, string currentCommitHash, string? artifactName = null, CancellationToken ct = default) =>
        await FindPreviousReleaseArtifactAsync(
            projectId, currentCommitHash, artifactName, PublishedReleaseStatuses, ct).ConfigureAwait(false);

    public async Task<PipelineArtifact?> FindPreviousDeployedReleaseArtifactAsync(
        int projectId, string currentCommitHash, string? artifactName = null, CancellationToken ct = default) =>
        await FindPreviousReleaseArtifactAsync(
            projectId, currentCommitHash, artifactName, DeployedReleaseStatuses, ct).ConfigureAwait(false);

    public async Task<bool> RequiresPreviousPublishedArtifactAsync(
        int projectId, string currentCommitHash, string? artifactName, CancellationToken ct = default) =>
        await RequiresPreviousArtifactAsync(
            projectId, currentCommitHash, artifactName, PublishedReleaseStatuses, ct).ConfigureAwait(false);

    public async Task<bool> RequiresPreviousDeployedArtifactAsync(
        int projectId, string currentCommitHash, string? artifactName, CancellationToken ct = default) =>
        await RequiresPreviousArtifactAsync(
            projectId, currentCommitHash, artifactName, DeployedReleaseStatuses, ct).ConfigureAwait(false);

    private async Task<PipelineArtifact?> FindPreviousReleaseArtifactAsync(
        int projectId,
        string currentCommitHash,
        string? artifactName,
        ReleaseStatus[] statuses,
        CancellationToken ct)
    {
        var release = await db.Releases
            .Where(candidate => candidate.ProjectId == projectId
                                && statuses.Contains(candidate.Status)
                                && candidate.Artifacts.Any(artifact =>
                                    artifact.PipelineRun.CommitHash != currentCommitHash))
            .OrderByDescending(candidate => candidate.PublishedAt ?? candidate.DetectedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (release is null)
            return null;

        return await db.PipelineArtifacts
            .Where(artifact => artifact.Releases.Any(candidate => candidate.Id == release.Id)
                               && artifact.PipelineRun.CommitHash != currentCommitHash
                               && (artifactName == null || artifact.Name == artifactName))
            .Include(artifact => artifact.Project)
            .OrderByDescending(artifact => artifact.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task<bool> RequiresPreviousArtifactAsync(
        int projectId,
        string currentCommitHash,
        string? artifactName,
        ReleaseStatus[] statuses,
        CancellationToken ct)
    {
        var producer = await db.Releases
            .AsNoTracking()
            .Where(release => release.ProjectId == projectId
                              && statuses.Contains(release.Status)
                              && release.Artifacts.Any(artifact =>
                                    artifact.PipelineRun.CommitHash != currentCommitHash))
            .Where(ProducedByRollbackContractPipeline)
            .OrderByDescending(release => release.PublishedAt ?? release.DetectedAt)
            .Select(release => new
            {
                release.PipelineRun!.Pipeline.Name,
                release.PipelineRun.Pipeline.TemplateReferenceName
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (producer is null)
            return false;

        // A fast release carries the application payload only, whatever the project named it.
        var fastRelease = string.Equals(producer.Name, LegacyFastReleasePipeline, StringComparison.Ordinal)
            || DeliveryTemplateNames.Is(producer.TemplateReferenceName, DeliveryTemplateNames.ReleaseFast);
        return !fastRelease
               || string.Equals(artifactName, "ApplicationPayload-artifacts", StringComparison.Ordinal);
    }

    private const string LegacyFastReleasePipeline = "aetheus-release-fast";

    /// <summary>Recognize rollback-contract publishers by canonical self-deploy pipeline names or
    /// the shipped candidate and fast-release templates they extend.</summary>
    private static readonly Expression<Func<Release, bool>> ProducedByRollbackContractPipeline = release =>
        release.PipelineRun != null
        && (release.PipelineRun.Pipeline.Name == "aetheus-candidate"
            || release.PipelineRun.Pipeline.Name == "aetheus-release-with-rollback"
            || release.PipelineRun.Pipeline.Name == LegacyFastReleasePipeline
            || release.PipelineRun.Pipeline.TemplateReferenceName != null
                && (release.PipelineRun.Pipeline.TemplateReferenceName.ToLower() == DeliveryTemplateNames.Candidate
                    || release.PipelineRun.Pipeline.TemplateReferenceName.ToLower() == DeliveryTemplateNames.ReleaseFast));

    public async Task<int?> GetServerOrganizationIdAsync(int serverId, CancellationToken ct = default) =>
        await db.Servers
            .Where(s => s.Id == serverId)
            .Select(s => (int?)s.OrganizationId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    // Read shared entities locally to avoid module dependencies on Pipelines and Releases while
    // leaving their write ownership intact. Keep query shapes aligned with their canonical
    // counterparts.

    /// <summary>
    /// Pipeline and project a run belongs to. Canonical shape:
    /// <c>PipelineRunService.GetRunPipelineContextAsync</c>, which reads the same two columns through
    /// a full run DTO this module has no use for.
    /// </summary>
    public async Task<(int PipelineId, int? ProjectId)?> GetRunPipelineContextAsync(
        int runId, CancellationToken ct = default)
    {
        // Projected into an anonymous type, not a ValueTuple: EF Core translates a tuple projection
        // into a PostgreSQL record, which Npgsql refuses to read back ("not supported for fields
        // having DataTypeName 'record'"). That threw on every artifact upload. The tuple is composed
        // here instead, so the public shape is unchanged.
        var context = await db.PipelineRuns.AsNoTracking()
            .Where(run => run.Id == runId)
            .Select(run => new
            {
                run.PipelineId,
                run.Pipeline.ProjectId,
                run.Pipeline.EnvironmentId,
                run.Pipeline.ProjectServerId
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (context is null) return null;
        // BACK-009: an Environment- or ProjectServer-owned pipeline has no direct ProjectId; its
        // artifacts still belong to a project, which carries their quota and their retention.
        var projectId = await ResolveOwningProjectIdAsync(
            context.ProjectId, context.EnvironmentId, context.ProjectServerId, ct).ConfigureAwait(false);
        return (context.PipelineId, projectId);
    }

    /// <summary>
    /// Owning project of an artifact, following the pipeline's exactly-one-owner triple. Canonical
    /// shape: <c>PipelineServerResolver.GetPipelineProjectIdAsync</c>, which answers the same
    /// question from a loaded <c>Pipeline</c> this module does not hold.
    /// </summary>
    public async Task<int?> GetArtifactOwningProjectIdAsync(int artifactId, CancellationToken ct = default)
    {
        // One round trip: the three owner columns are read together and resolved below, rather than
        // probing Environments and ProjectServers in sequence on every authorization.
        var owner = await db.PipelineArtifacts.AsNoTracking()
            .Where(artifact => artifact.Id == artifactId)
            .Select(artifact => new
            {
                artifact.Pipeline.ProjectId,
                artifact.Pipeline.EnvironmentId,
                artifact.Pipeline.ProjectServerId
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (owner is null) return null;
        return await ResolveOwningProjectIdAsync(
            owner.ProjectId, owner.EnvironmentId, owner.ProjectServerId, ct).ConfigureAwait(false);
    }

    private async Task<int?> ResolveOwningProjectIdAsync(
        int? projectId, int? environmentId, int? projectServerId, CancellationToken ct)
    {
        if (projectId is { } direct) return direct;

        if (environmentId is { } environment)
            return await db.Environments.AsNoTracking()
                .Where(item => item.Id == environment)
                .Select(item => item.ProjectId)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (projectServerId is { } projectServer)
            return await db.ProjectServers.AsNoTracking()
                .Where(item => item.Id == projectServer)
                .Select(item => (int?)item.ProjectId)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        // A legacy pipeline with all three owners null. No project can be named, so no permission
        // can be checked, and the caller refuses.
        return null;
    }

    public async Task<int?> GetReleaseProjectIdAsync(int releaseId, CancellationToken ct = default) =>
        await db.Releases.AsNoTracking()
            .Where(release => release.Id == releaseId)
            .Select(release => (int?)release.ProjectId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Whether a server ran any step of a run - the RBAC question an agent upload has to answer.
    /// Canonical shape: <c>PipelineCoreRepository.IsServerAssignedToRunAsync</c>. The semantics that
    /// matter: ANY step, so a run fanned out over several servers authorizes each of them.
    /// </summary>
    public async Task<bool> IsServerAssignedToRunAsync(int runId, int serverId, CancellationToken ct = default) =>
        await db.PipelineStepRuns.AsNoTracking()
            .AnyAsync(step => step.PipelineRunId == runId && step.ServerId == serverId, ct).ConfigureAwait(false);

    /// <summary>
    /// Release produced by a run, if any. Canonical shape:
    /// <c>ReleaseRepository.FindByPipelineRunIdAsync</c>.
    /// </summary>
    public async Task<Release?> FindReleaseForRunAsync(int pipelineRunId, CancellationToken ct = default) =>
        await db.Releases
            .AsNoTracking()
            .FirstOrDefaultAsync(release => release.PipelineRunId == pipelineRunId, ct).ConfigureAwait(false);

    /// <summary>
    /// Deployed releases of a project, which retention must not delete the artifacts of. Canonical
    /// shape: <c>ReleaseRepository.GetDeployedProjectReleasesAsync</c>.
    /// </summary>
    public async Task<List<Release>> GetDeployedProjectReleasesAsync(int projectId, CancellationToken ct = default) =>
        await db.Releases
            .Where(release => release.ProjectId == projectId && release.Status == ReleaseStatus.Deployed)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
}
