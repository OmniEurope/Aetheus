// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Builds the agent task of a <c>type: aetheus</c> step: one function of
/// <see cref="PipelineFunctionCatalog"/>, its <c>with:</c> values substituted and encoded.</summary>
public interface IPipelineFunctionTaskFactory
{
    /// <summary><c>null</c> when the task was created, or when a step allowed to fail (continue on
    /// error) was itself marked failed because a parameter kept an unresolved <c>$(NAME)</c>; otherwise
    /// the validation error the caller must fail the run with. Finalizing a run stays with the engine,
    /// so this stays a factory.</summary>
    string? CreateFunctionTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef);
}

/// <inheritdoc cref="IPipelineFunctionTaskFactory"/>
public sealed class PipelineFunctionTaskFactory(IPipelineStepTaskBuilder taskBuilder, TimeProvider timeProvider)
    : IPipelineFunctionTaskFactory
{
    public string? CreateFunctionTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef)
    {
        ArgumentNullException.ThrowIfNull(stepDef);

        // The definition was validated at save time; this re-check covers a definition stored before
        // the catalog changed, so a function the agent cannot resolve never becomes a task.
        return PipelineFunctionCatalog.Find(stepDef.Function) is { } function
            ? CreateTaskFor(function, runId, legServer, stepRun, stepDef, legVars, stageDef)
            : $"Step '{stepDef.Name}': '{stepDef.Function}' is not a known aetheus function.";
    }

    /// <summary>The task of an already-resolved function. Internal so the access-to-kind mapping and
    /// required-parameter checks are tested against descriptors the shipped catalog does not hold
    /// yet (a deployment function, a required parameter).</summary>
    internal string? CreateTaskFor(
        PipelineFunctionDescriptor function, int runId, Server legServer, PipelineStepRun stepRun,
        PipelineStepDefinition stepDef, Dictionary<string, string> legVars, PipelineStageDefinition stageDef)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(stepDef);
        ArgumentNullException.ThrowIfNull(legVars);
        ArgumentNullException.ThrowIfNull(stageDef);

        var error = ValidateParameters(stepDef, function);
        if (error is not null)
            return error;

        var parameters = stepDef.With.ToDictionary(
            pair => pair.Key, pair => SubstituteVariables(pair.Value, legVars), StringComparer.Ordinal);
        var unresolved = DescribeUnresolvedReferences(stepDef, parameters);
        if (unresolved is not null)
        {
            // A step that may not fail stops the run here, like any refused parameter. One that may
            // (continue_on_error, or every step of a run-to-end run) fails alone, as if it had run and
            // failed, so the run goes on; either way the reason names what was never produced.
            ArgumentNullException.ThrowIfNull(stepRun);
            if (!stepRun.ContinueOnError)
                return unresolved;
            MarkSystemStepFailed(
                stepRun, TaskFailureCodes.InfrastructureMismatch, unresolved, timeProvider.GetUtcNow().UtcDateTime);
            return null;
        }
        var variables = new Dictionary<string, string>(legVars, StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_RUN_ID"] = runId.ToString(CultureInfo.InvariantCulture),
            ["AETHEUS_STAGE_NAME"] = stageDef.Name,
            [PipelineFunctionVariables.ParametersJson] = PipelineFunctionVariables.SerializeParameters(parameters)
        };

        // The function name travels as the task target, so the target validator checks it against
        // the catalog and the task kind, and the access level cannot be forged downstream. A
        // container-isolated stage hands its container to the task: the agent runs the function inside
        // it (aetheus-fn), never on the host, so the isolation holds and the function sees the
        // container's files.
        taskBuilder.TrackTypedTask(
            runId, legServer, stepRun, stepDef, function.Name, variables, function.Operation,
            stageDef.Isolation);
        return null;
    }

    /// <summary>A <c>$(NAME)</c> still present after substitution names a variable nothing defined, for
    /// instance the output of a step that failed. A function parameter is a value, never a shell
    /// expression, so handing the literal text on would make the function act on a name that does not
    /// exist (mirror CI run 1272 pruned the Buildx builder "$(CI_BUILDX_BUILDER)").</summary>
    private static string? DescribeUnresolvedReferences(
        PipelineStepDefinition stepDef, Dictionary<string, string> parameters)
    {
        var unresolved = parameters
            .SelectMany(pair => PipelineCommandBuilder.ReferencedVariableNames(pair.Value)
                .Select(name => $"'{pair.Key}' references $({name})"))
            .ToList();
        return unresolved.Count == 0
            ? null
            : $"Step '{stepDef.Name}': function '{stepDef.Function}' was not dispatched because a parameter " +
              $"references a variable that is not defined at this point of the run ({string.Join(", ", unresolved)}). " +
              "Check that the step producing it ran and published it.";
    }

    private static string? ValidateParameters(PipelineStepDefinition stepDef, PipelineFunctionDescriptor function)
    {
        var unknown = stepDef.With.Keys.FirstOrDefault(key => function.FindParameter(key) is null);
        if (unknown is not null)
            return $"Step '{stepDef.Name}': function '{function.Name}' has no parameter '{unknown}'.";
        var missing = function.Parameters.FirstOrDefault(parameter =>
            parameter.Required
            && (!stepDef.With.TryGetValue(parameter.Name, out var value) || string.IsNullOrWhiteSpace(value)));
        return missing is null
            ? null
            : $"Step '{stepDef.Name}': function '{function.Name}' needs the parameter '{missing.Name}'.";
    }
}
