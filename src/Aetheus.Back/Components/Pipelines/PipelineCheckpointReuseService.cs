// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text.Json;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Decides whether a `type: trigger` step may skip its child run and adopt an earlier successful run's
/// result instead (ADR-036). Reuse is refused unless every contract still holds: same commit, same
/// parameters, same resolved YAML, same orchestrator version, same agents and scanner manifests, and
/// artifact bytes that re-hash to their recorded SHA-256 under a retention lease.
/// </summary>
public interface IPipelineCheckpointReuseService
{
    /// <summary>Adopts a verified checkpoint into <paramref name="stepRun"/>.</summary>
    /// <returns><c>true</c> when the checkpoint was reused and the step is already complete; <c>false</c>
    /// when the caller must launch the child run normally.</returns>
    Task<bool> TryReuseCheckpointAsync(
        PipelineRun currentParent,
        Pipeline target,
        string targetName,
        PipelineRunPreparation preparation,
        IReadOnlyDictionary<string, string> expectedParameters,
        PipelineStepRun stepRun,
        DateTime now,
        CancellationToken ct);

    /// <summary>Reports, without mutating anything, which checkpoints of <paramref name="sourceRunId"/>
    /// look reusable. Advisory only - every contract is revalidated at launch.</summary>
    Task<PipelineCheckpointResumePreviewDto?> GetCheckpointResumePreviewAsync(
        int sourceRunId, CancellationToken ct = default);
}

