// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Builds a gate-status task only for a well-formed gate so the agent cannot pass an empty
/// or meaningless aggregation.</summary>
public interface IPipelineGateStatusTaskFactory
{
    /// <summary><c>null</c> when the task was created; otherwise the validation error the caller must
    /// fail the run with. Finalizing a run stays with the engine, so this stays a factory.</summary>
    string? CreateGateStatusTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef);
}

/// <inheritdoc cref="IPipelineGateStatusTaskFactory"/>
public sealed class PipelineGateStatusTaskFactory(IPipelineStepTaskBuilder taskBuilder)
    : IPipelineGateStatusTaskFactory
{
    public string? CreateGateStatusTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef)
    {
        ArgumentNullException.ThrowIfNull(stepDef);
        ArgumentNullException.ThrowIfNull(legVars);
        ArgumentNullException.ThrowIfNull(stageDef);

        // A gate over nothing passes unconditionally, and in the run's history that is indis-
        // tinguishable from a gate that held. Refusing at dispatch is the last place the difference
        // is still visible to anyone.
        var names = stepDef.StatusVariables
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToList();
        if (names.Count == 0)
            return $"Step '{stepDef.Name}': a gate-status step needs at least one entry in "
                + "'status_variables'; a gate over nothing always passes.";

        foreach (var name in names)
            if (!PipelineRunVariableName.IsValid(name))
                return $"Step '{stepDef.Name}': '{name}' is not a run variable name (upper-case "
                    + "letters, digits and underscores).";

        if (!string.IsNullOrWhiteSpace(stepDef.PublishStatusAs)
            && !PipelineRunVariableName.IsValid(stepDef.PublishStatusAs))
            return $"Step '{stepDef.Name}': 'publish_status_as' must be a run variable name.";

        var variables = new Dictionary<string, string>(legVars, StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_RUN_ID"] = runId.ToString(CultureInfo.InvariantCulture),
            ["AETHEUS_STAGE_NAME"] = stageDef.Name,
            [PipelineGateStatusVariables.StatusVariables] = string.Join(',', names),
            [PipelineGateStatusVariables.Blocking] = stepDef.Blocking ? "true" : "false",
            [PipelineGateStatusVariables.Label] =
                string.IsNullOrWhiteSpace(stepDef.GateLabel) ? stepDef.Name : stepDef.GateLabel
        };
        if (!string.IsNullOrWhiteSpace(stepDef.PublishStatusAs))
            variables[PipelineGateStatusVariables.PublishAs] = stepDef.PublishStatusAs;

        taskBuilder.TrackTypedTask(
            runId, legServer, stepRun, stepDef, string.Empty, variables,
            OperationKind.PipelineGateStatus);
        return null;
    }
}
