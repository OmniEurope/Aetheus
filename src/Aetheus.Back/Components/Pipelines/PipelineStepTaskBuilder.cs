// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Turns one pipeline step into the agent task that executes it, and hands that task to the
/// repository's change tracker. Every step factory goes through here, which is what keeps the
/// environment protection and the "step is now dispatched" bookkeeping in a single place.
/// </summary>
public interface IPipelineStepTaskBuilder
{
    /// <summary>
    /// Builds the task without tracking it. For the few callers that still have to decorate the task
    /// (a scanner lease, an isolation policy) before it is handed over.
    /// </summary>
    ServerTask BuildPipelineTask(
        int runId, Server server, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        string command, IReadOnlyDictionary<string, string> variables, OperationKind operation);

    /// <summary>Builds, tracks and marks the step dispatched, for a typed (non-shell) operation. With a
    /// container <paramref name="isolation"/> the task carries the stage container, so the agent runs
    /// the operation inside it (a <c>type: aetheus</c> function); without one it runs on the host.</summary>
    void TrackTypedTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        string command, Dictionary<string, string> variables, OperationKind operation,
        PipelineIsolationDefinition? isolation = null);

    /// <summary>
    /// The ordinary shell step: builds the command for the target OS, scopes the environment to what
    /// the step declared, applies the stage's container isolation, then tracks it.
    /// </summary>
    void CreateCommandTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef, bool stageIsContainer,
        HashSet<string> secretKeys);
}

/// <inheritdoc cref="IPipelineStepTaskBuilder"/>
public sealed class PipelineStepTaskBuilder(
    IPipelineRepository repo,
    IEncryptionService encryption,
    ILogger<PipelineStepTaskBuilder> logger) : IPipelineStepTaskBuilder
{
    public ServerTask BuildPipelineTask(
        int runId, Server server, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        string command, IReadOnlyDictionary<string, string> variables, OperationKind operation)
    {
        stepRun.ServerId = server.Id;
        return new ServerTask
        {
            ServerId = server.Id,
            Name = stepRun.StepName,
            Command = command,
            PipelineRunId = runId,
            PipelineStepRunId = stepRun.Id,
            EnvironmentVariables = TaskEnvProtection.Protect(
                encryption, JsonSerializer.Serialize(variables)),
            TimeoutSeconds = stepDef.TimeoutSeconds,
            Operation = operation
        };
    }

    public void TrackTypedTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        string command, Dictionary<string, string> variables, OperationKind operation,
        PipelineIsolationDefinition? isolation = null)
    {
        var task = BuildPipelineTask(
            runId, legServer, stepRun, stepDef, command, variables, operation);
        foreach (var warning in ApplyContainerIsolation(task, isolation))
            logger.LogWarning("Run {RunId} step '{Step}': {Warning}", runId, stepRun.StepName, warning);
        repo.TrackTask(task);
        MarkStepDispatched(stepRun, task);
    }

    public void CreateCommandTask(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef, bool stageIsContainer,
        HashSet<string> secretKeys)
    {
        var command = PipelineCommandBuilder.BuildStepCommand(
            stepDef,
            legVars,
            OsTypeHelper.IsWindows(legServer.OsType, legServer.OsDescription) && !stageIsContainer,
            secretKeys);
        var environment = ScopeStepEnvironment(legVars, secretKeys, stepDef);
        var task = BuildPipelineTask(
            runId, legServer, stepRun, stepDef, command, environment, OperationKind.None);
        foreach (var warning in ApplyContainerIsolation(task, stageDef.Isolation))
            logger.LogWarning("Run {RunId} step '{Step}': {Warning}", runId, stepRun.StepName, warning);
        repo.TrackTask(task);
        MarkStepDispatched(stepRun, task);
    }
}
