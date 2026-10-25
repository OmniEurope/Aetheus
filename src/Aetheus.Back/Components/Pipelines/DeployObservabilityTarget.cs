// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The project and environment whose monitored application receives a deploy's telemetry. The stage's
/// <c>environment</c> names it: a project with one monitored application per environment (prod and
/// demo) otherwise has no unambiguous target, the provider derives nothing, and the step that starts
/// the colour refuses the telemetry names its <c>compose_env</c> lists (deploy-prod #132, 2026-10-22).
/// </summary>
internal static class DeployObservabilityTarget
{
    internal static async Task<(int ProjectId, int? EnvironmentId)?> ResolveAsync(
        IPipelineRepository repo, int runId, string? environmentName, CancellationToken ct)
    {
        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        var projectId = run?.Pipeline is null
            ? null
            : await repo.GetPipelineProjectIdAsync(run.Pipeline, ct).ConfigureAwait(false);
        if (projectId is null) return null;
        if (string.IsNullOrWhiteSpace(environmentName)) return (projectId.Value, null);
        var environment = await repo
            .FindEnvironmentByNameForProjectAsync(environmentName.Trim(), projectId.Value, ct).ConfigureAwait(false);
        return (projectId.Value, environment?.Id);
    }
}
