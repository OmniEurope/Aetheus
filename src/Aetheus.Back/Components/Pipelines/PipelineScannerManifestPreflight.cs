// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.Tasks;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Scanner preflight requires an agent manifest matching the backend before launching
/// candidate work, so incompatible runners are detected before scanner dispatch.</summary>
public interface IPipelineScannerManifestPreflight
{
    /// <summary>
    /// Null when the pipeline's scanner steps do not need the candidate manifest, or when every stage
    /// holding one has at least one configured runner whose last report carries it; otherwise one
    /// readable refusal naming both hashes and each runner.
    /// </summary>
    Task<string?> FindProblemAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolvedVariables,
        string? upstreamPipeline,
        string? upstreamTemplate,
        int? organizationId,
        int? projectId,
        CancellationToken ct);
}

/// <summary>
/// Refuses rather than warns: the manifest a runner reports only changes when its agent is updated,
/// which is an explicit fleet operation (ADR-035) nobody performs in the middle of a candidate, so a
/// launch no runner can serve is certain to fail at its first scanner step.
///
/// Like the runner check it follows, an offline runner is not held against the launch: a runner whose
/// last report carries the expected manifest satisfies the stage whether it is online right now or not.
/// </summary>
public sealed class PipelineScannerManifestPreflight(
    IPipelineRunnerTargetReader repo,
    IPipelineDispatchServerResolver dispatchServers) : IPipelineScannerManifestPreflight
{
    public async Task<string?> FindProblemAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolvedVariables,
        string? upstreamPipeline,
        string? upstreamTemplate,
        int? organizationId,
        int? projectId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(resolvedVariables);
        if (!PipelineScannerTaskFactory.RequiresCandidateScannerManifest(upstreamPipeline, upstreamTemplate)) return null;

        var scannerStages = YamlParsingHelper.FlattenJobs(definition)
            .Where(stage => stage.Steps.Any(step =>
                string.Equals(step.Type, "scanner", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (scannerStages.Count == 0) return null;

        var expected = ScannerManifestCatalog.Sha256;
        var candidatesBySelector = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal);
        var unservedStages = new List<string>();
        var unservedRunners = new Dictionary<int, PipelineRunnerFacts>();
        foreach (var stage in scannerStages)
        {
            var candidates = await FindCandidatesAsync(
                stage, resolvedVariables, organizationId, projectId, candidatesBySelector, ct).ConfigureAwait(false);
            // No configured runner at all is the runner check's refusal, not this one's.
            if (candidates.Count == 0) continue;
            var facts = await repo.GetRunnerFactsAsync(candidates, ct).ConfigureAwait(false);
            if (facts is not { Count: > 0 }
                || facts.Any(runner => string.Equals(ReportedManifest(runner), expected, StringComparison.Ordinal)))
                continue;
            unservedStages.Add(stage.Name);
            foreach (var runner in facts) unservedRunners[runner.Id] = runner;
        }
        if (unservedStages.Count == 0) return null;

        return $"Scanner steps of stage(s) '{string.Join("', '", unservedStages)}' of pipeline "
            + $"'{definition.Name}' need an agent reporting scanner manifest {expected} (the one this backend "
            + "embeds), and no eligible runner reports it: "
            + string.Join("; ", unservedRunners.Values.OrderBy(runner => runner.Name, StringComparer.Ordinal)
                .Select(DescribeRunner))
            + ". The run would fail at its first scanner step. Update the agent from its server page "
            + "(Update agent), then launch again once it reports the expected manifest.";
    }

    private async Task<IReadOnlyCollection<int>> FindCandidatesAsync(
        PipelineStageDefinition stage,
        IReadOnlyDictionary<string, string> resolvedVariables,
        int? organizationId,
        int? projectId,
        Dictionary<string, IReadOnlyCollection<int>> cache,
        CancellationToken ct)
    {
        var effective = dispatchServers.ResolveEffectiveStageTarget(stage, resolvedVariables);
        var deploymentStage = dispatchServers.StageHasDeployStep(stage);
        var key = string.Join(
            "|",
            effective.Pool, effective.Environment, effective.Agent, effective.Os,
            organizationId?.ToString(CultureInfo.InvariantCulture), projectId?.ToString(CultureInfo.InvariantCulture), deploymentStage);
        if (cache.TryGetValue(key, out var cached)) return cached;
        var ids = await repo.FindCandidateTargetServerIdsAsync(
            effective.Pool, effective.Environment, effective.Agent,
            OsTypeHelper.Parse(effective.Os), organizationId, deploymentStage, projectId, ct).ConfigureAwait(false);
        cache[key] = ids;
        return ids;
    }

    private static string? ReportedManifest(PipelineRunnerFacts runner) =>
        TaskRepository.ExtractScannerManifestSha256(runner.ScannerCapabilitiesJson);

    private static string DescribeRunner(PipelineRunnerFacts runner)
    {
        var presence = runner.Status == ServerStatus.Online ? string.Empty : $" ({runner.Status})";
        return $"'{runner.Name}'{presence} reports {ReportedManifest(runner) ?? "no manifest"}";
    }
}
