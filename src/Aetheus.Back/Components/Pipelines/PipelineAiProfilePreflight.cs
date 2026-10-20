// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AiTasks;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>What the AI profile preflight found: refusals for the AI steps whose result counts, warnings
/// for the advisory ones, which never decide anything.</summary>
public sealed record PipelineAiProfilePreflightResult(IReadOnlyList<string> Blocking, IReadOnlyList<string> Warnings)
{
    public static readonly PipelineAiProfilePreflightResult None = new([], []);
}

/// <summary>
/// Anticipates, at launch, the profile check an <c>ai</c> step meets at dispatch: when no AI runner
/// profile of the pipeline's organization can run (none exists, or none has a provider, model and API
/// key), nothing can fix itself while the run builds, so a run would spend every stage before it to fail
/// on it. A step's <c>profile:</c> is optional and a profile that cannot be used is replaced by another
/// usable one at dispatch (<c>AiProfileResolver</c>), so only the absence of any usable profile refuses.
/// The pipeline's assigned profile never changes that outcome (when it is usable, the organization has a
/// usable profile anyway), which is why this check does not need it.
/// </summary>
public interface IPipelineAiProfilePreflight
{
    Task<PipelineAiProfilePreflightResult> FindProblemsAsync(
        PipelineYamlDefinition definition, int? organizationId, CancellationToken ct);
}

/// <summary>
/// An ordinary AI step with an unusable profile refuses the launch, exactly as the dispatcher would fail
/// the run. An advisory one only warns: its dispatch records the step as not run and the run carries on,
/// so refusing the whole launch for an opinion would be stricter than the run itself. The dispatch-time
/// check stays as it is (a profile can still be deleted while the run is in flight).
/// </summary>
public sealed class PipelineAiProfilePreflight(IAiTaskService aiTasks) : IPipelineAiProfilePreflight
{
    public async Task<PipelineAiProfilePreflightResult> FindProblemsAsync(
        PipelineYamlDefinition definition, int? organizationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var aiSteps = YamlParsingHelper.FlattenJobs(definition)
            .SelectMany(stage => stage.Steps
                .Where(step => string.Equals(step.Type, "ai", StringComparison.OrdinalIgnoreCase))
                .Select(step => (Stage: stage.Name, Step: step)))
            .ToList();
        if (aiSteps.Count == 0) return PipelineAiProfilePreflightResult.None;

        var blocking = new List<string>();
        var warnings = new List<string>();
        var problemsByProfile = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (stage, step) in aiSteps)
        {
            var problem = await FindProblemAsync(step.Profile, organizationId, problemsByProfile, ct)
                .ConfigureAwait(false);
            if (problem is null) continue;
            var message = $"AI step '{step.Name}' in stage '{stage}': {problem}";
            if (step.Advisory)
                warnings.Add($"{message} The advisory step will be recorded as not run; the run is not blocked.");
            else
                blocking.Add(message);
        }
        return new PipelineAiProfilePreflightResult(blocking, warnings);
    }

    private async Task<string?> FindProblemAsync(
        string? profile, int? organizationId, Dictionary<string, string?> cache, CancellationToken ct)
    {
        if (organizationId is not { } id)
            return "the pipeline belongs to no organization, so no AI runner profile can be resolved.";
        var key = profile ?? string.Empty;
        if (cache.TryGetValue(key, out var known)) return known;
        var problem = await aiTasks.FindPipelineProfileProblemAsync(profile, id, ct).ConfigureAwait(false);
        cache[key] = problem;
        return problem;
    }
}