/// <summary>Checkpoint reuse fails closed: missing runs, artifacts, matching agent versions or
/// valid hashes require replay.</summary>
public sealed class PipelineCheckpointReuseService(
    IPipelineRepository repo,

    // Verification reads and lease writes use the artifact repository directly because
    // ArtifactService depends on the pipeline run service and would introduce a dependency cycle
    // (ADR-039).
    IArtifactRepository artifactRepo,
    IArtifactStorageService artifactStorage,
    IAuditService audit,
    IPipelineRunParameterResolver parameterResolver,
    IPostgresLeaderLease operationLock) : IPipelineCheckpointReuseService
{
    /// <summary>Candidate children are identified by shipped template role or the recognized
    /// self-deploy name; preview names follow the candidate's variables.</summary>
    private static readonly CheckpointRole[] ResumableRoles =
    [
        new("aetheus-ci", DeliveryTemplateNames.Ci, "APPLICATION_CI_PIPELINE", "AETHEUS_CHECKPOINT_CI_RUN_ID"),
        new("aetheus-quality", DeliveryTemplateNames.Quality, "APPLICATION_QUALITY_PIPELINE", "AETHEUS_CHECKPOINT_QUALITY_RUN_ID"),
        new("aetheus-security", DeliveryTemplateNames.Security, "APPLICATION_SECURITY_PIPELINE", "AETHEUS_CHECKPOINT_SECURITY_RUN_ID")
    ];

    private sealed record CheckpointRole(
        string LegacyPipelineName, string TemplateName, string PipelineVariable, string CheckpointVariable);

    private static CheckpointRole? RoleOf(Pipeline target, string targetName) =>
        Array.Find(ResumableRoles, role =>
            string.Equals(targetName, role.LegacyPipelineName, StringComparison.OrdinalIgnoreCase)
            || DeliveryTemplateNames.Is(target.TemplateReferenceName, role.TemplateName));

    public async Task<bool> TryReuseCheckpointAsync(
        PipelineRun currentParent,
        Pipeline target,
        string targetName,
        PipelineRunPreparation preparation,
        IReadOnlyDictionary<string, string> expectedParameters,
        PipelineStepRun stepRun,
        DateTime now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(currentParent);
        ArgumentNullException.ThrowIfNull(target);

        if (RoleOf(target, targetName) is not { } role) return false;
        if (!TryGetResumeSourceRunId(currentParent, out var sourceParentRunId)) return false;
        var sourceParent = await GetCompatibleSourceParentAsync(
            currentParent, sourceParentRunId, ct).ConfigureAwait(false);
        if (sourceParent is null) return false;
        var checkpoint = await GetSuccessfulCheckpointAsync(
            sourceParentRunId, currentParent, target, targetName, ct).ConfigureAwait(false);
        if (checkpoint is null) return false;
        if (!await CheckpointDefinitionMatchesAsync(
                checkpoint, preparation, expectedParameters, ct).ConfigureAwait(false))
            return false;
        if (!CheckpointOrchestratorMatches(checkpoint)) return false;
        if (!await CheckpointAgentsMatchAsync(checkpoint, ct).ConfigureAwait(false)) return false;

        var artifacts = await artifactRepo.GetByRunAsync(checkpoint.Id, ct).ConfigureAwait(false);
        if (artifacts.Count == 0) return false;
        if (!await VerifyAndLeaseCheckpointArtifactsAsync(
                artifacts, checkpoint.Id, target.Id, now, ct).ConfigureAwait(false))
            return false;

        await CompleteCheckpointReuseAsync(
            currentParent, targetName, role, sourceParentRunId, checkpoint.Id, artifacts.Count, stepRun, now, ct)
            .ConfigureAwait(false);
        return true;
    }

    public async Task<PipelineCheckpointResumePreviewDto?> GetCheckpointResumePreviewAsync(
        int sourceRunId,
        CancellationToken ct = default)
    {
        var source = await repo.GetRunDetailAsync(sourceRunId, ct).ConfigureAwait(false);
        if (source is null) return null;
        var items = new List<PipelineCheckpointResumeItemDto>();
        var sourceVariables = DeserializeResolvedVariables(source.ResolvedVariablesJson);
        foreach (var role in ResumableRoles)
        {
            var pipelineName = sourceVariables.TryGetValue(role.PipelineVariable, out var named)
                && !string.IsNullOrWhiteSpace(named)
                    ? named.Trim()
                    : role.LegacyPipelineName;
            var childId = await repo.FindTriggeredRunIdByPipelineNameAsync(sourceRunId, pipelineName, ct)
                .ConfigureAwait(false);
            if (childId is not { } runId)
            {
                items.Add(new PipelineCheckpointResumeItemDto
                {
                    PipelineName = pipelineName,
                    Reason = "No checkpoint run; the pipeline will be replayed."
                });
                continue;
            }
            var child = await repo.GetRunDetailAsync(runId, ct).ConfigureAwait(false);
            var artifacts = await artifactRepo.GetByRunAsync(runId, ct).ConfigureAwait(false);
            var tasks = child?.StepRuns.Select(step => step.Task).OfType<ServerTask>().ToList() ?? [];
            var hasCapturedContracts = tasks.Count > 0
                && tasks.All(task =>
                    !string.IsNullOrWhiteSpace(task.AssignedAgentVersion)
                    && (task.Operation != OperationKind.PipelineRunScanner
                        || !string.IsNullOrWhiteSpace(task.AssignedScannerManifestSha256)));
            var candidate = child?.Status == PipelineStatus.Success
                && artifacts.Count > 0
                && artifacts.All(artifact => artifact.Sha256 is { Length: 64 } hash && hash.All(Uri.IsHexDigit))
                && hasCapturedContracts;
            items.Add(new PipelineCheckpointResumeItemDto
            {
                PipelineName = pipelineName,
                RunId = runId,
                ReuseCandidate = candidate,
                Reason = candidate
                    ? "Candidate for reuse; all contracts and bytes will be revalidated atomically at launch."
                    : "Incomplete or non-success checkpoint; the pipeline will be replayed."
            });
        }
        return new PipelineCheckpointResumePreviewDto { SourceRunId = sourceRunId, Items = items };
    }

    private static bool TryGetResumeSourceRunId(PipelineRun currentParent, out int sourceParentRunId)
    {
        sourceParentRunId = 0;
        var parentVariables = DeserializeResolvedVariables(currentParent.AdditionalVariablesJson);
        return parentVariables.TryGetValue(PipelineRunService.ResumeSourceRunVariable, out var sourceIdText)
            && int.TryParse(sourceIdText, out sourceParentRunId);
    }

    private async Task<PipelineRun?> GetCompatibleSourceParentAsync(
        PipelineRun currentParent,
        int sourceParentRunId,
        CancellationToken ct)
    {
        var sourceParent = await repo.GetRunDetailAsync(sourceParentRunId, ct).ConfigureAwait(false);
        return sourceParent is not null
            && sourceParent.PipelineId == currentParent.PipelineId
            && string.Equals(sourceParent.CommitHash, currentParent.CommitHash, StringComparison.OrdinalIgnoreCase)
            && DictionariesEqual(
                DeserializeResolvedVariables(sourceParent.ParametersJson),
                DeserializeResolvedVariables(currentParent.ParametersJson))
            ? sourceParent
            : null;
    }

    private async Task<PipelineRun?> GetSuccessfulCheckpointAsync(
        int sourceParentRunId,
        PipelineRun currentParent,
        Pipeline target,
        string targetName,
        CancellationToken ct)
    {
        var childId = await repo.FindTriggeredRunIdByPipelineNameAsync(sourceParentRunId, targetName, ct)
            .ConfigureAwait(false);
        if (childId is not { } checkpointRunId) return null;
        var checkpoint = await repo.GetRunDetailAsync(checkpointRunId, ct).ConfigureAwait(false);
        return checkpoint is not null
            && checkpoint.Status == PipelineStatus.Success
            && checkpoint.PipelineId == target.Id
            && string.Equals(checkpoint.CommitHash, currentParent.CommitHash, StringComparison.OrdinalIgnoreCase)
            ? checkpoint
            : null;
    }

    private async Task<bool> CheckpointDefinitionMatchesAsync(
        PipelineRun checkpoint,
        PipelineRunPreparation preparation,
        IReadOnlyDictionary<string, string> expectedParameters,
        CancellationToken ct)
    {
        var resolvedPreparation = await parameterResolver.ResolveRunParametersAsync(
            preparation, expectedParameters, ct).ConfigureAwait(false);
        return PipelineParameterResolver.TryResolve(
                resolvedPreparation.Definition.Parameters,
                expectedParameters,
                out var effectiveParameters,
                out _)
            && string.Equals(checkpoint.YamlSnapshot, resolvedPreparation.YamlSnapshot, StringComparison.Ordinal)
            && DictionariesEqual(
                DeserializeResolvedVariables(checkpoint.ParametersJson),
                effectiveParameters);
    }

    private static bool CheckpointOrchestratorMatches(PipelineRun checkpoint)
    {
        var resolvedVariables = DeserializeResolvedVariables(checkpoint.ResolvedVariablesJson);
        var currentVersion = typeof(PipelineVariableResolver).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        return resolvedVariables.TryGetValue("AETHEUS_VERSION", out var checkpointVersion)
            && string.Equals(checkpointVersion, currentVersion, StringComparison.Ordinal);
    }

    private async Task<bool> CheckpointAgentsMatchAsync(PipelineRun checkpoint, CancellationToken ct)
    {
        var checkpointTasks = checkpoint.StepRuns.Select(step => step.Task).OfType<ServerTask>().ToList();
        if (checkpointTasks.Count == 0) return false;
        foreach (var task in checkpointTasks)
        {
            var currentServer = await repo.FindServerByIdAsync(task.ServerId, ct).ConfigureAwait(false);
            if (currentServer is null
                || string.IsNullOrWhiteSpace(task.AssignedAgentVersion)
                || !string.Equals(task.AssignedAgentVersion, currentServer.AgentVersion, StringComparison.Ordinal))
                return false;
            if (!ScannerContractMatches(task, currentServer)) return false;
        }
        return true;
    }

    private static bool ScannerContractMatches(ServerTask task, Server currentServer)
    {
        if (task.Operation != OperationKind.PipelineRunScanner) return true;
        var currentManifest = TaskRepository.ExtractScannerManifestSha256(currentServer.ScannerCapabilitiesJson);
        return !string.IsNullOrWhiteSpace(task.AssignedScannerManifestSha256)
            && string.Equals(task.AssignedScannerManifestSha256, currentManifest, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> VerifyAndLeaseCheckpointArtifactsAsync(
        IReadOnlyCollection<PipelineArtifact> artifacts,
        int checkpointRunId,
        int targetPipelineId,
        DateTime now,
        CancellationToken ct)
    {
        var verifiedAndLeased = false;
        await RunWithArtifactRetentionLocksAsync(
            artifacts.Select(artifact => artifact.Id).Order().ToArray(),
            async lockToken =>
            {
                verifiedAndLeased = await VerifyArtifactSetAndLeaseAsync(
                    artifacts, checkpointRunId, targetPipelineId, now, lockToken).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        return verifiedAndLeased;
    }

    private async Task<bool> VerifyArtifactSetAndLeaseAsync(
        IReadOnlyCollection<PipelineArtifact> artifacts,
        int checkpointRunId,
        int targetPipelineId,
        DateTime now,
        CancellationToken ct)
    {
        foreach (var artifact in artifacts)
        {
            if (artifact.PipelineRunId != checkpointRunId
                || artifact.PipelineId != targetPipelineId
                || artifact.Sha256 is not { Length: 64 } expectedSha256
                || !expectedSha256.All(Uri.IsHexDigit))
                return false;
            await using var content = artifactStorage.OpenArtifact(artifact.FilePath);
            if (content is null || content.Length != artifact.SizeBytes) return false;
            var actualSha256 = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(content, ct).ConfigureAwait(false));
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase)) return false;
        }

        var leaseDeadline = now.AddHours(24);
        foreach (var artifact in artifacts)
            if (artifact.RetentionLeaseExpiresAt is null || artifact.RetentionLeaseExpiresAt < leaseDeadline)
                artifact.RetentionLeaseExpiresAt = leaseDeadline;
        await artifactRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task CompleteCheckpointReuseAsync(
        PipelineRun currentParent,
        string targetName,
        CheckpointRole role,
        int sourceParentRunId,
        int checkpointRunId,
        int artifactCount,
        PipelineStepRun stepRun,
        DateTime now,
        CancellationToken ct)
    {

        var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in await repo.GetSuccessfulStepOutputsAsync(checkpointRunId, ct).ConfigureAwait(false))
            foreach (var (name, value) in DeserializeResolvedVariables(output.OutputVariablesJson))
                outputs[name] = value;
        outputs[role.CheckpointVariable] = checkpointRunId.ToString();
        stepRun.Status = TaskExecutionStatus.Success;
        stepRun.ExitCode = 0;
        stepRun.StartedAt = now;
        stepRun.CompletedAt = now;
        stepRun.TriggeredRunId = checkpointRunId;
        stepRun.OutputVariablesJson = outputs.Count == 0 ? null : JsonSerializer.Serialize(outputs);
        await repo.AppendRunWarningsAsync(currentParent.Id,
            [PipelineProblem.Of(
                PipelineProblemCodes.CheckpointReused,
                $"Checkpoint reused: pipeline '{targetName}', run #{checkpointRunId}, {artifactCount} verified artifact(s), source run #{sourceParentRunId}.",
                ("pipeline", targetName), ("run", checkpointRunId), ("artifacts", artifactCount), ("source", sourceParentRunId)).ToStored()], ct)
            .ConfigureAwait(false);
        await audit.LogAsync("PipelineCheckpointReused", "PipelineRun", currentParent.Id,
            $"{targetName} | checkpoint={checkpointRunId} | source={sourceParentRunId} | artifacts={artifactCount}", ct)
            .ConfigureAwait(false);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private Task RunWithArtifactRetentionLocksAsync(
        IReadOnlyList<int> artifactIds,
        Func<CancellationToken, Task> operation,
        CancellationToken ct)
    {
        Task AcquireAsync(int index, CancellationToken lockToken) => index == artifactIds.Count
            ? operation(lockToken)
            : operationLock.RunSerializedAsync(
                ArtifactRetentionLock.For(artifactIds[index]),
                nextToken => AcquireAsync(index + 1, nextToken),
                lockToken);

        return AcquireAsync(0, ct);
    }

    private static bool DictionariesEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var value)
            && string.Equals(pair.Value, value, StringComparison.Ordinal));
}
