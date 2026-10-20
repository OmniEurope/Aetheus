// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Acquires all declared environment locks after approval to avoid partial-lock deadlocks
/// and blocking other runs during human review (ADR-049). Locks remain effective only while their
/// run is active.</summary>
internal sealed class PipelineEnvironmentLockGate(IPipelineEnvironmentReader repo, IPipelineResourceLockRepository locks)
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
