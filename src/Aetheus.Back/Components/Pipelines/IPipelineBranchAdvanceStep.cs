// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary> Executes a <c>type: advance-branch</c> step : the backend fast-forwards the step's
/// <c>branch</c> onto the commit of the release the run deploys, and completes or fails the step
/// with a readable reason. No agent task is created, like a <c>type: trigger</c> step. </summary>
public interface IPipelineBranchAdvanceStep
{
    /// <summary>Advances the branch and marks <paramref name="stepRun"/> Success or Failed.</summary>
    Task ExecuteAsync(
        int runId, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, CancellationToken ct);
}
