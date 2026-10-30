// SPDX-License-Identifier: EUPL-1.2
using System.Linq.Expressions;
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Maps <see cref="PipelineRun"/> to API DTOs through server-side list projections or
/// in-memory detail mapping. Result summaries are computed separately by the database.</summary>
public static class PipelineRunDtoMapper
{
    // Run-list projections belong to Monitoring; these names expose the same read model to pipeline
    // grids.
    public static readonly Expression<Func<PipelineRun, PipelineRunDto>> RunListProjection = PipelineRunListRows.Projection;

    public static PipelineRunDto MapRunToDto(PipelineRun r)
    {
        var definition = ParseSnapshot(r);
        var dto = MapRunToDto(
            r,
            definition is null ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) : StageDepths(definition),
            definition is null ? [] : PipelineRunScheduler.AdvisoryStepKeys(definition));
        return dto with { DeployedAddress = PipelineRunDeployedAddress.Read(r, definition, dto.ResolvedVariables) };
    }

    /// <summary>The run's own definition snapshot, null when missing or unparseable.</summary>
    private static PipelineYamlDefinition? ParseSnapshot(PipelineRun run) =>
        PipelineAdvisorySteps.ParseSnapshot(run.YamlSnapshot);

    /// <summary>
    /// Depth of every stage in the run's own definition snapshot: 0 for a stage that waits for
    /// nothing, 1 for one that waits only on depth-0 stages, and so on. Stages sharing a depth run
    /// concurrently, and saying so is the whole point - a run listed in definition order shows a
    /// stage turning green above one that is still running, with nothing to say the two were never
    /// sequential.
    ///
    /// Reads the SNAPSHOT, not the pipeline's current YAML: a run is explained by the definition it
    /// actually executed. A snapshot that is missing or unparseable yields no depths at all rather
    /// than a plausible-looking wrong order.
    /// </summary>
    private static Dictionary<string, int> StageDepths(PipelineYamlDefinition definition)
    {
        var depths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stages = YamlParsingHelper.FlattenJobs(definition).ToList();
        var byName = stages
            .GroupBy(stage => stage.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        int Depth(PipelineStageDefinition stage, HashSet<string> visiting)
        {
            if (depths.TryGetValue(stage.Name, out var known)) return known;
            // A cycle cannot be ordered, and the parser refuses one anyway; treat it as depth 0
            // rather than recursing forever on a definition that reached us some other way.
            if (!visiting.Add(stage.Name)) return 0;
            var depth = 0;
            foreach (var dependency in stage.DependsOn)
                if (byName.TryGetValue(dependency, out var parent))
                    depth = Math.Max(depth, Depth(parent, visiting) + 1);
            visiting.Remove(stage.Name);
            depths[stage.Name] = depth;
            return depth;
        }

        foreach (var stage in stages) Depth(stage, []);
        return depths;
    }

    /// <summary>A run that can still be waiting for something. Terminal runs never can.</summary>
    private static bool IsWaitable(PipelineStatus status) =>
        status is PipelineStatus.Running or PipelineStatus.Pending;

    /// <summary>The preflight record kept on the run. A malformed or older payload reads as "nothing
    /// recorded" rather than failing the whole run detail.</summary>
    private static List<PreflightCheckDto> DeserializePreflight(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<List<PreflightCheckDto>>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    /// <summary>The author or reason a cancellation recorded on the run, null when none.</summary>
    private static string? CancellationNote(string? additionalVariablesJson, string variable) =>
        DeserializeResolvedVariables(additionalVariablesJson).TryGetValue(variable, out var value)
            && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static PipelineRunDto MapRunToDto(
        PipelineRun r, Dictionary<string, int> stageDepths, HashSet<string> advisoryKeys) => new()
        {
            Id = r.Id,
            PipelineId = r.PipelineId,
            ProjectId = r.Pipeline?.ProjectId,
            PipelineName = r.Pipeline?.Name ?? string.Empty,
            Status = r.Status,
            RunToEnd = r.RunToEnd,
            GateOverrideBy = r.GateOverrideBy,
            GateOverrideAt = r.GateOverrideAt,
            GateOverrideWarnings = PipelineGateOverride.ReadWarnings(r.GateOverrideWarningsJson),
            ResumeDeadline = r.Status == PipelineStatus.AwaitingResume ? r.ResumeDeadline : null,
            CancellationRequested = HasCancellationRequest(r.AdditionalVariablesJson),
            CancelledBy = CancellationNote(r.AdditionalVariablesJson, PipelineRunHelpers.CancelledByVariable),
            CancellationReason = CancellationNote(r.AdditionalVariablesJson, PipelineRunHelpers.CancellationReasonVariable),
            // See RunListProjection: a terminal run never reports a current wait.
            WaitingReason = IsWaitable(r.Status) ? r.WaitingReason : null,
            WaitingSince = IsWaitable(r.Status) ? r.WaitingSince : null,
            PreflightChecks = DeserializePreflight(r.PreflightJson),
            StartedAt = r.StartedAt,
            CompletedAt = r.CompletedAt,
            BranchName = r.BranchName,
            CommitHash = r.CommitHash,
            RepositoryUrl = r.RepositoryUrl,
            ProjectName = r.Pipeline?.Project?.Name,
            YamlSnapshot = r.YamlSnapshot,
            ResolvedVariables = DeserializeResolvedVariables(r.ResolvedVariablesJson),
            Parameters = DeserializeResolvedVariables(r.ParametersJson),
            Warnings = DeserializeWarnings(r.WarningsJson),
            // Same worst-letter-wins rule as RunListProjection (see there); the detail path loads the
            // evaluations in full so this is a plain in-memory Max, no extra query. Max on an empty
            // nullable-enum sequence returns null (LINQ contract), so no evaluation -> ungraded, not A.
            GateGrade = r.AnalysisEvaluations
            .Where(evaluation => evaluation.Grade != null)
            .Max(evaluation => evaluation.Grade),
            Steps = r.StepRuns.Select(s => MapStep(s, stageDepths, advisoryKeys)).ToList()
            // Tests, coverage, lint, metrics and artifacts are not mapped from loaded rows;
            // PipelineRunReader fills them from database-computed summaries
            // (GetRunResultSummariesAsync, GetTestResultSummaryAsync).
        };

    private static PipelineStepRunDto MapStep(
        PipelineStepRun s, Dictionary<string, int> stageDepths, HashSet<string> advisoryKeys) => new()
        {
            Id = s.Id,
            StepName = s.StepName,
            StageName = s.StageName,
            Status = s.Status,
            ServerId = s.ServerId,
            ServerName = s.Server?.Name,
            ServerOs = s.Server?.OsDescription,
            StartedAt = s.StartedAt,
            CompletedAt = s.CompletedAt,
            ExitCode = s.ExitCode,
            FailureCode = s.Task?.FailureCode ?? s.FailureCode,
            FailureReason = s.Task?.FailureReason ?? s.FailureReason,
            OutputVariables = DeserializeResolvedVariables(s.OutputVariablesJson),
            RetryCount = s.RetryCount,
            ContinueOnError = s.ContinueOnError,
            IsAdvisory = PipelineRunScheduler.IsAdvisory(advisoryKeys, s.StageName, s.StepName, s.MatrixLeg),
            IsRejected = s.Status == TaskExecutionStatus.Success
                && !PipelineRunScheduler.IsAdvisory(advisoryKeys, s.StageName, s.StepName, s.MatrixLeg)
                && PipelineControlVerdict.RejectsResult(DeserializeResolvedVariables(s.OutputVariablesJson)),
            MatrixLeg = s.MatrixLeg,
            TaskId = s.TaskId,
            IsSystem = s.IsSystem,
            GroupName = s.GroupName,
            StageDepth = stageDepths.TryGetValue(s.StageName, out var depth) ? depth : null,
            SkippedCondition = s.SkippedCondition,
            SkippedConditionVariables = DeserializeResolvedVariables(s.SkippedConditionVariablesJson),
            SkippedReason = s.SkippedReason,
            // Command is masked in GetRunAsync (async secret-masking) before reaching the client.
            Command = s.Task?.Command,
            IsContainerIsolated = !string.IsNullOrEmpty(s.Task?.ContainerImage)
                || !string.IsNullOrEmpty(s.Task?.ContainerToolchain),
            TriggeredRunId = s.TriggeredRunId
        };

    public static PipelineRunDto HydrateListWarnings(PipelineRunDto run) => PipelineRunListRows.HydrateWarnings(run);
}
