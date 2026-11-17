// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Recette R-25: whether a step will run, read from its stage and step conditions on the variables a launch resolves,
/// with the engine's own <see cref="PipelineConditionEvaluator"/>. Only a comparison on a value that is known and cannot
/// change during the run decides <see cref="No"/>; anything else (a status function, a value a step produces, an
/// unresolved reference, an unsupported condition) is <see cref="Unknown"/> and the caller keeps the finding: a real risk
/// is never hidden because it could not be evaluated.
/// </summary>
internal static class PipelineStepReach
{
    /// <summary>True when the stage or the step is certain not to run on <paramref name="variables"/>.</summary>
    public static bool NeverRuns(
        PipelineStageDefinition stage, PipelineStepDefinition step,
        IReadOnlyDictionary<string, string> variables, IReadOnlySet<string> deferred)
    {
        var scoped = new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in stage.Variables) scoped[key] = value;
        return IsFalse(stage.Condition, scoped, deferred) || IsFalse(step.Condition, scoped, deferred);
    }

    /// <summary>The pipelines a definition triggers only from steps that are certain not to run on
    /// <paramref name="variables"/>. A pipeline that one step that may run also triggers is not in the set.</summary>
    public static IReadOnlySet<string> TriggeredOnlyByStepsThatNeverRun(
        PipelineYamlDefinition definition, IReadOnlyDictionary<string, string> variables, IReadOnlySet<string> deferred)
    {
        var triggers = YamlParsingHelper.FlattenJobs(definition)
            .SelectMany(stage => stage.Steps
                .Where(step => string.Equals(step.Type, "trigger", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(step.Pipeline))
                .Select(step => (Name: (step.Pipeline ?? string.Empty).Trim(), Skipped: NeverRuns(stage, step, variables, deferred))))
            .ToList();
        var mayRun = triggers.Where(trigger => !trigger.Skipped).Select(trigger => trigger.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return triggers.Where(trigger => trigger.Skipped && !mayRun.Contains(trigger.Name)).Select(trigger => trigger.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsFalse(string? condition, Dictionary<string, string> variables, IReadOnlySet<string> deferred)
    {
        if (PipelineConditionEvaluator.FindComparedVariable(condition) is not { } name) return false;
        if (deferred.Contains(name)
            || !variables.TryGetValue(name, out var value)
            || value.Contains("$(", StringComparison.Ordinal))
            return false;
        var verdict = PipelineConditionEvaluator.Evaluate(condition, variables, previousStageFailed: false);
        return !verdict.IsInvalid && !verdict.Met;
    }
}
