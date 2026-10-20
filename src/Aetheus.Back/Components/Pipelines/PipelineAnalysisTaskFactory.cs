// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Creates the agent tasks for the analysis-flavoured step types: the analysis gate, and the four
/// publish steps (observability bundle, coverage, lint, complexity).
/// </summary>
public interface IPipelineAnalysisTaskFactory
{
    /// <summary>
    /// Creates the analysis-gate task.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the task was created; otherwise the validation error the caller must fail the
    /// run with. The factory deliberately does not finalize the run itself: run finalization stays with
    /// the engine, so this stays a factory and nothing more.
    /// </returns>
    string? CreateAnalysisGateTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef);

    void CreateObservabilityTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, HashSet<string> secretKeys);

    void CreateCoverageTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef);

    void CreateLintTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef);

    void CreateComplexityTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef);

    void CreateMutationTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef);

    void CreateCollectArtifactsTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef);
}

/// <inheritdoc cref="IPipelineAnalysisTaskFactory"/>
public sealed class PipelineAnalysisTaskFactory(IPipelineStepTaskBuilder taskBuilder)
    : IPipelineAnalysisTaskFactory
{
    public string? CreateAnalysisGateTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef)
    {
        var scope = stepDef.AnalysisScope?.ToLowerInvariant();
        if (scope is not ("security" or "quality"))
            return $"Step '{stepDef.Name}': analysis_scope must be security or quality.";

        var variables = new Dictionary<string, string>(legVars, StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_RUN_ID"] = runId.ToString(),
            ["AETHEUS_STAGE_NAME"] = stageDef.Name
        };
        taskBuilder.TrackTypedTask(
            runId, legServer, stepRun, stepDef, scope, variables,
            OperationKind.PipelineEvaluateAnalysisGate);
        return null;
    }

    public void CreateObservabilityTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, HashSet<string> secretKeys)
    {
        var variables = ScopeStepEnvironment(legVars, secretKeys, stepDef);
        variables["AETHEUS_WORKING_DIR"] = legVars.GetValueOrDefault("WORKSPACE", "");
        taskBuilder.TrackTypedTask(
            runId, legServer, stepRun, stepDef, "optional-observability", variables,
            OperationKind.PipelinePublishObservabilityBundle);
    }

    public void CreateCoverageTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef)
    {
        var variables = CreateAnalysisVariables(runId, legVars, stageDef);
        if (stepDef.MinCoverage is { } minCoverage)
            variables["AETHEUS_MIN_COVERAGE"] =
                minCoverage.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(stepDef.CoverageTool))
            variables["AETHEUS_COVERAGE_TOOL"] = stepDef.CoverageTool;
        if (!string.IsNullOrWhiteSpace(stepDef.CoverageLanguage))
            variables["AETHEUS_COVERAGE_LANGUAGE"] = stepDef.CoverageLanguage;
        if (!string.IsNullOrWhiteSpace(stepDef.CoverageVersion))
            variables["AETHEUS_COVERAGE_VERSION"] = stepDef.CoverageVersion;
        var command = JsonSerializer.Serialize(
            stepDef.TargetFiles.Count > 0
                ? stepDef.TargetFiles
                : new List<string> { "**/coverage.cobertura.xml" });
        taskBuilder.TrackTypedTask(
            runId, legServer, stepRun, stepDef, command, variables, OperationKind.PipelinePublishCoverage);
    }

    public void CreateLintTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef)
    {
        var variables = CreateAnalysisVariables(runId, legVars, stageDef);
        if (LintAnalysisCategories.TryParse(stepDef.AnalysisCategory, out var category))
            variables[LintAnalysisCategories.VariableName] = category.ToString();
        var command = JsonSerializer.Serialize(
            stepDef.TargetFiles.Count > 0 ? stepDef.TargetFiles : new List<string> { "**/*.sarif" });
        taskBuilder.TrackTypedTask(
            runId, legServer, stepRun, stepDef, command, variables, OperationKind.PipelinePublishLint);
    }

    /// <summary>Publishes the Stryker mutation score as a metric.</summary>
    public void CreateMutationTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef)
    {
        var variables = CreateAnalysisVariables(runId, legVars, stageDef);
        var command = JsonSerializer.Serialize(
            stepDef.TargetFiles.Count > 0 ? stepDef.TargetFiles : new List<string> { "**/mutation-report.json" });
        taskBuilder.TrackTypedTask(
            runId, legServer, stepRun, stepDef, command, variables, OperationKind.PipelinePublishMutation);
    }

    /// <summary>Artifact collection runs as a step so the stage cannot finish before upload and
    /// downstream consumers cannot race it.</summary>
    public void CreateCollectArtifactsTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef)
    {
        var variables = CreateAnalysisVariables(runId, legVars, stageDef);
        variables["AETHEUS_ARTIFACT_NAME"] = stepDef.ArtifactName!.Trim();
        variables["AETHEUS_WORKSPACE_MODE"] = "process";
        var command = JsonSerializer.Serialize(
            stepDef.TargetFiles.Select(pattern => SubstituteVariables(pattern, legVars)).ToList());
        taskBuilder.TrackTypedTask(
            runId, legServer, stepRun, stepDef, command, variables, OperationKind.PipelineCollectArtifacts);
    }

    public void CreateComplexityTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef)
    {
        var variables = CreateAnalysisVariables(runId, legVars, stageDef);
        if (stepDef.MaxComplexity is { } maxComplexity)
            variables["AETHEUS_MAX_COMPLEXITY"] =
                maxComplexity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        taskBuilder.TrackTypedTask(
            runId, legServer, stepRun, stepDef, string.Empty, variables,
            OperationKind.PipelinePublishComplexity);
    }

    private static Dictionary<string, string> CreateAnalysisVariables(
        int runId, Dictionary<string, string> legVars, PipelineStageDefinition stageDef) =>
        new(legVars, StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_RUN_ID"] = runId.ToString(),
            ["AETHEUS_STAGE_NAME"] = stageDef.Name,
            ["AETHEUS_WORKING_DIR"] = legVars.GetValueOrDefault("WORKSPACE", "")
        };
}
