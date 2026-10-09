// SPDX-License-Identifier: EUPL-1.2
using System.Linq.Expressions;
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Maps a <see cref="PipelineRun"/> entity to the <see cref="PipelineRunDto"/> the API and the
/// launcher return, for both the run LIST (server-side EF projection) and the run DETAIL (plain
/// in-memory mapping over the run, its pipeline and its steps; its results are summarised by the
/// database, recette R-484). Extracted from <see cref="PipelineRunHelpers"/>
/// (which keeps the smaller variable/condition/matrix helpers this class calls into) to stay under
/// the file-size budget - see <c>FileSizeAuditTests</c>.
/// </summary>
public static class PipelineRunDtoMapper
{
    // Recette R-263: the list row lives in Monitoring (the read-model module below this one); these
    // names stay so the run grids of this module keep reading the same row.
    public static readonly Expression<Func<PipelineRun, PipelineRunDto>> RunListProjection = PipelineRunListRows.Projection;

    public static PipelineRunDto MapRunToDto(PipelineRun r) => MapRunToDto(r, StageDepths(r));

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
    private static Dictionary<string, int> StageDepths(PipelineRun run)
    {
        var depths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(run.YamlSnapshot)) return depths;
        PipelineYamlDefinition? definition;
        try
        {
            definition = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(run.YamlSnapshot);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return depths;
        }
        if (definition is null) return depths;

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

    /// <summary>Recette R2-103: the author or reason a cancellation recorded on the run, null when none.</summary>
    private static string? CancellationNote(string? additionalVariablesJson, string variable) =>
        DeserializeResolvedVariables(additionalVariablesJson).TryGetValue(variable, out var value)
            && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static PipelineRunDto MapRunToDto(PipelineRun r, Dictionary<string, int> stageDepths) => new()
    {
        Id = r.Id,
        PipelineId = r.PipelineId,
        ProjectId = r.Pipeline?.ProjectId,
        PipelineName = r.Pipeline?.Name ?? string.Empty,
        Status = r.Status,
        CancellationRequested = HasCancellationRequest(r.AdditionalVariablesJson),
        CancelledBy = CancellationNote(r.AdditionalVariablesJson, PipelineRunService.CancelledByVariable),
        CancellationReason = CancellationNote(r.AdditionalVariablesJson, PipelineRunService.CancellationReasonVariable),
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
        Steps = r.StepRuns.Select(s => new PipelineStepRunDto
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
        }).ToList()
        // Recette R-484: tests, coverage, lint, metrics and artifacts are not mapped from loaded rows;
        // PipelineRunReader fills them from database-computed summaries (GetRunResultSummariesAsync,
        // GetTestResultSummaryAsync).
    };

    public static PipelineRunDto HydrateListWarnings(PipelineRunDto run) => PipelineRunListRows.HydrateWarnings(run);
}
