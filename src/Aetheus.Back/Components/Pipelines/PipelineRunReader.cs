// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.GitGraph;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineDtoMapper;
using static Aetheus.Back.Components.Pipelines.PipelineRunDtoMapper;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Reads one run back as the DTO the API and the launcher both return.</summary>
public interface IPipelineRunReader
{
    /// <summary>The run with its steps, queue positions and mapped detail, or <c>null</c> if unknown.</summary>
    Task<PipelineRunDto?> GetRunAsync(int runId, CancellationToken ct = default);
}

/// <summary>
/// The run-detail query, extracted from <see cref="PipelineRunService"/> so that
/// <see cref="PipelineRunLauncher"/> can return a launched run without depending on the facade it is
/// called from. A leaf: repository read plus mapping, no state change.
/// </summary>
public sealed class PipelineRunReader(
    IPipelineRepository repo,
    IGitGraphRecorder gitGraph,
    ISecretMaskingService secretMasking,
    IPipelineVariableResolver variableResolver) : IPipelineRunReader
{
    public async Task<PipelineRunDto?> GetRunAsync(int runId, CancellationToken ct = default)
    {
        var run = await repo.GetRunDetailAsync(runId, ct).ConfigureAwait(false);
        if (run is null) return null;

        // The detail query leaves every result row out. The tests are counted by outcome, the
        // coverage and lint figures, the metrics and the artifacts are computed and projected by
        // the database; the coverage per-file list is read by the Coverage tab when it opens.
        var results = await repo.GetRunResultSummariesAsync(run, ct).ConfigureAwait(false);
        var dto = MapRunToDto(run) with
        {
            TestResultSummary = await repo.GetTestResultSummaryAsync(runId, ct).ConfigureAwait(false),
            CoverageSummary = results.Coverage,
            LintSummary = results.Lint,
            Metrics = results.Metrics,
            Artifacts = results.Artifacts
        };
        dto = await DropResolvedWarningsAsync(run, dto, ct).ConfigureAwait(false);
        // A candidate run owns no evaluation of its own (it delegates every analysis to a child
        // pipeline and publishes its verdict via CANDIDATE_ASSURANCE_GRADE); the detail path already
        // loaded Steps.OutputVariables in full, so this resolves synchronously, no extra query.
        dto = PipelineRunGradeHydration.Apply(dto);
        if (dto.GateGrade is null)
            dto = await repo.HydrateLinkedGradeAsync(dto, ct).ConfigureAwait(false);
        dto = await AddQueuePositionsAsync(dto, ct).ConfigureAwait(false);
        // Resolve the run's commit/branch to internal git-graph pages (mirrors the Release/Artifact
        // detail views). Cheap point lookups; empty when no graph node exists yet (plain-text fallback).
        if (run.Pipeline?.ProjectId is { } projectId)
        {
            var (commits, branches) = await gitGraph.ResolveRunLinksAsync(projectId, run.CommitHash, run.BranchName, ct).ConfigureAwait(false);
            dto = dto with { Commits = commits, Branches = branches };
        }

        // Trigger steps are NOT flattened into this run's step list. Each keeps its TriggeredRunId +
        // TriggeredPipelineName so the run view renders the child pipeline as a collapsible node and
        // lazy-loads its own run detail on expand (recursive tree, keyed by run id - see the front
        // PipelineRun timeline). Flattening here produced an unreadable, duplicate-prone linear list.
        return await MaskStepsAsync(dto, runId, ct).ConfigureAwait(false);
    }

    // Warnings are written once, at launch. On a run that has not finished, a missing variable
    // library the user has since created is no longer missing, and showing it as a live warning on a
    // RUNNING run says otherwise. Re-asked here rather than rewritten in the database: the stored
    // list stays the launch record, this is what is true when someone looks.
    private async Task<PipelineRunDto> DropResolvedWarningsAsync(PipelineRun run, PipelineRunDto dto, CancellationToken ct)
    {
        if (dto.Warnings.Count == 0 || run.Status is not (PipelineStatus.Pending or PipelineStatus.Running
                or PipelineStatus.WaitingForApproval or PipelineStatus.AwaitingResume))
            return dto;
        var current = await variableResolver
            .DropResolvedLibraryWarningsAsync(dto.Warnings, run.Pipeline?.ProjectId, ct).ConfigureAwait(false);
        return current is not null && current.Count != dto.Warnings.Count ? dto with { Warnings = current } : dto;
    }

    private async Task<PipelineRunDto> AddQueuePositionsAsync(PipelineRunDto dto, CancellationToken ct)
    {
        var queuedTaskIds = dto.Steps
            .Where(step => step.Status == TaskExecutionStatus.Assigned && step.TaskId.HasValue)
            .Select(step => step.TaskId!.Value)
            .ToArray();
        if (queuedTaskIds.Length == 0)
            return dto;
        var positions = await repo.GetTaskQueuePositionsAsync(queuedTaskIds, ct).ConfigureAwait(false);
        return dto with
        {
            Steps = dto.Steps.Select(step =>
                step.TaskId is { } taskId && positions.TryGetValue(taskId, out var queue)
                    ? step with { QueuePosition = queue.Position, QueueDepth = queue.Depth }
                    : step).ToList()
        };
    }

    // S-UX-18: surface each step's executed command, but mask substituted secret values first -
    // ServerTask.Command stores the resolved command verbatim (unlike logs, masked at write time).
    // OutputVariables get the same treatment: a step can `setvariable` a secret's value, which
    // would otherwise reach any Project.Read user verbatim, bypassing the log masking.
    private async Task<PipelineRunDto> MaskStepsAsync(PipelineRunDto dto, int runId, CancellationToken ct)
    {
        if (!dto.Steps.Any(s => !string.IsNullOrEmpty(s.Command)
                                || !string.IsNullOrEmpty(s.FailureReason)
                                || s.OutputVariables.Count > 0))
            return dto;
        var maskedSteps = new List<PipelineStepRunDto>(dto.Steps.Count);
        foreach (var step in dto.Steps)
            maskedSteps.Add(await MaskStepAsync(step, runId, ct).ConfigureAwait(false));
        return dto with { Steps = maskedSteps };
    }

    private async Task<PipelineStepRunDto> MaskStepAsync(PipelineStepRunDto step, int runId, CancellationToken ct)
    {
        var masked = step;
        if (!string.IsNullOrEmpty(step.Command))
            masked = masked with { Command = await secretMasking.MaskAsync(step.Command, runId, ct).ConfigureAwait(false) };
        if (!string.IsNullOrEmpty(step.FailureReason))
            masked = masked with
            {
                FailureReason = await secretMasking.MaskAsync(step.FailureReason, runId, ct).ConfigureAwait(false)
            };
        if (step.OutputVariables.Count > 0)
        {
            var maskedVars = new Dictionary<string, string>(step.OutputVariables.Count);
            foreach (var (key, value) in step.OutputVariables)
                maskedVars[key] = await secretMasking.MaskAsync(value, runId, ct).ConfigureAwait(false);
            masked = masked with { OutputVariables = maskedVars };
        }
        return masked;
    }
}
