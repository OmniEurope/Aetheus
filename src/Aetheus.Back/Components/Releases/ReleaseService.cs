// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppBackups;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.GitGraph;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Releases;

public class ReleaseService(
    IReleaseRepository repo,
    IProjectService projectService,
    IGitCliService gitService,
    IPipelineLauncher pipelineLauncher,
    IPipelineService pipelineService,
    IHubContext<ReleaseHub> releaseHub,
    IGitGraphRecorder gitGraph,
    TimeProvider timeProvider,
    IArtifactStorageService? artifactStorage = null,
    IBackupRepository? backupRepo = null,
    IAuditService? audit = null,
    IArtifactRepository? artifactRepo = null,
    IArtifactRetentionService? artifactRetention = null,
    IPipelineRepository? pipelineRepo = null,
    IDbTransactionScope? transaction = null) : IReleaseService
{
    public async Task<PaginatedResult<ReleaseDto>> GetReleasesAsync(int? projectId, PaginationRequest request, List<int>? accessibleIds = null, CancellationToken ct = default, bool deployableOnly = false)
    {
        var (page, pageSize) = request.Normalize();
        var (columnFilters, releaseIds) = await ListFilters.ResolveAsync(request.Filters, projectId, accessibleIds, null, ct).ConfigureAwait(false);
        var (items, totalCount) = await repo.GetReleasesPagedAsync(
            request.Search, projectId, page, pageSize, accessibleIds, ct,
            request.SortBy, request.SortDescending, deployableOnly, columnFilters, releaseIds).ConfigureAwait(false);

        return new PaginatedResult<ReleaseDto>
        {
            Items = await MapToDtosAsync(items, ct).ConfigureAwait(false),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<ReleaseDto?> GetReleaseAsync(int id, CancellationToken ct = default)
    {
        var release = await repo.FindReleaseAsync(id, ct).ConfigureAwait(false);
        return release is null ? null : await MapToDtoAsync(release, ct).ConfigureAwait(false);
    }

    public async Task<ReleaseRollbackPreviewDto> GetRollbackPreviewAsync(int id, CancellationToken ct = default)
    {
        var release = await repo.FindReleaseAsync(id, ct).ConfigureAwait(false);
        if (release is null) throw new NotFoundException("Release not found");
        var target = await repo.FindPreviousPublishedWithArtifactAsync(release, ct).ConfigureAwait(false);
        if (target is null)
            return new ReleaseRollbackPreviewDto { Reason = "No previous release with a retained artifact is available." };

        var artifact = target.Artifacts.OrderByDescending(a => a.CreatedAt).First();
        await using var probe = artifactStorage?.OpenArtifact(artifact.FilePath);
        return probe is null
            ? new ReleaseRollbackPreviewDto { Reason = "The previous release artifact is no longer available in storage." }
            : new ReleaseRollbackPreviewDto
            {
                CanRollback = true,
                TargetVersion = target.Version,
                TargetPublishedAt = target.PublishedAt
            };
    }

    public async Task<List<ReleaseDto>> GetReleasesByRunAsync(int pipelineRunId, CancellationToken ct = default)
    {
        var releases = await repo.GetByPipelineRunIdAsync(pipelineRunId, ct).ConfigureAwait(false);
        return await MapToDtosAsync(releases, ct).ConfigureAwait(false);
    }

    public async Task<List<ReleaseDto>> SyncReleasesAsync(int projectId, CancellationToken ct = default)
    {
        await SyncReleaseRowsAsync(projectId, ct).ConfigureAwait(false);
        var releases = await repo.GetProjectReleasesAsync(projectId, ct).ConfigureAwait(false);
        return await MapToDtosAsync(releases, ct).ConfigureAwait(false);
    }

    private async Task SyncReleaseRowsAsync(int projectId, CancellationToken ct)
    {
        var project = await projectService.GetProjectDetailAsync(projectId, ct).ConfigureAwait(false);
        if (project is null) throw new NotFoundException("Project not found");
        if (string.IsNullOrWhiteSpace(project.RepositoryUrl))
            throw new BadRequestException("Project has no repository URL");

        var branches = await gitService.ListReleaseBranchesAsync(project.RepositoryUrl, ct).ConfigureAwait(false);

        foreach (var (branchName, version) in branches)
        {
            var existing = await repo.FindByVersionAsync(projectId, version, ct).ConfigureAwait(false);
            if (existing is not null) continue;

            await repo.AddReleaseAsync(new Release
            {
                ProjectId = projectId,
                Version = version,
                BranchName = branchName,
                Status = ReleaseStatus.Detected,
                DetectedAt = timeProvider.GetUtcNow().UtcDateTime
            }, ct).ConfigureAwait(false);
        }
    }

    public async Task<ReleaseDto> TriggerReleaseBuildAsync(
        int releaseId, TriggerReleaseBuildRequest request, PipelineRunPreparation authorizedRun, CancellationToken ct = default)
    {
        EnsureAuthorizedRunMatches(authorizedRun, request.PipelineId);
        var release = await repo.FindReleaseAsync(releaseId, ct).ConfigureAwait(false);
        if (release is null) throw new NotFoundException("Release not found");

        // F-12: ensure the requested pipeline belongs to the same project as the release. Resolved
        // through the Pipelines service contract (not its repository) - cross-module deps via interfaces.
        var pipeline = await pipelineService.GetPipelineAsync(request.PipelineId, ct).ConfigureAwait(false);
        if (pipeline is null) throw new NotFoundException("Pipeline not found");
        if (pipeline.ProjectId != release.ProjectId)
            throw new BadRequestException("Pipeline does not belong to the release's project.");

        release.Status = ReleaseStatus.Building;

        var runResult = await pipelineLauncher.TriggerPreparedRunAsync(authorizedRun, ct: ct).ConfigureAwait(false);
        if (runResult is not null)
            release.PipelineRunId = runResult.Id;

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        return await MapToDtoAsync(release, ct).ConfigureAwait(false);
    }

    public async Task<PipelineRunDto> RelaunchFromCommitAsync(
        int releaseId, TriggerReleaseBuildRequest request, PipelineRunPreparation authorizedRun, CancellationToken ct = default)
    {
        EnsureAuthorizedRunMatches(authorizedRun, request.PipelineId);
        var release = await repo.FindReleaseAsync(releaseId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Release not found");
        if (!ReleaseRelaunch.CanRelaunch(release.Status, release.CommitHash))
            throw new BadRequestException("Only an archived release with a full commit hash can be relaunched from its commit.");
        // The authorized snapshot must be the one pinned to this release's commit, never a branch head.
        if (!string.Equals(authorizedRun.CommitHash, release.CommitHash, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("The authorized run is not pinned to the release's commit.");
        var pipeline = await pipelineService.GetPipelineAsync(request.PipelineId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Pipeline not found");
        if (pipeline.ProjectId != release.ProjectId)
            throw new BadRequestException("Pipeline does not belong to the release's project.");

        // The release stays archived: the run, if it publishes this version again, makes it Published
        // (CreateReleaseFromPipelineAsync), and a failed run leaves it as it was, its logs kept.
        var run = await pipelineLauncher.TriggerPreparedRunAsync(authorizedRun, ct: ct).ConfigureAwait(false)
            ?? throw new BadRequestException("The pipeline run could not be started.");
        if (audit is not null) await audit.LogAsync("ReleaseRelaunchedFromCommit", "Release", release.Id,
            $"version={release.Version}; commit={release.CommitHash}; pipeline={pipeline.Id}; run={run.Id}", ct).ConfigureAwait(false);
        return run;
    }

    public async Task<ReleaseRollbackDto> RollbackReleaseAsync(
        int releaseId, RollbackReleaseRequest request, PipelineRunPreparation authorizedRun, CancellationToken ct = default)
    {
        EnsureAuthorizedRunMatches(authorizedRun, request.PipelineId);
        var release = await repo.FindReleaseAsync(releaseId, ct).ConfigureAwait(false);
        if (release is null) throw new NotFoundException("Release not found");
        var target = await repo.FindPreviousPublishedWithArtifactAsync(release, ct).ConfigureAwait(false);
        if (target is null)
            throw new BadRequestException("No previous release with a retained artifact is available for rollback.");
        await EnsureArtifactAvailableAsync(target).ConfigureAwait(false);
        var (pipeline, definition) = await LoadRollbackPipelineAsync(
            request.PipelineId, release.ProjectId, ct).ConfigureAwait(false);
        var backup = await ResolveRollbackBackupAsync(
            request, release.ProjectId, definition, ct).ConfigureAwait(false);
        var rollback = new ReleaseRollback
        {
            SourceReleaseId = release.Id,
            TargetReleaseId = target.Id,
            PipelineId = pipeline.Id,
            BackupRunId = backup?.Id,
            RestoreDatabase = request.RestoreDatabase,
            RequestedAt = timeProvider.GetUtcNow().UtcDateTime
        };
        await repo.AddRollbackAsync(rollback, ct).ConfigureAwait(false);
        var variables = CreateRollbackVariables(release, target, rollback, backup);
        var run = await pipelineLauncher.TriggerPreparedRunAsync(authorizedRun, variables, ct: ct).ConfigureAwait(false);
        await CompleteRollbackLaunchAsync(
            release, target, rollback, run, request.RestoreDatabase, ct).ConfigureAwait(false);
        return MapRollback(rollback);
    }

    private static void EnsureAuthorizedRunMatches(PipelineRunPreparation authorizedRun, int pipelineId)
    {
        ArgumentNullException.ThrowIfNull(authorizedRun);
        if (authorizedRun.PipelineId != pipelineId)
            throw new BadRequestException("The authorized run does not belong to the requested pipeline.");
    }

    private async Task EnsureArtifactAvailableAsync(Release target)
    {
        var artifact = target.Artifacts.OrderByDescending(item => item.CreatedAt).First();
        await using var probe = artifactStorage?.OpenArtifact(artifact.FilePath);
        if (probe is null)
            throw new BadRequestException(
                "The previous release artifact is no longer available in storage; rollback was not started.");
    }

    private async Task<(PipelineDto Pipeline, PipelineYamlDefinition Definition)> LoadRollbackPipelineAsync(
        int pipelineId,
        int projectId,
        CancellationToken ct)
    {
        var pipeline = await pipelineService.GetPipelineAsync(pipelineId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Pipeline not found");
        if (pipeline.ProjectId != projectId)
            throw new BadRequestException("Rollback pipeline does not belong to the release project.");
        var definition = pipelineService.ValidateYaml(pipeline.YamlDefinition)
            ?? throw new BadRequestException("Rollback pipeline YAML is invalid.");
        var hasRollbackDeploy = definition.Stages.SelectMany(stage => stage.Steps)
            .Any(step => string.Equals(step.Type, "deploy", StringComparison.OrdinalIgnoreCase)
                && IsRollbackReleaseSelector(step.Release));
        if (!hasRollbackDeploy)
            throw new BadRequestException(
                "Rollback pipeline must contain a type: deploy step using $(RELEASE) or $(ROLLBACK_RELEASE).");
        return (pipeline, definition);
    }

    private async Task<BackupRun?> ResolveRollbackBackupAsync(
        RollbackReleaseRequest request,
        int projectId,
        PipelineYamlDefinition definition,
        CancellationToken ct)
    {
        if (!request.RestoreDatabase) return null;
        if (request.BackupRunId is null)
            throw new BadRequestException("A verified backup is required when database restore is requested.");
        var backup = backupRepo is null
            ? null
            : await backupRepo.FindRunWithPolicyAsync(request.BackupRunId.Value, ct).ConfigureAwait(false);
        if (!IsVerifiedProjectBackup(backup, projectId))
            throw new BadRequestException("The selected backup is not a verified successful backup for this project.");
        var hasRollbackRestore = definition.Stages.SelectMany(stage => stage.Steps)
            .Any(step => string.Equals(step.Type, "restore-backup", StringComparison.OrdinalIgnoreCase)
                && IsRollbackBackupSelector(step.BackupRun));
        if (!hasRollbackRestore)
            throw new BadRequestException(
                "Rollback pipeline must contain a type: restore-backup step using $(AETHEUS_ROLLBACK_BACKUP_RUN_ID).");
        return backup;
    }

    private static bool IsVerifiedProjectBackup(BackupRun? backup, int projectId) =>
        backup?.BackupPolicy.ProjectId == projectId
        && backup.Status == BackupRunStatus.Succeeded
        && backup.RestoreCheckStatus == RestoreCheckStatus.Verified
        && !string.IsNullOrWhiteSpace(backup.ArchivePath);

    private static Dictionary<string, string> CreateRollbackVariables(
        Release release,
        Release target,
        ReleaseRollback rollback,
        BackupRun? backup)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RELEASE"] = target.Id.ToString(),
            ["ROLLBACK_RELEASE"] = target.Id.ToString(),
            ["AETHEUS_ROLLBACK_ID"] = rollback.Id.ToString(),
            ["AETHEUS_ROLLBACK_SOURCE_RELEASE_ID"] = release.Id.ToString(),
            ["AETHEUS_ROLLBACK_RESTORE_DATABASE"] = rollback.RestoreDatabase ? "true" : "false"
        };
        if (backup is not null)
            variables["AETHEUS_ROLLBACK_BACKUP_RUN_ID"] = backup.Id.ToString();
        return variables;
    }

    private async Task CompleteRollbackLaunchAsync(
        Release release,
        Release target,
        ReleaseRollback rollback,
        PipelineRunDto? run,
        bool restoreDatabase,
        CancellationToken ct)
    {
        if (run is null)
        {
            rollback.Status = RollbackStatus.Failed;
            rollback.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
            rollback.FailureReason = "Rollback pipeline could not be created.";
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
            throw new BadRequestException(rollback.FailureReason);
        }
        rollback.PipelineRunId = run.Id;
        if (run.Status.IsTerminal())
        {
            rollback.Status = RollbackStatus.Failed;
            rollback.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
            rollback.FailureReason = BuildRollbackFailureReason(run.Status);
        }
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        if (audit is null) return;
        await audit.LogAsync("RollbackRequested", "Release", release.Id,
            $"from={release.Version}; to={target.Version}; run={run.Id}; restoreDatabase={restoreDatabase}", ct).ConfigureAwait(false);
        if (rollback.Status == RollbackStatus.Failed)
            await audit.LogAsync("RollbackFailed", "Release", release.Id,
                $"from={release.Version}; to={target.Version}; run={run.Id}; reason={rollback.FailureReason}", ct).ConfigureAwait(false);
    }

    public async Task<ReleaseDto> PromoteReleaseAsync(int releaseId, CancellationToken ct = default)
    {
        var release = await repo.FindReleaseAsync(releaseId, ct).ConfigureAwait(false);
        if (release is null) throw new NotFoundException("Release not found");

        release.Status = ReleaseStatus.Promoted;
        release.PromotedAt = timeProvider.GetUtcNow().UtcDateTime;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        var dto = await MapToDtoAsync(release, ct).ConfigureAwait(false);
        await releaseHub.Clients.Groups([HubGroups.AllReleases, HubGroups.ProjectReleases(dto.ProjectId)])
            .SendAsync("ReleaseStatusChanged", dto, ct).ConfigureAwait(false);
        return dto;
    }

    public async Task<ReleaseDto> CreateReleaseFromPipelineAsync(
        int projectId, int pipelineRunId, string version, string? changelog,
        string? commitHash = null, string? tagName = null, string? branchName = null,
        int? artifactPipelineRunId = null, bool deployed = false, CancellationToken ct = default)
    {
        var retainedArtifactRunId = artifactPipelineRunId ?? pipelineRunId;
        IReadOnlyList<PipelineArtifact>? validatedArtifacts = null;
        if (retainedArtifactRunId != pipelineRunId)
            validatedArtifacts = await ValidateArtifactSourceRunAsync(
                projectId, retainedArtifactRunId, commitHash, deployed, ct).ConfigureAwait(false);

        if (artifactPipelineRunId.HasValue && artifactRetention is null)
            throw new BadRequestException("Artifact retention is unavailable; refusing to publish a release without a retained payload.");

        // F1: hydrate the candidate's sealed assurance verdict from the SAME run's own step outputs
        // (the AssuranceSeal stage publishes CANDIDATE_ASSURANCE_GRADE/CANDIDATE_DEPLOYABLE/
        // CANDIDATE_BLOCKING_TESTS before the Candidate stage below it calls this endpoint), so the
        // release row carries it without a client round-trip. Null on any pipeline that publishes no
        // such verdict (e.g. a legacy release).
        var assuranceVerdict = await ReleaseAssuranceVerdictReader.ReadAsync(pipelineRepo, pipelineRunId, ct).ConfigureAwait(false);

        async Task<(ReleaseDto Dto, bool Created)> PersistAsync()
        {
            var deployedReleases = deployed
                ? await repo.GetDeployedProjectReleasesAsync(projectId, ct).ConfigureAwait(false)
                : [];
            foreach (var deployedRelease in deployedReleases)
                deployedRelease.Status = ReleaseStatus.Superseded;
            if (deployedReleases.Count > 0)
                await repo.SaveChangesAsync(ct).ConfigureAwait(false);

            var releaseStatus = deployed ? ReleaseStatus.Deployed : ReleaseStatus.Published;
            var existingByVersion = await repo.FindByVersionAsync(projectId, version, ct).ConfigureAwait(false);
            if (ReleaseRunOutcome.IsLiveBeforeThisRun(existingByVersion, deployed, pipelineRunId))
                return (await MapToDtoAsync(existingByVersion, ct).ConfigureAwait(false), false);
            if (existingByVersion is not null)
            {
                ReleaseRunOutcome.RecordSourceIdentity(existingByVersion, deployed, commitHash, tagName, branchName);
                existingByVersion.Status = releaseStatus;
                existingByVersion.PublishedAt = timeProvider.GetUtcNow().UtcDateTime;
                existingByVersion.ArchivedAt = null;
                existingByVersion.PipelineRunId = pipelineRunId;
                existingByVersion.Changelog = changelog;
                if (assuranceVerdict is not null)
                {
                    existingByVersion.AssuranceGrade = assuranceVerdict.Grade;
                    existingByVersion.Deployable = assuranceVerdict.Deployable;
                    existingByVersion.BlockingTestCount = assuranceVerdict.BlockingTestCount;
                }
                await repo.SaveChangesAsync(ct).ConfigureAwait(false);
                await LinkRunArtifactsToReleaseAsync(
                    existingByVersion, retainedArtifactRunId, validatedArtifacts, ct).ConfigureAwait(false);
                if (retainedArtifactRunId != pipelineRunId)
                    await LinkRunArtifactsToReleaseAsync(
                        existingByVersion, pipelineRunId, null, ct).ConfigureAwait(false);
                await gitGraph.RecordReleaseContextAsync(existingByVersion, ct).ConfigureAwait(false);
                return (await MapToDtoAsync(existingByVersion, ct).ConfigureAwait(false), false);
            }

            var maxBuildNumber = await repo.GetMaxBuildNumberAsync(projectId, ct).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var release = new Release
            {
                // The release comes to be here: never leaving DetectedAt at its default, which every
                // screen reading it rendered as 01/01/0001.
                DetectedAt = now,
                ProjectId = projectId,
                Version = version,
                BranchName = branchName ?? string.Empty,
                Status = releaseStatus,
                PipelineRunId = pipelineRunId,
                Changelog = changelog,
                CommitHash = commitHash,
                TagName = tagName ?? $"v{version}",
                BuildNumber = maxBuildNumber + 1,
                PublishedAt = now,
                AssuranceGrade = assuranceVerdict?.Grade,
                Deployable = assuranceVerdict?.Deployable,
                BlockingTestCount = assuranceVerdict?.BlockingTestCount
            };

            await repo.AddReleaseAsync(release, ct).ConfigureAwait(false);
            await LinkRunArtifactsToReleaseAsync(
                release, retainedArtifactRunId, validatedArtifacts, ct).ConfigureAwait(false);
            if (retainedArtifactRunId != pipelineRunId)
                await LinkRunArtifactsToReleaseAsync(
                    release, pipelineRunId, null, ct).ConfigureAwait(false);
            await gitGraph.RecordReleaseContextAsync(release, ct).ConfigureAwait(false);
            return (await MapToDtoAsync(release, ct).ConfigureAwait(false), true);
        }

        (ReleaseDto Dto, bool Created) persisted;
        if (transaction?.IsRelational == true)
        {
            await transaction.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                persisted = await PersistAsync().ConfigureAwait(false);
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
            persisted = await PersistAsync().ConfigureAwait(false);
        }

        if (persisted.Created)
            await releaseHub.Clients.Groups([HubGroups.AllReleases, HubGroups.ProjectReleases(projectId)])
                .SendAsync("ReleaseCreated", persisted.Dto, ct).ConfigureAwait(false);
        return persisted.Dto;
    }

    public async Task HandleWebhookAsync(WebhookPayload payload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload.RepositoryUrl)) return;

        var projects = await projectService.GetProjectsWithRepoUrlAsync(ct).ConfigureAwait(false);
        var normalizedPayloadUrl = RepositoryUrlNormalizer.Normalize(payload.RepositoryUrl);
        var matchingProject = projects.FirstOrDefault(p =>
            RepositoryUrlNormalizer.Normalize(p.RepositoryUrl!).Equals(normalizedPayloadUrl, StringComparison.OrdinalIgnoreCase));

        if (matchingProject is null) return;

        // The webhook only needs to persist newly detected rows. Avoid materializing the project's
        // complete release history and its linked artifacts when nobody consumes the returned list.
        await SyncReleaseRowsAsync(matchingProject.Id, ct).ConfigureAwait(false);
    }

    public bool ValidateWebhookSignature(string? signature, string secret, string rawBody) =>
        WebhookSignatureValidator.Validate(signature, secret, rawBody);

    public async Task NotifyPipelineRunCompletedAsync(int pipelineRunId, PipelineStatus status, CancellationToken ct = default)
    {
        var rollback = await repo.FindRollbackByPipelineRunIdAsync(pipelineRunId, ct).ConfigureAwait(false);
        if (rollback is not null && rollback.Status == RollbackStatus.Pending)
        {
            rollback.Status = RollbackStatus.Failed;
            rollback.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
            rollback.FailureReason = BuildRollbackFailureReason(status);
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
            if (audit is not null) await audit.LogAsync("RollbackFailed", "Release", rollback.SourceReleaseId,
                $"from={rollback.SourceRelease.Version}; to={rollback.TargetRelease.Version}; run={pipelineRunId}; reason={rollback.FailureReason}", ct).ConfigureAwait(false);
            return;
        }

        var release = await repo.FindByPipelineRunIdAsync(pipelineRunId, ct).ConfigureAwait(false);
        if (release is null || !ReleaseRunOutcome.Apply(release, status, timeProvider.GetUtcNow().UtcDateTime)) return;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task NotifyRollbackDeploymentSucceededAsync(int rollbackId, CancellationToken ct = default)
    {
        var rollback = await repo.FindRollbackAsync(rollbackId, ct).ConfigureAwait(false);
        if (rollback is null || rollback.Status != RollbackStatus.Pending) return;

        rollback.Status = RollbackStatus.Succeeded;
        rollback.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
        rollback.SourceRelease.Status = ReleaseStatus.RolledBack;
        rollback.SourceRelease.RolledBackAt = rollback.CompletedAt;
        rollback.TargetRelease.Status = ReleaseStatus.Deployed;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        if (audit is not null) await audit.LogAsync("RollbackSucceeded", "Release", rollback.SourceReleaseId,
            $"from={rollback.SourceRelease.Version}; to={rollback.TargetRelease.Version}; run={rollback.PipelineRunId}; restoreDatabase={rollback.RestoreDatabase}", ct).ConfigureAwait(false);
        await BroadcastStatusAsync(rollback.SourceRelease, ct).ConfigureAwait(false);
        await BroadcastStatusAsync(rollback.TargetRelease, ct).ConfigureAwait(false);
    }

    private async Task BroadcastStatusAsync(Release release, CancellationToken ct)
    {
        var dto = await MapToDtoAsync(release, ct).ConfigureAwait(false);
        await releaseHub.Clients.Groups([HubGroups.AllReleases, HubGroups.ProjectReleases(dto.ProjectId)])
            .SendAsync("ReleaseStatusChanged", dto, ct).ConfigureAwait(false);
    }

    private static bool IsRollbackReleaseSelector(string? selector) =>
        string.Equals(selector, "$(RELEASE)", StringComparison.OrdinalIgnoreCase)
        || string.Equals(selector, "$(ROLLBACK_RELEASE)", StringComparison.OrdinalIgnoreCase);

    private static bool IsRollbackBackupSelector(string? selector) =>
        string.Equals(selector, "$(AETHEUS_ROLLBACK_BACKUP_RUN_ID)", StringComparison.OrdinalIgnoreCase);

    private static string BuildRollbackFailureReason(PipelineStatus status) =>
        status == PipelineStatus.Success
            ? "Rollback pipeline completed without a successful deployment task."
            : "Rollback pipeline failed before the deployment health gate succeeded.";

    /// <summary>A release that is NOT recorded as deployed may be built from a Rejected source run: that
    /// run executed every control to the end and sealed what it found (recette R1-80), so the candidate
    /// is published undeployed with its bad verdict, and aetheus-deploy-prod refuses it on the seal. A
    /// release recorded as deployed keeps the strict rule: its artifacts must come from a Success run.</summary>
    private async Task<IReadOnlyList<PipelineArtifact>> ValidateArtifactSourceRunAsync(
        int projectId, int artifactPipelineRunId, string? commitHash, bool deployed, CancellationToken ct)
    {
        if (pipelineRepo is null || artifactRepo is null)
            throw new BadRequestException("Artifact source validation is unavailable.");

        var sourceRun = await pipelineRepo.GetPipelineRunWithPipelineAsync(
            artifactPipelineRunId, ct).ConfigureAwait(false);
        var sourceProjectId = sourceRun?.Pipeline is null
            ? null
            : await pipelineRepo.GetPipelineProjectIdAsync(sourceRun.Pipeline, ct).ConfigureAwait(false);
        var sourceCompleted = sourceRun is { Status: PipelineStatus.Success }
            || (!deployed && sourceRun is { Status: PipelineStatus.Rejected });
        if (sourceRun is null
            || sourceProjectId != projectId
            || !sourceCompleted
            || string.IsNullOrWhiteSpace(commitHash)
            || !string.Equals(sourceRun.CommitHash, commitHash, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("The artifact source run is not a completed same-project run at the release commit (successful, or rejected for an undeployed release).");

        var artifacts = await artifactRepo.GetByRunAsync(artifactPipelineRunId, ct).ConfigureAwait(false);
        if (artifacts.Count == 0 || artifacts.Any(artifact => artifact.ProjectId != projectId))
            throw new BadRequestException("The artifact source run has no releasable artifact for this project.");
        return artifacts;
    }

    private async Task LinkRunArtifactsToReleaseAsync(
        Release release, int artifactPipelineRunId, IReadOnlyList<PipelineArtifact>? validatedArtifacts,
        CancellationToken ct)
    {
        if (artifactRepo is null || artifactRetention is null)
            return;

        var artifacts = validatedArtifacts
            ?? await artifactRepo.GetByRunAsync(artifactPipelineRunId, ct).ConfigureAwait(false);
        foreach (var artifact in artifacts)
            await artifactRetention.ApplyReleaseRetentionAsync(artifact, release.Id, ct).ConfigureAwait(false);
    }

    private static ReleaseRollbackDto MapRollback(ReleaseRollback rollback) => new()
    {
        Id = rollback.Id,
        SourceReleaseId = rollback.SourceReleaseId,
        TargetReleaseId = rollback.TargetReleaseId,
        PipelineRunId = rollback.PipelineRunId,
        BackupRunId = rollback.BackupRunId,
        RestoreDatabase = rollback.RestoreDatabase,
        Status = rollback.Status,
        RequestedAt = rollback.RequestedAt,
        CompletedAt = rollback.CompletedAt,
        FailureReason = rollback.FailureReason
    };

    private static ReleaseDto MapToDto(Release r) => new()
    {
        Id = r.Id,
        ProjectId = r.ProjectId,
        ProjectName = r.Project?.Name ?? string.Empty,
        Version = r.Version,
        BranchName = r.BranchName,
        Status = r.Status,
        DetectedAt = r.DetectedAt,
        PublishedAt = r.PublishedAt,
        PromotedAt = r.PromotedAt,
        RolledBackAt = r.RolledBackAt,
        ArchivedAt = r.ArchivedAt,
        PipelineRunId = r.PipelineRunId,
        Changelog = r.Changelog,
        BuildNumber = r.BuildNumber,
        CommitHash = r.CommitHash,
        TagName = r.TagName,
        AssuranceGrade = r.AssuranceGrade,
        Deployable = r.Deployable,
        BlockingTestCount = r.BlockingTestCount,
        RepositoryUrl = r.Project?.RepositoryUrl,
        Artifacts = r.Artifacts.Select(GitGraphMapper.ToLink).ToList(),
        Commits = r.Commits.Select(GitGraphMapper.ToLink).ToList(),
        Branches = r.Branches.Select(GitGraphMapper.ToLink).ToList()
    };

    /// <summary>
    /// Releases of every project a server is involved in.
    ///
    /// It lived in ServerService, which had to inject IPipelineRepository purely to enrich the result
    /// with each release's source pipeline - the one thing that made Servers depend on Pipelines.
    /// Here the enrichment is already what MapToDtosAsync does for every other release view.
    /// </summary>
    public async Task<List<ReleaseDto>> GetServerReleasesAsync(int serverId, CancellationToken ct = default)
    {
        var releases = await repo.GetReleasesForServerAsync(serverId, ct).ConfigureAwait(false);
        return await MapToDtosAsync(releases, ct).ConfigureAwait(false);
    }

    public async Task<PaginatedResult<ReleaseDto>> GetServerReleasesAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (page, pageSize) = request.Normalize();
        var (columnFilters, releaseIds) = await ListFilters.ResolveAsync(request.Filters, null, null, serverId, ct).ConfigureAwait(false);
        var (releases, total) = await repo.GetReleasesForServerPagedAsync(
            serverId, page, pageSize, ct, request.SortBy, request.SortDescending, columnFilters, releaseIds).ConfigureAwait(false);
        return new PaginatedResult<ReleaseDto>
        {
            Items = await MapToDtosAsync(releases, ct).ConfigureAwait(false),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    // The header filters and filter values of the releases list.
    private ReleaseListFilters ListFilters => new(repo, pipelineRepo);
    public Task<ReleaseFilterValuesDto> GetReleaseFilterValuesAsync(int? projectId, List<int>? accessibleIds, int? serverId, CancellationToken ct = default)
        => ListFilters.ValuesAsync(projectId, accessibleIds, serverId, ct);

    private async Task<ReleaseDto> MapToDtoAsync(Release release, CancellationToken ct)
        => (await MapToDtosAsync([release], ct).ConfigureAwait(false))[0];

    private async Task<List<ReleaseDto>> MapToDtosAsync(
        IReadOnlyCollection<Release> releases, CancellationToken ct)
    {
        var mapped = await ReleaseSourcePipelineEnricher.EnrichAsync(releases.Select(MapToDto).ToList(), pipelineRepo, ct).ConfigureAwait(false);
        return await ReleaseRedeployEnricher.EnrichAsync(mapped, repo, ct).ConfigureAwait(false);
    }
}
