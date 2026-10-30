// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;
using Aetheus.Back.Components.AppBackups;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The two steps that bring something back onto the runner before the run can use it: restoring
/// pipeline artifacts, and restoring an application backup.
/// </summary>
public interface IPipelineArtifactTaskFactory
{
    Task CreateRestoreArtifactsTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, CancellationToken ct);

    Task CreateBackupRestoreTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, CancellationToken ct);

    /// <summary>
    /// Resolves the artifact a step points at - same run, another pipeline's run, or a release.
    /// Public because the release step resolves the same thing and must not grow its own version of it.
    /// </summary>
    Task<(PipelineArtifact? Artifact, string? Error)> ResolveArtifactSourceAsync(
        int runId, Dictionary<string, string> variables, PipelineStepDefinition stepDef,
        CancellationToken ct);
}

/// <summary>
/// Resolves what a restore step needs and dispatches it. Unlike the other factories this one may also
/// legitimately COMPLETE a step without dispatching anything: an optional producer that never ran
/// leaves nothing to restore, and saying so is the honest outcome rather than a failure. What it
/// never does is advance or finalize the run.
/// </summary>
public sealed class PipelineArtifactTaskFactory(
    IPipelineRepository repo,
    IArtifactRepository artifactRepo,
    IEncryptionService encryption,
    TimeProvider timeProvider,
    IBackupRepository? backupRepo = null) : IPipelineArtifactTaskFactory
{
    public async Task CreateRestoreArtifactsTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var releaseSelector = SubstituteVariables(stepDef.Release ?? string.Empty, legVars).Trim();
        var (artifact, error) = await ResolveArtifactSourceAsync(runId, legVars, stepDef, ct).ConfigureAwait(false);
        if (artifact is null)
        {
            await HandleMissingRestoreArtifactAsync(
                runId, legServer, stepRun, stepDef, releaseSelector, error, now, ct).ConfigureAwait(false);
            return;
        }
        var targetDirectory = SubstituteVariables(stepDef.TargetDirectory ?? string.Empty, legVars).Trim();
        if (!PipelinePathValidation.IsSafeRelativeDirectory(targetDirectory))
        {
            await FailRestoreArtifactsAsync(
                runId, legServer, stepRun, "target_directory must be a safe relative path.", now, ct)
                .ConfigureAwait(false);
            return;
        }
        if (artifact.Sha256 is not { Length: 64 } artifactSha256
            || artifactSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            await FailRestoreArtifactsAsync(
                runId, legServer, stepRun,
                $"artifact '{artifact.Name}' has no valid authoritative SHA-256.", now, ct).ConfigureAwait(false);
            return;
        }
        CreateRestoreArtifactsTask(
            runId, legServer, stepRun, stepDef, legVars, artifact, artifactSha256,
            releaseSelector, targetDirectory);
    }

    private async Task HandleMissingRestoreArtifactAsync(
        int runId,
        Server server,
        PipelineStepRun stepRun,
        PipelineStepDefinition stepDefinition,
        string releaseSelector,
        string? error,
        DateTime now,
        CancellationToken ct)
    {
        if (stepDefinition.AllowMissing && IsOptionalProducerArtifactUnavailable(stepDefinition, error))
        {
            // An optional artifact that does not exist yet (a first successful nightly has no
            // earlier evidence) is the expected case the step was declared for, not a run-level
            // warning. The step says it, in its own status; the steps that grade the comparison
            // record it as unavailable.
            await CompleteMissingRestoreArtifactAsync(
                runId, server, stepRun,
                $"Nothing to restore: the optional artifact '{stepDefinition.Artifact}' of {stepDefinition.ArtifactSourcePipeline} does not exist yet, so there is nothing to compare with ({error?.TrimEnd('.')}). The checks that read it record it as unavailable.",
                runNotice: false, now, ct).ConfigureAwait(false);
            return;
        }
        var canBootstrap = stepDefinition.AllowMissing
            && PipelineReleaseArtifactRules.IsBootstrapSelector(releaseSelector)
            && error?.Contains(PipelineReleaseArtifactRules.NoRetainedArtifact, StringComparison.Ordinal) == true;
        if (!canBootstrap)
        {
            await FailRestoreArtifactsAsync(
                runId, server, stepRun, error ?? "the requested artifact could not be resolved.", now, ct)
                .ConfigureAwait(false);
            return;
        }
        var currentRun = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        var projectId = currentRun?.Pipeline is null
            ? null
            : await repo.GetPipelineProjectIdAsync(currentRun.Pipeline, ct).ConfigureAwait(false);
        var hasPriorContract = projectId is not null
            && await IsMissingArtifactRequiredByPriorContractAsync(
                releaseSelector, projectId.Value, currentRun?.CommitHash,
                stepDefinition.Artifact, ct).ConfigureAwait(false);
        if (projectId is null || hasPriorContract)
        {
            await FailRestoreArtifactsAsync(runId, server, stepRun, error!, now, ct).ConfigureAwait(false);
            return;
        }
        await CompleteMissingRestoreArtifactAsync(
            runId, server, stepRun,
            "the selected release contract does not require this retained artifact; explicit fallback selected.",
            runNotice: true, now, ct)
            .ConfigureAwait(false);
    }

    private Task<bool> IsMissingArtifactRequiredByPriorContractAsync(
        string selector,
        int projectId,
        string? commitHash,
        string? artifactName,
        CancellationToken ct) =>
        PipelineReleaseArtifactRules.IsRequiredByPriorContractAsync(
            artifactRepo, selector, projectId, commitHash, artifactName, ct);

    private async Task FailRestoreArtifactsAsync(
        int runId,
        Server server,
        PipelineStepRun stepRun,
        string reason,
        DateTime now,
        CancellationToken ct)
    {
        stepRun.ServerId = server.Id;
        MarkSystemStepFailed(stepRun, TaskFailureCodes.ToolError, reason, now);
        await repo.AppendRunWarningsAsync(
            runId, [PipelineStepFailureWarning.RestoreArtifacts(stepRun.StepName, reason)], ct).ConfigureAwait(false);
    }

    private async Task CompleteMissingRestoreArtifactAsync(
        int runId,
        Server server,
        PipelineStepRun stepRun,
        string reason,
        bool runNotice,
        DateTime now,
        CancellationToken ct)
    {
        stepRun.ServerId = server.Id;
        stepRun.Status = TaskExecutionStatus.Success;
        stepRun.StartedAt ??= now;
        stepRun.CompletedAt = now;
        stepRun.SkippedReason = reason.Length <= 1000 ? reason : reason[..1000];
        if (runNotice)
        {
            // R-16: no baseline to restore on a project's first release is information, not a warning of the
            // run; the front says it in French from this code (the detail is for the log reader).
            await repo.AppendRunWarningsAsync(
                runId,
                [PipelineRunNotices.Compose(PipelineRunNotices.BaselineAbsent, $"Restore-artifacts step '{stepRun.StepName}': {reason}")],
                ct).ConfigureAwait(false);
        }
    }

    private void CreateRestoreArtifactsTask(
        int runId,
        Server server,
        PipelineStepRun stepRun,
        PipelineStepDefinition stepDefinition,
        Dictionary<string, string> legVariables,
        PipelineArtifact artifact,
        string artifactSha256,
        string releaseSelector,
        string targetDirectory)
    {
        var restoreVars = new Dictionary<string, string>(legVariables, StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_RESTORE_ARTIFACT_ID"] = artifact.Id.ToString(),
            ["AETHEUS_RESTORE_RUN_ID"] = runId.ToString(),
            ["AETHEUS_RESTORE_ARTIFACT_SHA256"] = artifactSha256,
            ["AETHEUS_RESTORE_ARTIFACT_SIZE_BYTES"] = artifact.SizeBytes.ToString(),
            ["AETHEUS_WORKING_DIR"] = legVariables.GetValueOrDefault("WORKSPACE", ""),
            ["AETHEUS_RESTORE_RELEASE_SELECTOR"] = releaseSelector
        };
        if (!string.IsNullOrWhiteSpace(targetDirectory))
            restoreVars["AETHEUS_RESTORE_TARGET_DIR"] = targetDirectory;
        // Only the explicit opt-out travels: an absent variable is the agent's fail-closed default.
        if (PipelineDeployedBaseline.IsOptedOut(stepDefinition.DeployedBaseline))
            restoreVars[PipelineDeployedBaseline.EnvironmentVariable] = PipelineDeployedBaseline.None;

        // The selector is substituted exactly as ResolveSelector substitutes it, so a templated
        // `artifact_source_selector: "$(VAR)"` that resolved the artifact as latest-successful is also
        // recognised as latest-successful here, instead of being refused on the raw "$(VAR)" text.
        var artifactSourceSelector = SubstituteVariables(
            stepDefinition.ArtifactSourceSelector ?? string.Empty, legVariables).Trim();
        if (ExpectedSourceCommitFor(legVariables, releaseSelector, artifactSourceSelector)
            is { } expectedSourceCommit)
            restoreVars["AETHEUS_RESTORE_EXPECTED_SOURCE_COMMIT"] = expectedSourceCommit;

        stepRun.ServerId = server.Id;
        var restoreTask = new ServerTask
        {
            ServerId = server.Id,
            Name = stepRun.StepName,
            Command = artifact.Name,
            PipelineRunId = runId,
            PipelineStepRunId = stepRun.Id,
            EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(restoreVars)),
            TimeoutSeconds = stepDefinition.TimeoutSeconds,
            Operation = OperationKind.PipelineRestoreArtifacts
        };
        repo.TrackTask(restoreTask);
        ArtifactInputRecorder.Track(repo, runId, stepRun, artifact, ArtifactInputKind.Restore, null,
            timeProvider.GetUtcNow().UtcDateTime);
        MarkStepDispatched(stepRun, restoreTask);
    }

    public async Task<(PipelineArtifact? Artifact, string? Error)> ResolveArtifactSourceAsync(
        int runId, Dictionary<string, string> variables, PipelineStepDefinition stepDef,
        CancellationToken ct)
    {
        var releaseSelector = SubstituteVariables(stepDef.Release ?? string.Empty, variables).Trim();
        var artifactName = SubstituteVariables(stepDef.Artifact ?? string.Empty, variables).Trim();
        var sourcePipelineName = SubstituteVariables(stepDef.ArtifactSourcePipeline ?? string.Empty, variables).Trim();
        if (!string.IsNullOrWhiteSpace(releaseSelector))
            return await ResolveReleaseArtifactAsync(
                runId, releaseSelector, artifactName, sourcePipelineName, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(artifactName) || string.IsNullOrWhiteSpace(sourcePipelineName))
            return (null, "'release' or both 'artifact' and 'artifact_source_pipeline' are required.");

        var (selector, selectorError) = ResolveSelector(stepDef, variables);
        if (selectorError is not null) return (null, selectorError);

        var context = await ResolveArtifactContextAsync(runId, ct).ConfigureAwait(false);
        if (context.Error is not null) return (null, context.Error);

        // Opt-in only: a step that asks for nothing keeps the lineage-then-same-commit resolution
        // below untouched. Asking for latest-successful deliberately gives both of them up.
        if (string.Equals(selector, LatestSuccessfulSelector, StringComparison.OrdinalIgnoreCase))
        {
            var latestArtifact = await artifactRepo.FindLatestSuccessfulPipelineArtifactAsync(
                context.Context!.ProjectId, sourcePipelineName, artifactName, ct).ConfigureAwait(false);
            return latestArtifact is null
                ? (null, $"artifact '{artifactName}' was never produced by a successful {sourcePipelineName} run.")
                : (latestArtifact, null);
        }

        var lineageRootRunId = ResolveLineageRootRunId(runId, variables);
        var sourceRunId = await repo.FindTriggeredRunIdByPipelineNameAsync(
            lineageRootRunId, sourcePipelineName, ct).ConfigureAwait(false);
        if (sourceRunId is { } verifiedRunId)
            return await ResolveTriggeredArtifactAsync(
                verifiedRunId, context.Context!, artifactName, sourcePipelineName, ct).ConfigureAwait(false);
        var existingArtifact = await artifactRepo.FindSuccessfulPipelineArtifactByCommitAsync(
            context.Context!.ProjectId, sourcePipelineName, context.Context.Run.CommitHash!, artifactName, ct).ConfigureAwait(false);
        return existingArtifact is null
            ? (null, $"artifact '{artifactName}' was not found on a successful {sourcePipelineName} run at commit {context.Context.Run.CommitHash}.")
            : (existingArtifact, null);
    }

    internal const string SameCommitSelector = "same-commit";
    internal const string LatestSuccessfulSelector = "latest-successful";

    /// <summary>The step's artifact source selector, defaulted and validated. An unknown value is an
    /// error rather than a fall-back to the default: silently widening or narrowing a provenance
    /// lookup because a name was misspelled is exactly the failure this selector must not have.</summary>
    private static (string Selector, string? Error) ResolveSelector(
        PipelineStepDefinition stepDef, Dictionary<string, string> variables)
    {
        var selector = SubstituteVariables(stepDef.ArtifactSourceSelector ?? string.Empty, variables).Trim();
        if (selector.Length == 0) return (SameCommitSelector, null);
        if (string.Equals(selector, SameCommitSelector, StringComparison.OrdinalIgnoreCase)
            || string.Equals(selector, LatestSuccessfulSelector, StringComparison.OrdinalIgnoreCase))
        {
            return (selector, null);
        }
        return (selector,
            $"'artifact_source_selector' must be '{SameCommitSelector}' or '{LatestSuccessfulSelector}', not '{selector}'.");
    }

    private async Task<(ArtifactResolutionContext? Context, string? Error)> ResolveArtifactContextAsync(
        int runId, CancellationToken ct)
    {
        var currentRun = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        if (currentRun?.Pipeline is null || !IsGitCommitHash(currentRun.CommitHash))
            return (null, "the current run has no verified source commit.");
        var projectId = await repo.GetPipelineProjectIdAsync(currentRun.Pipeline, ct).ConfigureAwait(false);
        return projectId is null
            ? (null, "the current run is not attached to a project.")
            : (new ArtifactResolutionContext(currentRun, projectId.Value), null);
    }

    private static int ResolveLineageRootRunId(int runId, IReadOnlyDictionary<string, string> variables) =>
        variables.TryGetValue("UPSTREAM_RUN_ID", out var parentRunIdText)
        && int.TryParse(parentRunIdText, out var parentRunId)
        && parentRunId > 0
            ? parentRunId
            : runId;

    private async Task<(PipelineArtifact? Artifact, string? Error)> ResolveTriggeredArtifactAsync(
        int sourceRunId,
        ArtifactResolutionContext context,
        string artifactName,
        string sourcePipelineName,
        CancellationToken ct)
    {
        var sourceRun = await repo.GetPipelineRunWithPipelineAsync(sourceRunId, ct).ConfigureAwait(false);
        var sourceProjectId = sourceRun?.Pipeline is null
            ? null
            : await repo.GetPipelineProjectIdAsync(sourceRun.Pipeline, ct).ConfigureAwait(false);
        if (sourceProjectId != context.ProjectId)
            return (null, "the source run belongs to another project.");
        if (!string.Equals(sourceRun?.CommitHash, context.Run.CommitHash, StringComparison.OrdinalIgnoreCase))
            return (null, "the source run belongs to another source commit.");
        // A Rejected source ran every control to the end and sealed what it found (recette R1-80): its
        // artifacts are complete, and what it found travels in its outputs. Only this lineage lookup
        // (the run this very pipeline triggered) accepts it; the same-commit and latest-successful
        // lookups above and below stay Success-only.
        if (sourceRun?.Status is not (PipelineStatus.Success or PipelineStatus.Rejected))
            return (null, "the source run did not succeed; its optional producer artifact is unavailable.");
        var artifact = await artifactRepo.FindRunArtifactByNameAsync(
            sourceRunId, artifactName, ct).ConfigureAwait(false);
        return artifact?.ProjectId == context.ProjectId
            ? (artifact, null)
            : (null, $"artifact '{artifactName}' was not found on successful {sourcePipelineName} run {sourceRunId}.");
    }

    private sealed record ArtifactResolutionContext(PipelineRun Run, int ProjectId);

    private async Task<(PipelineArtifact? Artifact, string? Error)> ResolveReleaseArtifactAsync(
        int runId,
        string releaseSelector,
        string artifactName,
        string sourcePipelineName,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(sourcePipelineName))
            return (null, "'release' cannot be combined with 'artifact_source_pipeline'.");
        var releaseRun = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        var projectId = releaseRun?.Pipeline is null
            ? null
            : await repo.GetPipelineProjectIdAsync(releaseRun.Pipeline, ct).ConfigureAwait(false);
        if (projectId is null)
            return (null, "the current run is not attached to a project.");

        return await PipelineReleaseArtifactRules.FindAsync(
            artifactRepo, projectId.Value, releaseRun?.CommitHash, releaseSelector, artifactName, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// D-04: the revision a restored artifact must prove it was built from, or null when there is
    /// nothing to prove.
    ///
    /// The restore does this itself rather than each consumer rewriting the comparison in shell
    /// afterwards. That is the whole correction: a consumer that forgot the verification stage used
    /// to scan, grade or ship another commit's build with no warning at all, which is exactly what
    /// the sealed delivery contract exists to prevent. Nothing is declared in YAML, so nothing can
    /// be left out.
    ///
    /// A release selector is the one legitimate exception: restoring what is currently published or
    /// deployed in order to compare against it means the artifact is EXPECTED to carry a different
    /// commit, and demanding the run's own revision there would refuse every baseline comparison.
    /// </summary>
    internal static string? ExpectedSourceCommitFor(
        IReadOnlyDictionary<string, string> legVariables,
        string? releaseSelector,
        string? artifactSourceSelector = null)
    {
        ArgumentNullException.ThrowIfNull(legVariables);
        // ANY release selector, not just the four symbolic ones. The rule below already says why:
        // restoring a release means the artifact is EXPECTED to carry a different commit. A named
        // release is the same case - it names a build, and a build has its own revision.
        //
        // Restricting the exemption to the symbolic selectors made aetheus-deploy-prod work only
        // while develop had not moved since the candidate was qualified. Run 2323 deployed candidate
        // c-384f63d4 from develop at 61a6b12a and every candidate restore was refused with "built
        // from 384f63d4, but this run was launched on 61a6b12a", which is the normal situation:
        // deploy-prod reads its instructions from develop HEAD and promotes an older, qualified
        // release. verify-release-ancestry.sh is what proves the two are related, not this check.
        if (!string.IsNullOrWhiteSpace(releaseSelector)) return null;
        // latest-successful is the second legitimate exception, and it was missing. The resolution
        // above says it "deliberately gives up" lineage AND same-commit lookup: it takes the newest
        // successful run of another pipeline, which by construction was built from another commit.
        // Demanding this run's revision of it refused every such restore outright.
        //
        // aetheus-deploy-prod restores the nightly qualification verdict exactly this way (its own
        // comment says a nightly runs on develop HEAD of that night, never on the commit being
        // deployed), so run 2321 failed on "the restored artifact carries no source-commit" and no
        // production deployment could complete. allow_missing did not save it: a refusal is not an
        // absence.
        if (string.Equals(artifactSourceSelector?.Trim(), LatestSuccessfulSelector, StringComparison.OrdinalIgnoreCase))
            return null;
        var commit = legVariables.GetValueOrDefault("BUILD_SOURCEVERSION");
        return string.IsNullOrWhiteSpace(commit) ? null : commit;
    }


    private static bool IsOptionalProducerArtifactUnavailable(
        PipelineStepDefinition step,
        string? error) =>
        !string.IsNullOrWhiteSpace(step.ArtifactSourcePipeline)
        && (error?.StartsWith("artifact '", StringComparison.Ordinal) == true
            || error?.Contains("optional producer artifact is unavailable", StringComparison.Ordinal) == true);

    public async Task CreateBackupRestoreTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, CancellationToken ct)
    {
        async Task FailAsync(string reason)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            stepRun.ServerId = legServer.Id;
            MarkSystemStepFailed(stepRun, TaskFailureCodes.ToolError, reason, now);
            await repo.AppendRunWarningsAsync(
                runId, [PipelineStepFailureWarning.RestoreBackup(stepRun.StepName, reason)], ct).ConfigureAwait(false);
        }

        var selector = PipelineCommandBuilder.Substitute(stepDef.BackupRun ?? string.Empty, legVars);
        if (!int.TryParse(selector, out var backupRunId))
        {
            await FailAsync("backup_run must resolve to a verified backup run id.").ConfigureAwait(false);
            return;
        }

        var backup = backupRepo is null ? null
            : await backupRepo.FindRunWithPolicyAsync(backupRunId, ct).ConfigureAwait(false);
        if (!IsUsableBackup(backup, legServer.Id))
        {
            await FailAsync("the selected backup is not successful, verified, or owned by this target server.").ConfigureAwait(false);
            return;
        }
        var verifiedBackup = backup!;

        // A verified archive is still not interchangeable between projects.  Resolve the project
        // from the run being executed (rather than trusting the YAML or a caller-provided id) before
        // exposing the archive and DB credentials to the agent.
        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        var runProjectId = run?.Pipeline is null
            ? null
            : await repo.GetPipelineProjectIdAsync(run.Pipeline, ct).ConfigureAwait(false);
        if (runProjectId is null || runProjectId != verifiedBackup.BackupPolicy!.ProjectId)
        {
            await FailAsync("the selected backup belongs to a different project.").ConfigureAwait(false);
            return;
        }

        var restoreVars = BuildBackupRestoreVariables(verifiedBackup, legVars);

        stepRun.ServerId = legServer.Id;
        var restoreTask = new ServerTask
        {
            ServerId = legServer.Id,
            Name = stepRun.StepName,
            Command = verifiedBackup.Id.ToString(),
            PipelineRunId = runId,
            PipelineStepRunId = stepRun.Id,
            EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(restoreVars)),
            TimeoutSeconds = stepDef.TimeoutSeconds,
            Operation = OperationKind.BackupRestore
        };
        repo.TrackTask(restoreTask);
        MarkStepDispatched(stepRun, restoreTask);
    }

    private static bool IsUsableBackup(BackupRun? backup, int serverId) =>
        backup?.BackupPolicy is not null
        && backup.ServerId == serverId
        && backup.Status == BackupRunStatus.Succeeded
        && backup.RestoreCheckStatus == RestoreCheckStatus.Verified
        && !string.IsNullOrWhiteSpace(backup.ArchivePath);

    private Dictionary<string, string> BuildBackupRestoreVariables(
        BackupRun backup, IReadOnlyDictionary<string, string> legVars)
    {
        var policy = backup.BackupPolicy!;
        var variables = new Dictionary<string, string>(legVars, StringComparer.OrdinalIgnoreCase)
        {
            [BackupConstants.EngineEnvVar] = policy.DbEngine.ToString(),
            [BackupConstants.PolicyIdEnvVar] = policy.Id.ToString(),
            [BackupConstants.RunIdEnvVar] = backup.Id.ToString(),
            [BackupConstants.ArchivePathEnvVar] = backup.ArchivePath!
        };
        if (!string.IsNullOrWhiteSpace(policy.DbHost)) variables[BackupConstants.DbHostEnvVar] = policy.DbHost;
        if (policy.DbPort is not null) variables[BackupConstants.DbPortEnvVar] = policy.DbPort.Value.ToString();
        if (!string.IsNullOrWhiteSpace(policy.DbName)) variables[BackupConstants.DbNameEnvVar] = policy.DbName;
        if (!string.IsNullOrWhiteSpace(policy.DbUser)) variables[BackupConstants.DbUserEnvVar] = policy.DbUser;
        if (!string.IsNullOrWhiteSpace(policy.DbPasswordEncrypted))
            variables[BackupConstants.DbPasswordEnvVar] = encryption.DecryptValue(policy.DbPasswordEncrypted);
        return variables;
    }

}
