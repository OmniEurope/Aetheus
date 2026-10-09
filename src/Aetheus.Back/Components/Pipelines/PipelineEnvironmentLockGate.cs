// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// A run that acts on an environment holds it alone; another run that needs it waits (decision of
/// 2026-10-02). The lock is taken by the first stage that names an environment or deploys, for every
/// environment the run declares at once, so two runs can never each hold half of what they need.
/// It is taken after the approval gate: a run waiting for a human does not keep others out.
/// Nothing releases it but the end of the run (<see cref="PipelineRunFinalizer"/>), and a run that is
/// no longer active never blocks (<see cref="PipelineResourceLockRepository"/>), so an error, a
/// cancellation or a timeout frees it like a success does.
/// </summary>
internal sealed class PipelineEnvironmentLockGate(IPipelineRepository repo, IPipelineResourceLockRepository locks)
{
    /// <summary>The fragment every lock wait carries; the stuck-run sweep spares runs that show it.</summary>
    internal const string WaitMarker = "waits for environment '";

    internal static string Key(int environmentId) => $"environment:{environmentId}";

    /// <returns>Null when the stage may go on, otherwise the sentence the run shows while it waits.</returns>
    public async Task<string?> TryHoldAsync(
        int runId, int? projectId, PipelineStageDefinition stage, IReadOnlyList<PipelineStageDefinition> runStages,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stage.Environment) && !PipelineStageApprovalGate.IsDeploymentStage(stage))
            return null;

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in runStages.Select(s => s.Environment)
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .Select(n => n!.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // BACK-018: the run's project's environment, the one its approvals were asked for.
            var environment = await repo.FindEnvironmentByNameAsync(name, projectId, ct).ConfigureAwait(false);
            if (environment is not null) names[Key(environment.Id)] = environment.Name;
        }
        if (names.Count == 0) return null;

        var holder = await locks.TryAcquireAsync(runId, names.Keys.ToList(), ct).ConfigureAwait(false);
        if (holder is null) return null;
        var environmentName = names.GetValueOrDefault(holder.Key, holder.Key);
        return holder.RunId == 0
            ? $"Stage '{stage.Name}' {WaitMarker}{environmentName}', just taken by another run."
            : $"Stage '{stage.Name}' {WaitMarker}{environmentName}', held by {holder.PipelineName} #{holder.BuildNumber} (run {holder.RunId}).";
    }
}
