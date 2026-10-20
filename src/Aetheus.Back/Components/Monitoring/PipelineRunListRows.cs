// SPDX-License-Identifier: EUPL-1.2
using System.Linq.Expressions;
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Monitoring;

/// <summary> The run LIST row every run grid shows (EF projection, list warnings), read straight
/// from the shared <see cref="PipelineRun"/> entity. It lives in this read-model module (L1) so the
/// dashboard's recent runs and the Pipelines module (L6, which forwards to it) read the same row
/// without Monitoring reaching up into the orchestrator. Only pure JSON reads and entity fields
/// here: nothing that runs a pipeline. </summary>
public static class PipelineRunListRows
{
    // Server-side EF projection for the run LIST: scalar run fields + step summary chips only.
    // Deliberately omits the heavy per-run payload (YamlSnapshot, resolved variables, step commands,
    // output variables, artifacts) that the run detail loads - output variables especially must not
    // flow through the list path, which bypasses secret masking.
    public static readonly Expression<Func<PipelineRun, PipelineRunDto>> Projection = r => new PipelineRunDto
    {
        Id = r.Id,
        PipelineId = r.PipelineId,
        ProjectId = r.Pipeline != null ? r.Pipeline.ProjectId : null,
        PipelineName = r.Pipeline != null ? r.Pipeline.Name : string.Empty,
        Status = r.Status,
        RunToEnd = r.RunToEnd,
        ProjectedWarningsJson = r.WarningsJson,
        // Only an active run can still be waiting. A finished run keeps the column (it is the last
        // thing the planner observed, useful post-mortem in the database) but must never show it as a
        // current wait, so both projections drop it on a terminal status rather than clearing the row
        // from every path that can end a run.
        WaitingReason = r.Status == PipelineStatus.Running || r.Status == PipelineStatus.Pending
            ? r.WaitingReason : null,
        WaitingSince = r.Status == PipelineStatus.Running || r.Status == PipelineStatus.Pending
            ? r.WaitingSince : null,
        StartedAt = r.StartedAt,
        CompletedAt = r.CompletedAt,
        BranchName = r.BranchName,
        CommitHash = r.CommitHash,
        RepositoryUrl = r.RepositoryUrl,
        ProjectName = r.Pipeline != null && r.Pipeline.Project != null ? r.Pipeline.Project.Name : null,
        // Worst letter wins: the enum is ordered A=0..F=5, so Max is the most severe grade the run
        // earned. Read from the stored evaluations rather than recomputing the gate, which would mean
        // one gate computation per listed row.
        // A run with no evaluation of its own (a candidate delegating every analysis to a child, a
        // trigger orchestrator with no gate of its own, or a deploy run) is completed by
        // PipelineRunGradeAggregation.ApplyAsync after materialisation (see there).
        GateGrade = r.AnalysisEvaluations
            .Where(evaluation => evaluation.Grade != null)
            .Max(evaluation => evaluation.Grade),
        Steps = r.StepRuns.OrderBy(s => s.Order).Select(s => new PipelineStepRunDto
        {
            Id = s.Id,
            StepName = s.StepName,
            StageName = s.StageName,
            Status = s.Status,
            ServerId = s.ServerId,
            ServerName = s.Server != null ? s.Server.Name : null,
            ServerOs = s.Server != null ? s.Server.OsDescription : null,
            StartedAt = s.StartedAt,
            CompletedAt = s.CompletedAt,
            ExitCode = s.ExitCode,
            // The category only, same precedence as the detail (the task's own report first). The
            // reason text stays detail-only: it can quote a substituted value, and only the detail
            // path masks it. IsAdvisory needs the YAML snapshot: PipelineAdvisorySteps.ApplyToListAsync.
            FailureCode = s.Task != null && s.Task.FailureCode != null ? s.Task.FailureCode : s.FailureCode,
            RetryCount = s.RetryCount,
            ContinueOnError = s.ContinueOnError,
            MatrixLeg = s.MatrixLeg,
            TaskId = s.TaskId,
            IsSystem = s.IsSystem,
            GroupName = s.GroupName,
            SkippedCondition = s.SkippedCondition,
            SkippedConditionVariables = DeserializeResolvedVariables(s.SkippedConditionVariablesJson),
            SkippedReason = s.SkippedReason,
            TriggeredRunId = s.TriggeredRunId
        }).ToList()
    };

    // Grade completion for a list-projected run (no evaluation of its own) is NOT done here: the
    // projection omits Steps.OutputVariables (secret-masking boundary), so callers that need the
    // completed grade run PipelineRunGradeAggregation.ApplyAsync afterwards.
    public static PipelineRunDto HydrateWarnings(PipelineRunDto run)
        => run with
        {
            Warnings = DeserializeWarnings(run.ProjectedWarningsJson),
            ProjectedWarningsJson = null
        };

    public static Dictionary<string, string> DeserializeResolvedVariables(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "{}")
            return [];

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
    }

    public static List<string> DeserializeWarnings(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return [];

        return (JsonSerializer.Deserialize<List<string>>(json) ?? [])
            .Where(warning => !warning.StartsWith(
                "Cancellation requested; always() teardown stages remain mandatory",
                StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
