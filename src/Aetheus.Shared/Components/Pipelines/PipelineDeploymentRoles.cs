// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// Recette R-14: the values of a stage's <c>deployment_role</c>. A stage that deploys or undoes a
/// deployment by a script, rather than by the typed <c>bluegreen-up</c> / <c>bluegreen-rollback</c>
/// steps, declares it so the run is judged like a typed one: a run whose started deployment was undone
/// by its rollback stage finishes RolledBack, a cancelled run still runs its rollback stage, and the
/// rollback stage is skipped when no deployment stage started.
/// </summary>
public static class PipelineDeploymentRoles
{
    /// <summary>The stage starts the new deployment (what a <c>bluegreen-up</c> step does).</summary>
    public const string Start = "start";

    /// <summary>The stage undoes the deployment (what a <c>bluegreen-rollback</c> step does); it must be a
    /// failure handler, <c>condition: failed()</c>.</summary>
    public const string Rollback = "rollback";

    public static IReadOnlyList<string> All { get; } = [Start, Rollback];
}
