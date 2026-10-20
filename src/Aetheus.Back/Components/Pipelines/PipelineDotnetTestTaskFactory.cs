// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Builds the agent's dotnet-test task with its project, evidence destination and
/// classified-status output variable.</summary>
public interface IPipelineDotnetTestTaskFactory
{
    /// <summary><c>null</c> when the task was created; otherwise the validation error the caller must
    /// fail the run with. Finalizing a run stays with the engine, so this stays a factory.</summary>
    string? CreateDotnetTestTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef);
}

/// <inheritdoc cref="IPipelineDotnetTestTaskFactory"/>
public sealed class PipelineDotnetTestTaskFactory(IPipelineStepTaskBuilder taskBuilder)
    : IPipelineDotnetTestTaskFactory
{
    public string? CreateDotnetTestTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef)
    {
        ArgumentNullException.ThrowIfNull(stepDef);
        ArgumentNullException.ThrowIfNull(legVars);
        ArgumentNullException.ThrowIfNull(stageDef);

        if (string.IsNullOrWhiteSpace(stepDef.TestProject))
            return $"Step '{stepDef.Name}': a dotnet-test step needs a 'test_project'.";
        if (string.IsNullOrWhiteSpace(stepDef.ResultsDirectory))
            return $"Step '{stepDef.Name}': a dotnet-test step needs a 'results_directory'.";
        if (string.IsNullOrWhiteSpace(stepDef.StatusVariable))
            return $"Step '{stepDef.Name}': a dotnet-test step needs a 'status_variable', which is "
                + "the run variable its gate reads.";

        var variables = new Dictionary<string, string>(legVars, StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_RUN_ID"] = runId.ToString(CultureInfo.InvariantCulture),
            ["AETHEUS_STAGE_NAME"] = stageDef.Name,
            [PipelineDotnetTestVariables.ResultsDirectory] = stepDef.ResultsDirectory,
            [PipelineDotnetTestVariables.StatusVariable] = stepDef.StatusVariable,
            [PipelineDotnetTestVariables.CollectCoverage] =
                stepDef.CollectCoverage ? "true" : "false"
        };
        AddOptionalSettings(variables, stepDef);
        AddToolchain(variables, stepDef, legVars);

        // The project path travels as the task target rather than in the environment, so it goes
        // through the same target validation as every other typed step.
        taskBuilder.TrackTypedTask(
            runId, legServer, stepRun, stepDef, stepDef.TestProject, variables,
            OperationKind.PipelineDotnetTest);
        return null;
    }

    private static void AddOptionalSettings(Dictionary<string, string> variables, PipelineStepDefinition stepDef)
    {
        if (!string.IsNullOrWhiteSpace(stepDef.TrxName))
            variables[PipelineDotnetTestVariables.TrxName] = stepDef.TrxName;
        if (!string.IsNullOrWhiteSpace(stepDef.RunSettings))
            variables[PipelineDotnetTestVariables.RunSettings] = stepDef.RunSettings;
        if (!string.IsNullOrWhiteSpace(stepDef.Configuration))
            variables[PipelineDotnetTestVariables.Configuration] = stepDef.Configuration;
    }

    /// <summary>The pinned SDK and tool directories, usually step outputs such as <c>$(DOTNET)</c>,
    /// substituted now. A reference left unresolved stays literal, so the agent refuses it as a path
    /// instead of silently falling back to the host's toolchain.</summary>
    private static void AddToolchain(
        Dictionary<string, string> variables, PipelineStepDefinition stepDef, Dictionary<string, string> legVars)
    {
        if (!string.IsNullOrWhiteSpace(stepDef.Dotnet))
            variables[PipelineDotnetTestVariables.DotnetPath] = SubstituteVariables(stepDef.Dotnet, legVars).Trim();
        if (stepDef.ToolPaths.Count > 0)
            variables[PipelineDotnetTestVariables.ToolPaths] = string.Join(
                '\n', stepDef.ToolPaths.Select(path => SubstituteVariables(path, legVars).Trim()));
    }
}
